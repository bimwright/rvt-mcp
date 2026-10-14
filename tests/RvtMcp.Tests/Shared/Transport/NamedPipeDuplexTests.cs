using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;
using Xunit.Abstractions;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class NamedPipeDuplexTests
    {
        private readonly ITestOutputHelper _output;
        public NamedPipeDuplexTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public async Task Background_writer_delivers_handshake_while_named_pipe_reader_waits_for_next_request()
        {
            // The verdict is decided before teardown: closing a synchronous pipe handle that still has a
            // pending read or write can itself block, so a failure must never depend on dispose finishing.
            var verdict = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => RunScenario(verdict));
            var finished = await Task.WhenAny(verdict.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(finished == verdict.Task, "Pipe duplex scenario did not finish within 10 s.");
            Assert.Null(await verdict.Task);
        }

        private async Task RunScenario(TaskCompletionSource<string> verdict)
        {
            try
            {
                var name = "RvtMcp-duplex-test-" + Guid.NewGuid().ToString("N");
                using var server = PipeTransportServer.CreatePipe(name);
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                var connected = Task.Run(() => server.WaitForConnection());
                await client.ConnectAsync(2000);
                await connected.WaitAsync(TimeSpan.FromSeconds(2));
                using var clientReader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var clientWriter = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var serverReader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new TransportResponseWriter(new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });

                // The unbuffered pipe does not complete the client write until the server reads it, so read first.
                var requestWritten = clientWriter.WriteLineAsync("catalog request");
                var request = serverReader.ReadLine();
                await requestWritten;
                if (request != "catalog request") { verdict.TrySetResult("Server read '" + request + "' instead of the first request."); return; }

                var nextRead = Task.Run(() => { try { return serverReader.ReadLine(); } catch (IOException) { return null; } });
                // Exercise the real pipe handle after the server's ReadFile is already outstanding.
                await Task.Delay(100);
                _output.WriteLine("server.IsAsync={0}, next request read pending={1}", server.IsAsync, !nextRead.IsCompleted);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                writer.WriteLine("{\"id\":\"catalog\",\"success\":true}");
                var handshake = clientReader.ReadLineAsync();
                if (await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(2))) != handshake)
                {
                    verdict.TrySetResult("Handshake was not delivered within 2 s while the server read was pending: "
                        + "a synchronous pipe handle serializes that read with the background write (server.IsAsync=" + server.IsAsync + ").");
                    return;
                }
                _output.WriteLine("Handshake received after {0} ms", clock.ElapsedMilliseconds);
                if (!JObject.Parse(await handshake).Value<bool>("success")) { verdict.TrySetResult("Handshake payload was not a success."); return; }
                if (nextRead.IsCompleted) { verdict.TrySetResult("The pending read completed before any next request was sent."); return; }

                await clientWriter.WriteLineAsync("next request");
                if (await Task.WhenAny(nextRead, Task.Delay(TimeSpan.FromSeconds(2))) != nextRead)
                { verdict.TrySetResult("The next request was not read after the handshake."); return; }
                verdict.TrySetResult(await nextRead == "next request" ? null : "The next request arrived corrupted.");
            }
            catch (Exception ex)
            {
                verdict.TrySetResult(ex.ToString());
            }
        }
    }
}
