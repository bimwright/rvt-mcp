using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Plugin
{
    public class PipeTransportServer : ITransportServer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeHandle handle, IntPtr overlapped);

        private readonly string _discoveryDirectory;
        private readonly Action<string> _log;
        private readonly string _pipeNameOverride;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _clientGate = new object();
        private readonly object _lifetimeGate = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private bool _started, _fullyStopped;
        private Thread _listenThread;
        private volatile bool _running;
        private int _stopped;
        private Action<string, TaskCompletionSource<string>> _onRequest;
        private string _pipeName;
        private volatile bool _clientConnected;
        private NamedPipeServerStream _activeClient;
        private DiscoveryPublisher _publisher;

        public PipeTransportServer(string discoveryDirectory = null, Action<string> log = null, string pipeName = null)
        {
            _discoveryDirectory = discoveryDirectory;
            _log = log;
            _pipeNameOverride = pipeName;
        }

        public bool IsRunning => _running;
        public bool IsClientConnected => _clientConnected;
        public DateTime? LastCommandTime { get; private set; }
        public string ConnectionInfo => $"Pipe:{_pipeName}";

        public void Start(Action<string, TaskCompletionSource<string>> onRequest)
        {
            if (onRequest == null) throw new ArgumentNullException(nameof(onRequest));
            lock (_lifetimeGate)
            {
                if (_started || _stopped != 0) throw new InvalidOperationException("A transport instance has one lifetime; create a new instance to restart.");
                _started = true;
                _onRequest = onRequest;
                _pipeName = _pipeNameOverride ?? $"RvtMcp-{Process.GetCurrentProcess().Id}";
                try
                {
                    _activeClient = CreatePipe(_pipeName); // Reserve the name before advertising readiness.
                    _publisher = AuthToken.GenerateAndPersistPipe(_pipeName, _discoveryDirectory);
                    _running = true;
                    _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "RvtMcp.PipeTransportServer" };
                    _listenThread.Start();
                    Log($"Listening on pipe {_pipeName} (auth: enabled)");
                }
                catch
                {
                    _running = false; _stopped = 1; _fullyStopped = true;
                    try { _activeClient?.Dispose(); } catch { }
                    try { _publisher?.Dispose(); } catch { }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lifetimeGate)
            {
                if (_fullyStopped) return;
                _stopped = 1;
                _running = false;
                _stop.Set();
                _cancel.Cancel();
                // CancelIoEx first: DisconnectNamedPipe (called by Dispose) blocks while a
                // synchronous ReadFile is pending on the pipe.
                lock (_clientGate)
                {
                    try { if (_activeClient != null) CancelIoEx(_activeClient.SafePipeHandle, IntPtr.Zero); }
                    catch { }
                    try { _activeClient?.Dispose(); } catch { }
                }

                if (_listenThread != null && Thread.CurrentThread != _listenThread)
                    if (!_listenThread.Join(2000)) throw new TimeoutException("Pipe listener has not stopped; restart was refused.");

                try { _publisher?.Dispose(); } catch { }
                _publisher = null;
                _clientConnected = false;
                _fullyStopped = true;
                Log("Stopped");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            var backoffMs = 100;
            var pipe = _activeClient;
            try
            {
                while (_running)
                {
                    var hadClient = false;
                    try
                    {
                        // Keep the name reserved across clients; Stop cancels the actual wait.
                        pipe.WaitForConnectionAsync(_cancel.Token).GetAwaiter().GetResult();

                        if (!_running)
                            break;

                        HandleClient(pipe);
                        hadClient = _clientConnected;
                        if (hadClient) backoffMs = 100; // Reset only after authenticated traffic.
                    }
                    catch (Exception) when (!_running)
                    {
                        // Clean shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Listener failed ({ex.HResult:X8}): {ex.Message}");
                        // A writer failure may have closed this handle. Rebind only after it is released.
                        try { pipe.Dispose(); } catch { }
                        if (_stop.WaitOne(backoffMs))
                            break;
                        backoffMs = Math.Min(backoffMs * 2, 2000);
                        pipe = CreatePipe(_pipeName);
                        lock (_clientGate) { _activeClient = pipe; }
                    }
                    finally
                    {
                        hadClient = hadClient || _clientConnected;
                        try { pipe?.Disconnect(); } catch { }
                        if (hadClient)
                            Log("Client disconnected");
                        _clientConnected = false;
                    }
                }
            }
            catch (Exception ex) { Log($"Listener stopped after failure ({ex.HResult:X8}): {ex.Message}"); }
            finally
            {
                _running = false; _clientConnected = false;
                try { pipe?.Dispose(); } catch { }
                try { _publisher?.Dispose(); } catch { }
            }
        }

        // Asynchronous (overlapped) so the transport writer thread can write while the listener thread
        // waits in Read. A synchronous handle serializes the two and deadlocks the handshake.
        internal static NamedPipeServerStream CreatePipe(string pipeName) => new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        private void HandleClient(NamedPipeServerStream pipe)
        {
            lock (_clientGate) { _activeClient = pipe; }
            var conn = ToolCatalogStore.BeginConnection();
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new TransportResponseWriter(
                    new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true },
                    () =>
                    {
                        try { CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); } catch { }
                        try { pipe.Dispose(); } catch { }
                    },
                    // DisconnectNamedPipe discards unread bytes. Wait on the writer thread only,
                    // within Dispose's drain deadline, so the peer can read the terminal frame first.
                    () => { if (_running && pipe.IsConnected) pipe.WaitForPipeDrain(); });
                using var idleGuard = new Timer(_ =>
                {
                    if (!writer.IsIdleFor(TimeSpan.FromSeconds(120))) return;
                    Log("Closing idle client");
                    try { CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); } catch { }
                    try { pipe.Disconnect(); } catch { }
                }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

                var requestTimestamps = new System.Collections.Generic.Queue<DateTime>();
                const int RateLimitMax = 20;
                var RateLimitWindow = TimeSpan.FromSeconds(10);

                while (_running && pipe.IsConnected)
                {
                    string line;
                    try
                    {
                        line = ReadLineBounded(reader, MaxLineBytes, out bool overflow);
                        if (overflow)
                        {
                            Log("Dropped oversized request (>1 MiB)");
                            try
                            {
                                writer.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = "Request exceeded 1 MiB size limit."
                                }));
                            }
                            catch { }
                            break;
                        }
                        if (line == null) break; // Client disconnected
                    }
                    catch (IOException)
                    {
                        if (_running) Log("Client read ended");
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Parse request
                    Newtonsoft.Json.Linq.JObject request;
                    try
                    {
                        request = Newtonsoft.Json.Linq.JObject.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }

                    string token = request.Value<string>("token");
                    if (!AuthToken.Verify(token))
                    {
                        var denied = Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            id = request.Value<string>("id"),
                            success = false,
                            error = "Unauthorized: invalid or missing token."
                        });
                        Log("Auth rejected");
                        try { writer.WriteLine(denied); } catch { }
                        break; // drop the connection on auth failure
                    }
                    if (!_clientConnected) { _clientConnected = true; Log("Client connected"); }
                    writer.Touch();

                    string id = request.Value<string>("id");
                    string command = request.Value<string>("command");
                    string paramsJson = request["params"]?.ToString() ?? "{}";

                    // Internal server -> plugin handshake messages are consumed here,
                    // after token auth and before rate limiting or the Revit callback.
                    // They must never enter history, usage, toast, or ExternalEvent.
                    if (string.Equals(command, "set_tool_catalog", StringComparison.Ordinal))
                    {
                        var catalogResult = ToolCatalogStore.AcceptJson(paramsJson, conn);
                        var catalogResponse = new Newtonsoft.Json.Linq.JObject
                        {
                            ["id"] = id,
                            ["success"] = catalogResult.IsValid
                        };
                        if (!catalogResult.IsValid)
                            catalogResponse["error"] = catalogResult.Error;
                        try { writer.WriteLine(catalogResponse.ToString(Newtonsoft.Json.Formatting.None)); } catch { break; }
                        continue;
                    }


                    var now = DateTime.UtcNow;
                    while (requestTimestamps.Count > 0 && (now - requestTimestamps.Peek()) > RateLimitWindow)
                        requestTimestamps.Dequeue();
                    if (requestTimestamps.Count >= RateLimitMax)
                    {
                        Log("Rate limit exceeded, dropping connection");
                        try
                        {
                            writer.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                id,
                                success = false,
                                error = "Rate limit: 20 requests / 10 seconds per connection."
                            }));
                        }
                        catch { }
                        break;
                    }
                    requestTimestamps.Enqueue(now);

                    LastCommandTime = DateTime.Now;
                    writer.Dispatch(request, line, _onRequest);
                }
            }
            finally
            {
                ToolCatalogStore.Clear(conn);
            }
        }

        private const int MaxLineBytes = 1024 * 1024; // 1 MiB

        private static string ReadLineBounded(StreamReader reader, int maxBytes, out bool overflow)
        {
            overflow = false;
            var sb = new System.Text.StringBuilder();
            int count = 0;
            while (true)
            {
                int ch = reader.Read();
                if (ch == -1) return sb.Length == 0 ? null : sb.ToString();
                if (ch == '\n') return sb.ToString();
                if (ch == '\r') continue;
                count++;
                if (count > maxBytes) { overflow = true; return null; }
                sb.Append((char)ch);
            }
        }

        private void Log(string message)
        {
            if (_log != null) { try { _log(message); } catch { } return; }
            FileLog(message);
        }

        private static void FileLog(string message)
        {
            DiagnosticLog.Write("PipeTransport", message, AuthToken.RevitVersion);
        }
    }
}
