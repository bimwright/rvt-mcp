using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Plugin
{
    public class TcpTransportServer : ITransportServer
    {
        private readonly string _discoveryDirectory;
        private readonly Action<string> _log;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _clientGate = new object();
        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;
        private int _stopped;
        private Action<string, TaskCompletionSource<string>> _onRequest;
        private int _port;
        private volatile bool _clientConnected;
        private TcpClient _activeClient;
        private DiscoveryPublisher _publisher;

        public TcpTransportServer(string discoveryDirectory = null, Action<string> log = null)
        {
            _discoveryDirectory = discoveryDirectory;
            _log = log;
        }

        public bool IsRunning => _running;
        public int Port => _port;
        public bool IsClientConnected => _clientConnected;
        public DateTime? LastCommandTime { get; private set; }
        public string ConnectionInfo => $"TCP:{_port}";

        public void Start(Action<string, TaskCompletionSource<string>> onRequest)
        {
            _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));

            // S7 fail-closed default: bind loopback only unless BIMWRIGHT_ALLOW_LAN_BIND is
            // explicitly opted in. Token auth stays active in either mode (see AuthToken).
            bool allowLan = IsLanBindAllowed();
            var bindAddress = allowLan ? IPAddress.Any : IPAddress.Loopback;

            _listener = new TcpListener(bindAddress, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _publisher = AuthToken.GenerateAndPersist(_port, _discoveryDirectory);
            if (allowLan)
            {
                const string warn = "[RvtMcp] \u26A0 LAN bind enabled. Token auth active but network exposed.";
                try { Console.Error.WriteLine(warn); } catch { }
                Log($"Listening on 0.0.0.0:{_port} (LAN bind, auth: enabled)");
            }
            else
            {
                Log($"Listening on 127.0.0.1:{_port} (auth: enabled)");
            }

            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "RvtMcp.TcpTransportServer" };
            _listenThread.Start();
        }

        internal static bool IsLanBindAllowed()
        {
            var value = Environment.GetEnvironmentVariable("BIMWRIGHT_ALLOW_LAN_BIND");
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _running = false;
            _stop.Set();
            lock (_clientGate) { try { _activeClient?.Close(); } catch { } }
            try { _listener?.Stop(); } catch { }

            if (_listenThread != null && Thread.CurrentThread != _listenThread)
                _listenThread.Join(2000);

            try { _publisher?.Dispose(); } catch { }
            _publisher = null;

            Log("Stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            var backoffMs = 100;
            while (_running)
            {
                TcpClient client = null;
                var hadClient = false;
                try
                {
                    client = _listener.AcceptTcpClient();
                    hadClient = true;
                    backoffMs = 100; // a successful connection resets the error back-off
                    Log("Client connected");
                    HandleClient(client);
                }
                catch (SocketException) when (!_running)
                {
                    // Clean shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listen error: {ex.Message}");
                    if (_stop.WaitOne(backoffMs))
                        break;
                    backoffMs = Math.Min(backoffMs * 2, 2000);
                }
                finally
                {
                    try { client?.Close(); } catch { }
                    if (hadClient)
                        Log("Client disconnected");
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 120000;
            _clientConnected = true;
            lock (_clientGate) { _activeClient = client; }
            var conn = ToolCatalogStore.BeginConnection();
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var requestTimestamps = new System.Collections.Generic.Queue<DateTime>();
                const int RateLimitMax = 20;
                var RateLimitWindow = TimeSpan.FromSeconds(10);

                while (_running && client.Connected)
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
                    catch (IOException) // ReceiveTimeout fired or broken pipe
                    {
                        Log("Client read timeout or broken pipe");
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
                        try { writer.WriteLine(denied); } catch { }
                        Log("Auth rejected");
                        break; // drop the connection on auth failure
                    }

                    string id = request.Value<string>("id");
                    string command = request.Value<string>("command");
                    string paramsJson = request["params"]?.ToString() ?? "{}";
                    int? timeoutSeconds = request.Value<int?>("timeout_seconds");

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

                    // Create TCS and invoke callback
                    var tcs = new TaskCompletionSource<string>();

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
                    _onRequest(line, tcs);

                    var response = RequestWait.WaitOrTimeout(tcs, id,
                        timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null);

                    try
                    {
                        writer.WriteLine(response);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            finally
            {
                _clientConnected = false;
                lock (_clientGate) { _activeClient = null; }
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
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
                Directory.CreateDirectory(dir);
                var logFile = Path.Combine(dir, "revit-mcp.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] [TcpTransportServer] {SecretMasker.Mask(message)}\n");
            }
            catch { }
        }
    }
}
