using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig, ChangeHistoryRequest
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Connection half of <see cref="ToolGateway"/> (see ToolGateway.cs for the tool-call
    /// half): the socket/pipe lifecycle, response read loop, pending-request
    /// correlation, establishment serialization and the generation-checked write.
    /// Every wire command is gated by <see cref="RevitTargetBinding"/> (spec §6):
    /// a call pins the binding generation when it arrives, and the command is only
    /// written to the connection while the pinned generation still matches.
    /// </summary>
    internal static partial class ToolGateway
    {
        private sealed class Conn
        {
            public long Epoch;
            public long Generation;
            public Stream Stream;
            public StreamReader Reader;
            public StreamWriter Writer;
            public TcpClient Tcp;
            public NamedPipeClientStream Pipe;
            public string Token;
            public string TargetId;
            public string Year;
            public int Pid;
            public int Closed;
            public bool FailureProtocol;
            public bool DocumentGuard;
            /// <summary>
            /// Serializes wire writes on this connection so a blocked writer (plugin
            /// not reading) stalls only this call — never <c>_connectLock</c>, so
            /// switch_target and other callers stay responsive. Lock order is always
            /// <c>WriteLock → _connectLock</c>; close paths take only
            /// <c>_connectLock</c> and abort a blocked write by closing the stream.
            /// </summary>
            public readonly object WriteLock = new object();
            public bool Alive => Closed == 0 && (Tcp?.Connected == true || Pipe?.IsConnected == true);
        }

        /// <summary>Raised into a pending request when its connection closes mid-flight (§6.7).</summary>
        private sealed class ConnectionInterruptedException : Exception { }

        private static readonly ConcurrentDictionary<string, GatewayPendingRequest> _pending = new ConcurrentDictionary<string, GatewayPendingRequest>();
        private static readonly object _connectLock = new object();
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new McpJsonInput.JsonElementConverter() }
        };
        private static Conn _conn;
        private static long _nextEpoch;
        private static readonly SemaphoreSlim _establishGate = new SemaphoreSlim(1, 1);
        private static string _busyIdentity;
        private static long _busyUntil;
        internal static Action BeforeWriteForTests;

        /// <summary>Target id of the instance the open connection serves, or null.</summary>
        internal static string ConnectedTargetId => _conn is { Alive: true } c ? c.TargetId : null;

        /// <summary>PID of the instance the open connection serves, or null.</summary>
        internal static int? ConnectedPid => _conn is { Alive: true } c ? c.Pid : (int?)null;

        /// <summary>JIT warm-up for the wire paths: request serialization, pending map,
        /// response parse, plus a private 127.0.0.1 socket pair — no Revit listener is touched.</summary>
        internal static void WarmUpWire()
        {
            try
            {
                var wire = JsonConvert.SerializeObject(new
                {
                    id = "warmup",
                    command = "warmup",
                    @params = new { },
                    token = "warmup",
                    timeout_seconds = 1,
                    runtime = (object)null,
                    history = (object)null
                }, RequestJsonSettings);
                _ = wire.Length;
                _ = JObject.Parse("{\"id\":\"warmup\",\"success\":true,\"data\":{}}");
                _pending["warmup"] = new GatewayPendingRequest(null, 0);
                _pending.TryRemove("warmup", out _);

                // A loopback socket exercises the real connect path — TcpClient.Connect,
                // stream wrap, ReadLoop thread, WriteLine, close — without touching a
                // live listener (nothing ever accepts or answers).
                var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                    var accept = listener.AcceptTcpClientAsync();
                    var client = new TcpClient();
                    client.Connect("127.0.0.1", port);
                    var conn = NewConn(client, new Bimwright.Targeting.TargetDescriptor
                    {
                        TargetId = "warmup",
                        HostYear = 2024,
                        Transport = "tcp",
                        Port = port,
                        AuthToken = "warmup"
                    });
                    conn.Writer.WriteLine("{}");
                    var serverSide = accept.Result;
                    try { serverSide.GetStream().ReadByte(); } catch { }
                    CloseConn(conn);
                    serverSide.Dispose();
                }
                finally { listener.Stop(); }
            }
            catch { }
        }

        internal static bool ShouldSendToolCatalog(RvtMcp.ToolCatalog.ToolCatalog catalog, IReadOnlyList<string> capabilities)
        {
            return catalog != null && capabilities != null
                && capabilities.Any(value => string.Equals(value, "tool_catalog", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Ensure a live connection for <paramref name="pinned"/> exists; returns it with
        /// the generation it serves. Throws <see cref="TargetException"/> for every
        /// failure (§6.10 payloads). Can block for seconds — callers should offload it.
        /// </summary>
        private static (Conn Conn, long EffectiveGeneration) EnsureConnectedFor(long pinned, TargetCallContext callCtx)
        {
            var cancellation = callCtx?.Cancellation ?? CancellationToken.None;
            cancellation.ThrowIfCancellationRequested();
            if (callCtx != null && callCtx.ConnectionDeadlineTicks == 0)
                callCtx.ConnectionDeadlineTicks = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
            lock (_connectLock)
                if (TryFastPath(pinned, callCtx, out var fast, out var fastGen)) return (fast, fastGen);

            // Serialize establishment: parallel calls must not open competing sockets
            // against a single-client listener (they would see our own TARGET_BUSY).
            int gateWait;
            try { gateWait = ConnectionWait(callCtx, 10000); }
            catch (TimeoutException) { throw new TargetException(RevitTargetBinding.Binding.ReportConnectFailure(pinned, ConnectFailure.Busy, Array.Empty<TargetCandidate>())); }
            if (!_establishGate.Wait(gateWait, cancellation))
                throw new TargetException(RevitTargetBinding.Binding.ReportConnectFailure(pinned, ConnectFailure.Busy, Array.Empty<TargetCandidate>()));
            try
            {
                lock (_connectLock)
                    if (TryFastPath(pinned, callCtx, out var fast, out var fastGen)) return (fast, fastGen);
                try { return EstablishLocked(pinned, callCtx); }
                catch (TimeoutException) { throw new TargetException(RevitTargetBinding.Binding.ReportConnectFailure(pinned, ConnectFailure.Busy, Array.Empty<TargetCandidate>())); }
            }
            finally
            {
                _establishGate.Release();
            }
        }

        private static int ConnectionWait(TargetCallContext context, int maximum)
        {
            context?.Cancellation.ThrowIfCancellationRequested();
            if (context == null || context.ConnectionDeadlineTicks == 0) return maximum;
            var left = (context.ConnectionDeadlineTicks - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (left <= 0) throw new TimeoutException("Connection establishment budget expired.");
            return (int)Math.Min(maximum, left);
        }

        /// <summary>
        /// Steady-state path (caller holds <c>_connectLock</c>): reuse the open connection only
        /// when it serves exactly the pinned generation. The open authenticated connection is
        /// proof of life, so no directory scan is needed.
        /// </summary>
        private static bool TryFastPath(long pinned, TargetCallContext callCtx, out Conn conn, out long effectiveGeneration)
        {
            conn = null;
            effectiveGeneration = 0;
            var current = _conn;
            if (current == null || !current.Alive) return false;
            if (pinned == 0)
            {
                // Captured in state none while a concurrent call established the first
                // binding: only that first binding (generation 1, no instance switch since)
                // may be adopted.
                if (current.Generation != 1)
                {
                    if (!RevitTargetBinding.Binding.CanSend(0, Array.Empty<Bimwright.Targeting.TargetCandidate>(), out var failure))
                        throw new TargetException(failure);
                    return false;
                }
                if (callCtx != null) callCtx.PinnedGeneration = 1;
                effectiveGeneration = 1;
                conn = current;
                return true;
            }
            if (current.Generation != pinned) return false;
            effectiveGeneration = pinned;
            conn = current;
            return true;
        }

        private static (Conn Conn, long EffectiveGeneration) EstablishLocked(long pinned, TargetCallContext callCtx)
        {
            // Connect path uses the title-free probe: MainWindowTitle costs an
            // EnumWindows sweep per pid; display paths fill titles separately.
            var scan = RevitTargetBinding.ScanNoTitles();
            var live = scan.Live;
            var binding = RevitTargetBinding.Binding;
            var plan = binding.PlanCall(pinned, live);
            if (plan is FailPlan fail)
                throw new TargetException(fail.Payload);
            var cp = (ConnectPlan)plan;
            var effectiveGeneration = cp.EffectiveGeneration;

            // Any open connection now is dead or serves another generation; it must be
            // released before connecting (the listener accepts a single client).
            lock (_connectLock)
            {
                CloseConnLocked(_conn);
                _conn = null;
            }

            TargetDescriptor desc;
            if (cp.Record != null)
            {
                // Reconnect to the bound instance: read *that* instance's descriptor —
                // per-instance file by target_id, else the legacy file with the same pid.
                // Absent → retry up to 2 s (listener restart window), then UNAVAILABLE
                // with the binding kept (§6.4 step 2).
                var deadline = DateTime.UtcNow.AddSeconds(2);
                do
                {
                    desc = AuthToken.ReadDescriptorFor(cp.Record.Pid, cp.Record.HostYear);
                    if (desc == null)
                    {
                        var wait = ConnectionWait(callCtx, 200);
                        if ((callCtx?.Cancellation ?? CancellationToken.None).WaitHandle.WaitOne(wait))
                            callCtx.Cancellation.ThrowIfCancellationRequested();
                    }
                } while (desc == null && DateTime.UtcNow < deadline);
                if (desc == null)
                    throw new TargetException(binding.ReportConnectFailure(
                        effectiveGeneration, ConnectFailure.Unavailable, live));
            }
            else
            {
                desc = cp.FirstCandidate.Descriptor;
            }

            var conn = ConnectAndHandshake(desc, effectiveGeneration, live);

            if (cp.FirstCandidate != null)
            {
                if (!binding.TryCommitFirst(pinned, cp.FirstCandidate, out var gen, out var commitFailure))
                {
                    CloseConn(conn);
                    throw new TargetException(commitFailure);
                }
                effectiveGeneration = gen;
                // A call that arrived in state none pins to the generation it established.
                if (callCtx != null && pinned == 0) callCtx.PinnedGeneration = gen;
            }

            conn.Generation = effectiveGeneration;
            lock (_connectLock)
            {
                // A switch_target may have run while we connected: never install a
                // connection for a generation that is no longer current.
                if (!binding.CanSend(effectiveGeneration, live, out var installFailure))
                {
                    CloseConn(conn);
                    throw new TargetException(installFailure);
                }
                var old = _conn;
                _conn = conn;
                if (old != conn) CloseConnLocked(old);
            }
            return (conn, effectiveGeneration);
        }

        /// <summary>
        /// switch_target entry point: the binding change and closing the old connection happen
        /// under one <c>_connectLock</c>, so no call captured under the new generation can reach
        /// the old instance's still-open connection (§6.6, §6.8).
        /// </summary>
        internal static SwitchResult SwitchBinding(TargetSelector selector, IReadOnlyList<Bimwright.Targeting.TargetCandidate> live,
            out BindingSnapshot before, bool rebind = false)
        {
            lock (_connectLock)
            {
                // Verification rollback must use the binding immediately before this switch.
                before = RevitTargetBinding.Snapshot();
                var result = RevitTargetBinding.Switch(selector, live, rebind);
                if (result.Ok && RevitTargetBinding.CaptureGeneration() != before.Generation)
                {
                    var conn = _conn;
                    _conn = null;
                    CloseConnLocked(conn); // pending requests end as TARGET_INTERRUPTED
                }
                return result;
            }
        }

        private enum HandshakeResult { Ok, AnsweredRejected, TokenRejected, TimedOut, Closed }

        internal static bool ConfirmDocument(long generation, JObject stamp)
        {
            lock (_connectLock)
            {
                var record = RevitTargetBinding.Snapshot().Record;
                if (record == null || record.Generation != generation || stamp?.Value<int?>("pid") != record.Pid
                    || stamp.Value<string>("target_id") != record.TargetId || _conn?.DocumentGuard != true
                    || !RevitTargetBinding.Binding.ConfirmDocument(generation, stamp.Value<string>("document_id"))) return false;
                var confirmed = RevitTargetBinding.CaptureGeneration();
                if (confirmed != generation) { var conn = _conn; _conn = null; CloseConnLocked(conn); }
                if (TargetCallContext.Current != null) TargetCallContext.Current.PinnedGeneration = confirmed;
                return true;
            }
        }

        internal static bool RestoreAfterFailedVerification(BindingSnapshot before, long provisionalGeneration)
        {
            lock (_connectLock)
            {
                if (!RevitTargetBinding.Binding.RestoreAfterFailedVerification(before, provisionalGeneration)) return false;
                var conn = _conn;
                _conn = null;
                CloseConnLocked(conn);
                if (TargetCallContext.Current != null) TargetCallContext.Current.PinnedGeneration = RevitTargetBinding.CaptureGeneration();
                return true;
            }
        }

        /// <summary>§6.4 connect: busy vs no-listener distinction, handshake precondition.</summary>
        private static Conn ConnectAndHandshake(TargetDescriptor desc, long effectiveGeneration,
            IReadOnlyList<Bimwright.Targeting.TargetCandidate> live)
        {
            var binding = RevitTargetBinding.Binding;
            var descriptor = desc;
            var tokenRetried = false;
            while (true)
            {
                var identity = descriptor.TargetId + "|" + descriptor.ProcessStartUtc?.Ticks + "|" + effectiveGeneration;
                lock (_connectLock)
                    if (_busyIdentity == identity && System.Diagnostics.Stopwatch.GetTimestamp() < _busyUntil)
                        throw new TargetException(binding.ReportConnectFailure(effectiveGeneration, ConnectFailure.Busy, live));
                var conn = OpenTransport(descriptor, out var failure);
                if (conn == null)
                {
                    if (failure == ConnectFailure.Busy) RememberBusy(identity);
                    throw new TargetException(binding.ReportConnectFailure(effectiveGeneration, failure, live));
                }

                if (!ShouldSendToolCatalog(ServerState.ToolCatalog, descriptor.Capabilities))
                    return conn; // no handshake to send — socket connect counts as success

                switch (Handshake(conn))
                {
                    case HandshakeResult.Ok:
                    case HandshakeResult.AnsweredRejected:
                        // A rejection that is not an auth failure still proves the listener
                        // answered within the handshake budget — the connection is live.
                        return conn;
                    case HandshakeResult.TimedOut:
                        RememberBusy(identity);
                        CloseConn(conn);
                        throw new TargetException(binding.ReportConnectFailure(
                            effectiveGeneration, ConnectFailure.Busy, live));
                    case HandshakeResult.Closed:
                        CloseConn(conn);
                        throw new TargetException(binding.ReportConnectFailure(
                            effectiveGeneration, ConnectFailure.Unavailable, live));
                    default: // TokenRejected
                        CloseConn(conn);
                        if (tokenRetried)
                            throw new TargetException(binding.ReportConnectFailure(
                                effectiveGeneration, ConnectFailure.Unavailable, live));
                        tokenRetried = true;
                        // Re-read that instance's descriptor once — the token may have rotated.
                        var fresh = AuthToken.ReadDescriptorFor(descriptor.Pid, descriptor.HostYear);
                        if (fresh == null)
                            throw new TargetException(binding.ReportConnectFailure(
                                effectiveGeneration, ConnectFailure.Unavailable, live));
                        descriptor = fresh;
                        break;
                }
            }
        }

        private static void RememberBusy(string identity)
        {
            lock (_connectLock) { _busyIdentity = identity; _busyUntil = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency; }
        }

        /// <summary>Open the socket/pipe only; distinguishes busy from no-listener (§6.4).</summary>
        private static Conn OpenTransport(TargetDescriptor desc, out ConnectFailure failure)
        {
            failure = ConnectFailure.Unavailable;
            NamedPipeClientStream openedPipe = null;
            TcpClient openedTcp = null;
            try
            {
                if (desc.Transport == "pipe")
                {
                    // Enumerate \\.\pipe\ without opening: a missing name means no
                    // listener; a present name that times out Connect(4500) means busy.
                    var name = desc.PipeName;
                    if (!PipeExists(name))
                    {
                        DiagnosticLog.Write("Gateway", $"Pipe {name} not present — no listener", desc.HostYear.ToString());
                        return null;
                    }
                    var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    openedPipe = pipe;
                    try
                    {
                        pipe.ConnectAsync(ConnectionWait(TargetCallContext.Current, 4500), TargetCallContext.Current?.Cancellation ?? CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (TimeoutException)
                    {
                        try { pipe.Dispose(); } catch { }
                        failure = ConnectFailure.Busy;
                        return null;
                    }
                    var c = NewConn(pipe, desc);
                    openedPipe = null;
                    DiagnosticLog.Write("Gateway", $"Connected to {desc.TargetId} via Named Pipe: {name}", c.Year);
                    return c;
                }
                else
                {
                    var tcp = new TcpClient();
                    openedTcp = tcp;
                    try
                    {
                        if (!tcp.ConnectAsync("127.0.0.1", desc.Port.Value).Wait(ConnectionWait(TargetCallContext.Current, 4500), TargetCallContext.Current?.Cancellation ?? CancellationToken.None))
                            throw new TimeoutException("TCP connection timed out.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (TimeoutException)
                    {
                        try { tcp.Close(); } catch { }
                        failure = ConnectFailure.Busy;
                        return null;
                    }
                    catch
                    {
                        try { tcp.Close(); } catch { }
                        return null; // refused → no listener → Unavailable
                    }
                    var c = NewConn(tcp, desc);
                    openedTcp = null;
                    DiagnosticLog.Write("Gateway", $"Connected to {desc.TargetId} via TCP on port {desc.Port}", c.Year);
                    return c;
                }
            }
            catch (OperationCanceledException)
            {
                try { openedPipe?.Dispose(); openedTcp?.Dispose(); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { openedPipe?.Dispose(); openedTcp?.Dispose(); } catch { }
                DiagnosticLog.Write("Gateway", $"Connect to {desc.TargetId} failed ({ex.HResult:X8}): {ex.Message}", desc.HostYear.ToString());
                return null;
            }
        }

        private static bool PipeExists(string name)
        {
            try
            {
                foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
                {
                    if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static Conn NewConn(object transport, TargetDescriptor desc)
        {
            var conn = new Conn
            {
                Epoch = Interlocked.Increment(ref _nextEpoch),
                Token = desc.AuthToken,
                TargetId = desc.TargetId,
                Year = desc.HostYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Pid = desc.Pid,
                FailureProtocol = desc.Capabilities?.Any(c => string.Equals(c, "controlled_failures_v1", StringComparison.OrdinalIgnoreCase)) == true,
                DocumentGuard = desc.Capabilities?.Any(c => string.Equals(c, "document_target_v1", StringComparison.OrdinalIgnoreCase)) == true,
            };
            if (transport is TcpClient tcp)
            {
                conn.Tcp = tcp;
                conn.Stream = tcp.GetStream();
            }
            else
            {
                conn.Pipe = (NamedPipeClientStream)transport;
                conn.Stream = conn.Pipe;
            }
            conn.Reader = new StreamReader(conn.Stream, Encoding.UTF8);
            conn.Writer = new StreamWriter(conn.Stream, new UTF8Encoding(false)) { AutoFlush = true };
            var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "RvtMcp.ResponseReader" };
            readThread.Start(conn);
            return conn;
        }

        /// <summary>
        /// Catalog handshake (§6.4): set_tool_catalog must round-trip within 5 s before
        /// any real command is sent. A timeout is the only Busy outcome; a connection
        /// that dies or fails mid-handshake is Closed → Unavailable.
        /// </summary>
        private static HandshakeResult Handshake(Conn conn)
        {
            var id = $"catalog-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var request = new JObject
            {
                ["id"] = id,
                ["command"] = "set_tool_catalog",
                ["params"] = JObject.FromObject(ServerState.ToolCatalog),
                ["token"] = conn.Token
            };
            var pending = new GatewayPendingRequest(null, conn.Epoch);
            var tcs = pending.Caller;
            var cancellation = TargetCallContext.Current?.Cancellation ?? CancellationToken.None;
            using var budget = new CancellationTokenSource();
            _pending[id] = pending;
            try
            {
                // The handshake budget includes a blocked catalog write, not just its response.
                budget.CancelAfter(ConnectionWait(TargetCallContext.Current, 4500));
                using var abortBudget = budget.Token.Register(() => CloseConn(conn));
                using var abortCancellation = cancellation.Register(() => CloseConn(conn));
                cancellation.ThrowIfCancellationRequested();
                conn.Writer.WriteLine(request.ToString(Formatting.None));
                try
                {
                    if (!tcs.Task.Wait(ConnectionWait(TargetCallContext.Current, 4500), cancellation))
                    {
                        DiagnosticLog.Write("Gateway", "Tool catalog handshake timed out — treating target as busy.", conn.Year);
                        return HandshakeResult.TimedOut;
                    }
                }
                catch when (budget.IsCancellationRequested && !cancellation.IsCancellationRequested) { return HandshakeResult.TimedOut; }

                var response = JObject.Parse(tcs.Task.GetAwaiter().GetResult());
                if (response.Value<bool>("success")) return HandshakeResult.Ok;
                var error = response.Value<string>("error") ?? "";
                if (error.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                    return HandshakeResult.TokenRejected;
                DiagnosticLog.Write("Gateway", $"Tool catalog rejected: {error}", conn.Year);
                return HandshakeResult.AnsweredRejected;
            }
            catch (OperationCanceledException) { CloseConn(conn); throw; }
            catch (TimeoutException) { return HandshakeResult.TimedOut; }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) { CloseConn(conn); cancellation.ThrowIfCancellationRequested(); }
                if (budget.IsCancellationRequested) return HandshakeResult.TimedOut;
                // A faulted tcs.Task reaches Wait() wrapped in AggregateException, so a
                // mid-handshake ConnectionInterruptedException lands here too — both mean Closed.
                DiagnosticLog.Write("Gateway", $"Tool catalog handshake failed ({ex.HResult:X8}): {ex.Message}", conn.Year);
                return HandshakeResult.Closed; // e.g. write failed on a closing stream → unavailable
            }
            finally { _pending.TryRemove(id, out _); }
        }

        /// <summary>
        /// Register the pending entry and write the request line once the connection is
        /// proven to still be the current one serving <paramref name="effectiveGeneration"/>.
        /// CanSend + the identity check run under <c>_connectLock</c> (atomic with
        /// switch_target); the wire write runs under the connection's own
        /// <c>WriteLock</c> so a plugin that is not reading can block this call without
        /// blocking switch_target or other callers (lock order: conn.WriteLock →
        /// _connectLock; close paths take only _connectLock and abort a blocked write
        /// by closing the stream). If the connection was swapped or closed before the
        /// write, re-establishes once and retries, then fails with TARGET_UNAVAILABLE.
        /// </summary>
        private static async Task<(Conn Conn, long EffectiveGeneration, GatewayPendingRequest Pending)> WriteRequest(
            Conn conn, long effectiveGeneration, string id, string operationId, string command, object parameters,
            int? timeoutSeconds, RvtMcpConfig config, ChangeHistoryRequest history,
            TargetCallContext callCtx, bool requireWriteCapabilities)
        {
            var binding = RevitTargetBinding.Binding;
            for (var attempt = 0; ; attempt++)
            {
                var request = JsonConvert.SerializeObject(new
                {
                    id,
                    command,
                    @params = parameters ?? new { },
                    token = conn.Token,
                    timeout_seconds = timeoutSeconds,
                    runtime = config.ToRuntimeOptions(),
                    history,
                    operation_id = operationId,
                    failure_protocol = 1,
                    // Lets the add-in tell an unconfirmed document from a gateway that predates confirmation.
                    document_protocol = 1,
                    expected_document_id = binding.Snapshot().Record?.DocumentId
                }, RequestJsonSettings);
                var current = false;
                GatewayPendingRequest pending = null;
                BeforeWriteForTests?.Invoke();
                lock (conn.WriteLock)
                {
                    callCtx?.Cancellation.ThrowIfCancellationRequested();
                    lock (_connectLock)
                    {
                        if (!binding.CanSend(effectiveGeneration, Array.Empty<Bimwright.Targeting.TargetCandidate>(), out var sendFailure))
                            throw new TargetException(sendFailure);
                        if (_conn == conn && conn.Generation == effectiveGeneration && conn.Closed == 0)
                        {
                            string rejection = null;
                            GatewayPendingRequest blocking = null;
                            if ((!ToolReadPolicy.IsReadOnly(command) || requireWriteCapabilities) && (!conn.FailureProtocol || !conn.DocumentGuard))
                                rejection = ToolReadPolicy.AddinPairingRequired;
                            else if (!ToolReadPolicy.IsReadOnly(command)
                                // Send-code also supports Home; the add-in checks the actual document at execution.
                                && !binding.CanWrite(effectiveGeneration, command != "open_model" && command != "send_code_to_revit", out var writeFailure))
                                throw new TargetException(writeFailure);
                            else if (command != "health"
                                && (blocking = _pending.Values.FirstOrDefault(value => value.ConnectionId == conn.Epoch && value.WaitingForUser)) != null)
                                rejection = "REVIT_BUSY: Revit is waiting for native user input. _operation.blockedBy names the waiting operation and its dialog. Use the local failure or operation queries.";
                            if (rejection != null)
                            {
                                var notStarted = GatewayPendingRequest.NotStarted(operationId);
                                if (blocking != null) notStarted["blockedBy"] = blocking.BlockedBy();
                                pending = new GatewayPendingRequest(operationId, conn.Epoch);
                                pending.Route(new JObject { ["success"] = false, ["error"] = rejection, ["_operation"] = notStarted });
                                return (conn, effectiveGeneration, pending);
                            }
                            pending = new GatewayPendingRequest(operationId, conn.Epoch, history);
                            _pending[id] = pending;
                            current = true;
                        }
                    }
                    if (current)
                    {
                        try
                        {
                            conn.Writer.WriteLine(request);
                            return (conn, effectiveGeneration, pending);
                        }
                        catch
                        {
                            _pending.TryRemove(id, out _);
                            pending.Interrupt(new ConnectionInterruptedException());
                            // The write may be partial — never claim sent:false (§6.7).
                            throw new TargetException(binding.Interrupted(effectiveGeneration), pending.Pending("interrupted_unknown")["_operation"] as JObject);
                        }
                    }
                }
                // Nothing was written: the connection closed or was replaced in between.
                if (attempt > 0)
                    throw new TargetException(binding.ReportConnectFailure(
                        effectiveGeneration, ConnectFailure.Unavailable, RevitTargetBinding.ScanNoTitles().Live));
                (conn, effectiveGeneration) = await Task.Run(() => EnsureConnectedFor(effectiveGeneration, callCtx));
            }
        }

        private static void ReadLoop(object state)
        {
            var conn = (Conn)state;
            try
            {
                while (conn.Closed == 0)
                {
                    var line = conn.Reader.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var obj = JObject.Parse(line);
                        RouteResponse(conn.Epoch, obj);
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                // §6.7: every pending request of this connection completes immediately
                // with TARGET_INTERRUPTED — never wait for the command timeout.
                FailPending(conn);
            }
        }

        internal static bool RouteResponse(long epoch, JObject frame)
        {
            var id = frame.Value<string>("id");
            if (id == null || !_pending.TryGetValue(id, out var pending) || pending.ConnectionId != epoch) return false;
            if (pending.Route(frame)) _pending.TryRemove(id, out _);
            return true;
        }

        /// <summary>Fail every pending request that belongs to <paramref name="conn"/>.</summary>
        private static void FailPending(Conn conn)
        {
            if (conn == null) return;
            Interlocked.Exchange(ref conn.Closed, 1);
            foreach (var kv in _pending)
            {
                if (kv.Value.ConnectionId == conn.Epoch && _pending.TryRemove(kv.Key, out var pending))
                    pending.Interrupt(new ConnectionInterruptedException());
            }
        }

        private static void CloseConn(Conn conn)
        {
            if (conn == null) return;
            FailPending(conn);
            try { conn.Stream?.Close(); } catch { }
        }

        private static void CloseConnLocked(Conn conn) => CloseConn(conn);

        /// <summary>
        /// Close the current Server↔Plugin connection (called by revit_switch_target after
        /// the binding moved to a different instance). Pending requests complete with
        /// TARGET_INTERRUPTED immediately (§6.7).
        /// </summary>
        internal static void CloseConnection()
        {
            lock (_connectLock)
            {
                var conn = _conn;
                _conn = null;
                CloseConnLocked(conn);
            }
        }

        /// <summary>Test seam: drop any connection/pending state between tests.</summary>
        internal static void ResetForTests()
        {
            BeforeWriteForTests = null;
            PreviousOperations.Clear();
            lock (_connectLock) { _busyIdentity = null; _busyUntil = 0; }
            lock (_connectLock)
            {
                var conn = _conn;
                _conn = null;
                CloseConnLocked(conn);
            }
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var pending))
                    pending.Interrupt(new ConnectionInterruptedException());
        }
    }
}
