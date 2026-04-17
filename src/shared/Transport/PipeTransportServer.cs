using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bimwright.Plugin
{
    public class PipeTransportServer : ITransportServer
    {
        private Thread _listenThread;
        private volatile bool _running;
        private Action<string, TaskCompletionSource<string>> _onRequest;
        private string _pipeName;
        private volatile bool _clientConnected;

        public bool IsRunning => _running;
        public bool IsClientConnected => _clientConnected;
        public DateTime? LastCommandTime { get; private set; }
        public string ConnectionInfo => $"Pipe:{_pipeName}";

        public void Start(Action<string, TaskCompletionSource<string>> onRequest)
        {
            _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));

            _pipeName = $"Bimwright-{Process.GetCurrentProcess().Id}";

            AuthToken.GenerateAndPersistPipe(_pipeName);
            Log($"Listening on pipe {_pipeName} (auth: enabled)");

            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "Bimwright.PipeTransportServer" };
            _listenThread.Start();
        }

        public void Stop()
        {
            _running = false;

            // Wake up a blocked WaitForConnection by connecting briefly
            try
            {
                using (var dummy = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    dummy.Connect(100);
                }
            }
            catch { }

            AuthToken.DeleteDiscoveryFile("pipe");

            Log("Stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            while (_running)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1, // maxNumberOfServerInstances
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    pipe.WaitForConnection();

                    if (!_running)
                        break;

                    Log("Client connected");
                    HandleClient(pipe);
                }
                catch (IOException) when (!_running)
                {
                    // Clean shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listen error: {ex.Message}");
                }
                finally
                {
                    try { pipe?.Disconnect(); } catch { }
                    try { pipe?.Dispose(); } catch { }
                    Log("Client disconnected");
                }
            }
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            _clientConnected = true;
            try
            {
                var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

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
                        Log("Client read error or broken pipe");
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

                    // Wait for result with 60s timeout
                    string response;
                    if (tcs.Task.Wait(TimeSpan.FromSeconds(60)))
                    {
                        response = tcs.Task.Result;
                    }
                    else
                    {
                        response = Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            id,
                            success = false,
                            error = "Request timed out (60s). Revit may be in a modal dialog or busy."
                        });
                    }

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
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright");
                Directory.CreateDirectory(dir);
                var logFile = Path.Combine(dir, "revit-mcp.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] [PipeTransport] {SecretMasker.Mask(message)}\n");
            }
            catch { }
        }
    }
}
