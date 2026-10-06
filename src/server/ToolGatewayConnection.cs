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
    /// Connection half of <see cref="ToolGateway"/> (see Program.cs for the tool-call
    /// half): the socket/pipe lifecycle, response read loop, pending-request
    /// correlation, establishment serialization and the generation-checked write.
    /// Every wire command is gated by <see cref="RevitTargetBinding"/> (spec §6):
    /// a call pins the binding generation when it arrives, and the command is only
    /// written to the connection while the pinned generation still matches.
    /// </summary>
    internal static partial class ToolGateway
    {
        private sealed class PendingRequest
        {
            public TaskCompletionSource<string> Tcs;
            public long Epoch;
        }

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
            public int Closed;
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

        private static readonly ConcurrentDictionary<string, PendingRequest> _pending = new ConcurrentDictionary<string, PendingRequest>();
        private static readonly object _connectLock = new object();
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new McpJsonInput.JsonElementConverter() }
        };
        private static Conn _conn;
        private static long _nextEpoch;
        private static readonly SemaphoreSlim _establishGate = new SemaphoreSlim(1, 1);

        /// <summary>Target id of the instance the open connection serves, or null.</summary>
        internal static string ConnectedTargetId => _conn?.Alive == true ? _conn.TargetId : null;

        /// <summary>JIT warm-up for the wire paths: request serialization, pending map,
        /// response parse, plus a private 127.0.0.1 socket pair — no Revit listener is touched.</summary>
        internal static void WarmUpWire()
        {
            try
            {
                var wire = JsonConvert.SerializeObject(new
                {
                    id = "warmup", command = "warmup", @params = new { },
                    token = "warmup", timeout_seconds = 1, runtime = (object)null, history = (object)null
                }, RequestJsonSettings);
                _ = wire.Length;
                _ = JObject.Parse("{\"id\":\"warmup\",\"success\":true,\"data\":{}}");
                _pending["warmup"] = new PendingRequest { Tcs = new TaskCompletionSource<string>(), Epoch = 0 };
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
                        TargetId = "warmup", HostYear = 2024, Transport = "tcp",
                        Port = port, AuthToken = "warmup"
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
            lock (_connectLock)
                if (TryFastPath(pinned, callCtx, out var fast, out var fastGen)) return (fast, fastGen);

            // Serialize establishment: parallel calls must not open competing sockets
            // against a single-client listener (they would see our own TARGET_BUSY).
            _establishGate.Wait();
            try
            {
                lock (_connectLock)
                    if (TryFastPath(pinned, callCtx, out var fast, out var fastGen)) return (fast, fastGen);
                return EstablishLocked(pinned, callCtx);
            }
            finally
            {
                _establishGate.Release();
            }
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
                    if (desc == null) Thread.Sleep(200);
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
        internal static SwitchResult SwitchBinding(TargetSelector selector, IReadOnlyList<Bimwright.Targeting.TargetCandidate> live)
        {
            lock (_connectLock)
            {
                var result = RevitTargetBinding.Switch(selector, live);
                if (result.Ok && result.InstanceChanged)
                {
                    var conn = _conn;
                    _conn = null;
                    CloseConnLocked(conn); // pending requests end as TARGET_INTERRUPTED
                }
                return result;
            }
        }

        private enum HandshakeResult { Ok, AnsweredRejected, TokenRejected, TimedOut, Closed }

        /// <summary>§6.4 connect: busy vs no-listener distinction, handshake precondition.</summary>
        private static Conn ConnectAndHandshake(TargetDescriptor desc, long effectiveGeneration,
            IReadOnlyList<Bimwright.Targeting.TargetCandidate> live)
        {
            var binding = RevitTargetBinding.Binding;
            var descriptor = desc;
            var tokenRetried = false;
            while (true)
            {
                var conn = OpenTransport(descriptor, out var failure);
                if (conn == null)
                    throw new TargetException(binding.ReportConnectFailure(effectiveGeneration, failure, live));

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

        /// <summary>Open the socket/pipe only; distinguishes busy from no-listener (§6.4).</summary>
        private static Conn OpenTransport(TargetDescriptor desc, out ConnectFailure failure)
        {
            failure = ConnectFailure.Unavailable;
            try
            {
                if (desc.Transport == "pipe")
                {
                    // Enumerate \\.\pipe\ without opening: a missing name means no
                    // listener; a present name that times out Connect(4500) means busy.
                    var name = desc.PipeName;
                    if (!PipeExists(name))
                    {
                        Console.Error.WriteLine($"[RvtMcp] Pipe {name} not present — no listener");
                        return null;
                    }
                    var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        pipe.Connect(4500);
                    }
                    catch (TimeoutException)
                    {
                        try { pipe.Dispose(); } catch { }
                        failure = ConnectFailure.Busy;
                        return null;
                    }
                    var c = NewConn(pipe, desc);
                    Console.Error.WriteLine($"[RvtMcp] Connected to {desc.TargetId} via Named Pipe: {name}");
                    return c;
                }
                else
                {
                    var tcp = new TcpClient();
                    try
                    {
                        tcp.Connect("127.0.0.1", desc.Port.Value);
                    }
                    catch
                    {
                        try { tcp.Close(); } catch { }
                        return null; // refused → no listener → Unavailable
                    }
                    var c = NewConn(tcp, desc);
                    Console.Error.WriteLine($"[RvtMcp] Connected to {desc.TargetId} via TCP on port {desc.Port}");
                    return c;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RvtMcp] Connect to {desc.TargetId} failed: {ex.Message}");
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
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = new PendingRequest { Tcs = tcs, Epoch = conn.Epoch };
            try
            {
                conn.Writer.WriteLine(request.ToString(Formatting.None));
                if (!tcs.Task.Wait(TimeSpan.FromMilliseconds(4500)))
                {
                    _pending.TryRemove(id, out _);
                    Console.Error.WriteLine("[RvtMcp] Tool catalog handshake timed out (4.5s) — treating target as busy.");
                    return HandshakeResult.TimedOut;
                }

                var response = JObject.Parse(tcs.Task.GetAwaiter().GetResult());
                if (response.Value<bool>("success")) return HandshakeResult.Ok;
                var error = response.Value<string>("error") ?? "";
                if (error.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                    return HandshakeResult.TokenRejected;
                Console.Error.WriteLine($"[RvtMcp] Tool catalog rejected: {error}");
                return HandshakeResult.AnsweredRejected;
            }
            catch (ConnectionInterruptedException)
            {
                return HandshakeResult.Closed; // connection died mid-handshake → unavailable
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                Console.Error.WriteLine($"[RvtMcp] Tool catalog handshake failed: {ex.Message}");
                return HandshakeResult.Closed; // e.g. write failed on a closing stream → unavailable
            }
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
        private static async Task<(Conn Conn, long EffectiveGeneration)> WriteRequest(
            Conn conn, long effectiveGeneration, string id, string command, object parameters,
            int? timeoutSeconds, RvtMcpConfig config, ChangeHistoryRequest history,
            TaskCompletionSource<string> tcs, TargetCallContext callCtx)
        {
            var binding = RevitTargetBinding.Binding;
            for (var attempt = 0; ; attempt++)
            {
                var current = false;
                lock (conn.WriteLock)
                {
                    lock (_connectLock)
                    {
                        if (!binding.CanSend(effectiveGeneration, Array.Empty<Bimwright.Targeting.TargetCandidate>(), out var sendFailure))
                            throw new TargetException(sendFailure);
                        if (_conn == conn && conn.Generation == effectiveGeneration && conn.Closed == 0)
                        {
                            _pending[id] = new PendingRequest { Tcs = tcs, Epoch = conn.Epoch };
                            current = true;
                        }
                    }
                    if (current)
                    {
                        var request = JsonConvert.SerializeObject(new
                        {
                            id, command, @params = parameters ?? new { }, token = conn.Token,
                            timeout_seconds = timeoutSeconds, runtime = config.ToRuntimeOptions(), history
                        }, RequestJsonSettings);
                        try
                        {
                            conn.Writer.WriteLine(request);
                            return (conn, effectiveGeneration);
                        }
                        catch
                        {
                            _pending.TryRemove(id, out _);
                            // The write may be partial — never claim sent:false (§6.7).
                            throw new TargetException(binding.Interrupted(effectiveGeneration));
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
                        var id = obj.Value<string>("id");
                        if (id != null && _pending.TryRemove(id, out var pending) && pending.Epoch == conn.Epoch)
                        {
                            pending.Tcs.TrySetResult(line);
                        }
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

        /// <summary>Fail every pending request that belongs to <paramref name="conn"/>.</summary>
        private static void FailPending(Conn conn)
        {
            if (conn == null) return;
            Interlocked.Exchange(ref conn.Closed, 1);
            foreach (var kv in _pending)
            {
                if (kv.Value.Epoch == conn.Epoch && _pending.TryRemove(kv.Key, out var pending))
                    pending.Tcs.TrySetException(new ConnectionInterruptedException());
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
            lock (_connectLock)
            {
                var conn = _conn;
                _conn = null;
                CloseConnLocked(conn);
            }
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var pending))
                    pending.Tcs.TrySetException(new ConnectionInterruptedException());
        }
    }
}
