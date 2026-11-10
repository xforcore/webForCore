using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace IISGate
{
    // Process recycling (#56): the sample runs under webforcore-host. When the application restarts
    // (HttpRuntime.UnloadAppDomain, a Web.config change), the worker asks the host for its replacement and exits once it
    // has drained; the next worker runs the application with new static state. Requests are answered throughout, as IIS
    // answered them during a recycle.
    [Collection(IISSampleCollection.Name)]
    public class RecyclingTests
    {
        private readonly SampleServer _server;

        public RecyclingTests(SampleServer server)
        {
            _server = server;
        }

        private record Start(string Domain, int Starts, int Process);

        private async Task<Start> Current()
        {
            var response = await _server.Client.GetAsync("/Domain.aspx");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}\nThe host's output:\n{OutputTail()}");
            var match = Regex.Match(body, @"domain=([0-9a-f-]+);starts=(\d+);process=(\d+)");
            Assert.True(match.Success, body);
            return new Start(match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        }

        // The application after `before` has been replaced.
        private async Task<Start> Next(Start before, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            var current = before;
            while (current.Process == before.Process && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                current = await Current();
            }
            Assert.NotEqual(before.Process, current.Process);
            return current;
        }

        private async Task WaitForOutput(string text, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (!_server.Output.Contains(text) && DateTime.UtcNow < deadline) await Task.Delay(200);
            Assert.Contains(text, _server.Output);
        }

        // HttpRuntime.UnloadAppDomain: a new worker process runs the application (Application_Start once, new static
        // state), and the old one exits by itself.
        [Fact]
        public async Task UnloadAppDomainRestartsTheApplicationInANewWorker()
        {
            var before = await Current();
            Assert.Equal(before, await Current());
            Assert.Equal(1, before.Starts); // Application_Start once per worker

            Assert.Equal(HttpStatusCode.OK, (await _server.Client.GetAsync("/Restart.aspx")).StatusCode);
            var after = await Next(before, TimeSpan.FromSeconds(60));
            Assert.NotEqual(before.Domain, after.Domain);
            Assert.Equal(1, after.Starts); // the new worker's own static state

            await WaitForOutput($"(process {before.Process}) exited (0).", TimeSpan.FromSeconds(60));
            Assert.Matches($@"Recycling worker \d+ \(process {before.Process}\): UnloadAppDomainCalled\.", _server.Output);
        }

        // Every request is answered while the application restarts: those that arrive while the next worker starts
        // wait for it (the host sends a request the old worker didn't take to the next one).
        [Fact]
        public async Task RequestsDuringARestartAreAllAnswered()
        {
            await AllAnsweredDuringRestarts(clients: 4, restarts: 1);
        }

        // #209: the same with more clients and restart after restart, which hits the moment a request is between the old
        // worker's check that its application is still running and the runtime counting it more often. The runtime had
        // already begun disposing by then and answered "Server Too Busy" (503) without the request being processed; on
        // IIS such a request waits in http.sys's queue for the next worker, and here it goes to the next worker too.
        [Fact]
        public async Task RequestsDuringRepeatedRestartsUnderLoadAreAllAnswered()
        {
            await AllAnsweredDuringRestarts(clients: 16, restarts: 5);
        }

        // #214: the same with requests that have a body, form POSTs and raw PUTs, each of which must reach the application
        // with its body. One that reached the old worker as its application began shutting down got 503 ("Server Too
        // Busy", or the worker's own answer while it shut down), because webforcore-host had sent its body on and couldn't
        // send the request again; on IIS http.sys queues it and the next worker serves it.
        [Fact]
        public async Task RequestsWithBodiesDuringRepeatedRestartsUnderLoadAreAllAnswered()
        {
            await AllAnsweredDuringRestarts(clients: 16, restarts: 5, withBodies: true);
        }

        private async Task AllAnsweredDuringRestarts(int clients, int restarts, bool withBodies = false)
        {
            using var stop = new CancellationTokenSource();
            int answered = 0;
            var others = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var load = Enumerable.Range(0, clients).Select(client => Task.Run(async () =>
            {
                for (int n = 0; !stop.IsCancellationRequested; n++)
                {
                    HttpResponseMessage response;
                    string sent = null, what = "GET";
                    try
                    {
                        if (!withBodies) response = await _server.Client.GetAsync("/Domain.aspx");
                        else
                        {
                            // Small bodies, bodies the host keeps in memory until the request is answered (up to 49,152 bytes)
                            // and larger ones, which it sends only when the application reads them; and bodies of unknown
                            // length (chunked).
                            sent = $"{client}-{n}-" + new string((char)('a' + n % 26), (n % 3) switch { 0 => 200, 1 => 20000, _ => 100000 });
                            HttpContent content = client % 2 == 0
                                ? new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = sent })
                                : new StringContent(sent, Encoding.UTF8, "text/plain");
                            using var request = new HttpRequestMessage(client % 2 == 0 ? HttpMethod.Post : HttpMethod.Put, "/Posted.aspx") { Content = content };
                            if (n % 4 == 3) request.Headers.TransferEncodingChunked = true;
                            what = $"{request.Method} of {sent.Length} characters{(n % 4 == 3 ? ", chunked" : "")}";
                            response = await _server.Client.SendAsync(request);
                        }
                    }
                    catch (HttpRequestException e)
                    {
                        // The client's connection was reset or closed without an answer.
                        Interlocked.Increment(ref answered);
                        others.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {what}: {e.GetBaseException().Message}");
                        continue;
                    }
                    using (response)
                    {
                        Interlocked.Increment(ref answered);
                        var body = await response.Content.ReadAsStringAsync();
                        if (response.StatusCode != HttpStatusCode.OK)
                            others.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {response.RequestMessage.Method} {(int)response.StatusCode} {string.Join(", ", response.Headers.Select(h => h.Key))} {(body.Length > 300 ? body[..300] : body)}");
                        else if (sent != null && !body.Contains($";body={sent};expect=;"))
                            others.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {response.RequestMessage.Method} 200 without the body sent, or with an Expect header it didn't send: {(body.Length > 300 ? body[..300] : body)}");
                    }
                }
            })).ToArray();
            await Task.Delay(500);
            for (int i = 0; i < restarts; i++)
            {
                var before = await Current();
                Assert.Equal(HttpStatusCode.OK, (await _server.Client.GetAsync("/Restart.aspx")).StatusCode);
                await Next(before, TimeSpan.FromSeconds(60));
                await Task.Delay(500);
            }
            stop.Cancel();
            await Task.WhenAll(load);
            Assert.True(answered > 10, $"{answered} requests");
            if (!others.IsEmpty) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "recycling-host-output.txt"), _server.Output);
            Assert.True(others.IsEmpty, $"{others.Count} of {answered} requests weren't answered 200:\n{string.Join("\n", others)}\nThe host's output is in recycling-host-output.txt beside the test assembly.");
        }

        // A WebSocket open across a restart is aborted once the application's registered objects have stopped, without
        // waiting for <hostingEnvironment shutdownTimeout> (5 s in the sample) or <httpRuntime shutdownTimeout>, as
        // HostingEnvironment.ShutdownThisAppDomainOnce aborts every WebSocket on .NET Framework (#181), and the worker exits.
        // It used to be served until <hostingEnvironment shutdownTimeout>.
        [Fact]
        public async Task RestartAbortsAWebSocket()
        {
            var before = await Current();

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(_server.BaseAddress) { Scheme = "ws", Path = "/Echo.ashx" }.Uri, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, (await _server.Client.GetAsync("/Restart.aspx")).StatusCode);

            var buffer = new byte[1024];
            var restarted = DateTime.UtcNow;
            bool aborted = false;
            while (!aborted && DateTime.UtcNow - restarted < TimeSpan.FromSeconds(30))
            {
                try
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    aborted = result.MessageType == WebSocketMessageType.Close;
                }
                catch (WebSocketException)
                {
                    aborted = true;
                }
                if (!aborted) await Task.Delay(500);
            }
            var elapsed = DateTime.UtcNow - restarted;
            Assert.True(aborted, "the WebSocket stayed open 30 s after the restart");
            Assert.True(elapsed < TimeSpan.FromSeconds(4), $"aborted {elapsed.TotalSeconds:0.0} s after the restart");

            await WaitForOutput($"(process {before.Process}) exited", TimeSpan.FromSeconds(30));
        }

        // #181: a request still running when the application restarts runs to its end in the old worker, while new
        // requests go to the new one, as on .NET Framework, where HttpRuntime.Dispose waits for the old domain's requests
        // for <httpRuntime shutdownTimeout> (5 s in the sample), and in the classic pipeline for those still running after
        // that too (IIS 10 in both pipelines: tools/ci/iis_drain.cs). <hostingEnvironment shutdownTimeout> (5 s in the
        // sample) is the wait for registered objects, not for requests. DNN's installer streams its progress with
        // Response.Flush and changes bin; it was cut about 5 s after the restart.
        [Fact]
        public async Task ARequestRunningAcrossARestartRunsToItsEnd()
        {
            var before = await Current();
            const int seconds = 12;
            var started = DateTime.UtcNow;
            using var response = await _server.Client.GetAsync($"/Stream.aspx?seconds={seconds}&touch=drain.gate", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var read = Task.Run(async () =>
            {
                var lines = new List<string>();
                try
                {
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                    while (await reader.ReadLineAsync() is string line) lines.Add(line);
                }
                catch (Exception e) when (e is IOException || e is HttpRequestException)
                {
                    lines.Add("(" + e.GetBaseException().Message + ")");
                }
                return lines;
            });

            // The page changed bin_dotnet after 2 s: a new worker serves new requests while the old one finishes the page.
            var after = await Next(before, TimeSpan.FromSeconds(30));
            Assert.False(read.IsCompleted, "the long request ended before the next worker served");

            var body = await read;
            var elapsed = DateTime.UtcNow - started;
            Assert.True(body.Count > 0 && body[^1] == "done", $"after {elapsed.TotalSeconds:0.0} s: {string.Join(" | ", body)}");
            Assert.Equal($"process={before.Process}", body[0]);
            Assert.Equal(seconds + 2, body.Count);

            // Removing the file is a change in bin too: the next test starts after that restart.
            File.Delete(Path.Combine(_server.SampleDirectory, "bin_dotnet", "drain.gate"));
            await Next(after, TimeSpan.FromSeconds(30));
            await WaitForOutput($"(process {before.Process}) exited", TimeSpan.FromSeconds(30));
        }

        // Web.config changing again and again, also while the next worker starts (#49: in-process, a change while the
        // next start was starting left the application answering 503 for good): it ends with a worker serving.
        [Fact]
        public async Task ChangesWhileTheNextWorkerStartsEndInAServingWorker()
        {
            var before = await Current();
            var config = Path.Combine(_server.SampleDirectory, "Web.config");
            for (int i = 0; i < 30; i++)
            {
                File.SetLastWriteTimeUtc(config, DateTime.UtcNow);
                await Task.Delay(100);
            }
            var deadline = DateTime.UtcNow.AddSeconds(90);
            Start after = null;
            string last = null;
            while (DateTime.UtcNow < deadline)
            {
                var response = await _server.Client.GetAsync("/Domain.aspx");
                var body = await response.Content.ReadAsStringAsync();
                var match = Regex.Match(body, @"domain=([0-9a-f-]+);starts=(\d+);process=(\d+)");
                last = $"{(int)response.StatusCode} {(body.Length > 500 ? body[..500] : body)}";
                if (response.StatusCode == HttpStatusCode.OK && match.Success)
                {
                    after = new Start(match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
                    if (after.Process != before.Process) break;
                }
                await Task.Delay(250);
            }
            // #107: when no worker serves, what the host and its workers said (on Linux, an application whose file change
            // monitoring couldn't start because the user's inotify instances were used up answers with its error page).
            Assert.True(after != null && after.Process != before.Process,
                $"No new worker served /Domain.aspx within 90 s. Last answer: {last}\nThe host's output:\n{OutputTail()}");
            Assert.Equal(HttpStatusCode.OK, (await _server.Client.GetAsync("/Domain.aspx")).StatusCode);
        }

        private string OutputTail()
        {
            var output = _server.Output;
            return output.Length > 20000 ? output[^20000..] : output;
        }

        // #89: an App_Code change while the old worker still compiles a page (Umbraco 6.2.6's install: its Application_Start
        // creates ~/App_Code, and the login page that worker was compiling failed with CS0006 because the next worker had
        // deleted App_global.asax's assembly from the compilation folder they share). As on IIS, the next worker leaves
        // the assemblies a running worker has loaded, marked .delete, and the old worker's compilation succeeds.
        [Fact]
        public async Task AnOldWorkerStillCompilesAfterTheNextOneCleanedTheCompilationFolder()
        {
            var before = await Current();
            var slow = _server.Client.GetStringAsync("/regressions/RecycleCompile.aspx");
            // The next worker cleans the compilation folder when it starts the application: on its first request.
            await Next(before, TimeSpan.FromSeconds(30));
            var body = await slow;
            Assert.True(body.Contains("page: compiled") && body.Contains("cleaned: marked"), body);
        }

        // A worker that exits by itself (a crash) is replaced, and requests are answered again.
        [Fact]
        public async Task AWorkerThatExitsIsReplaced()
        {
            var before = await Current();
            Assert.Equal("exiting 42", (await _server.Client.GetStringAsync("/Exit.aspx?code=42")).Trim());
            var after = await Next(before, TimeSpan.FromSeconds(60));
            Assert.Equal(1, after.Starts);
            Assert.Matches($@"Worker \d+ \(process {before.Process}\) exited unexpectedly \(42\)", _server.Output);
        }

        // #51: a change in bin or App_Code restarts the application, as on IIS, on every platform. On Linux only Web.config
        // did: a deployment that replaced the application's DLLs kept running the old code (found running the smoke
        // test's applications on Linux: MvcMovie's bin_dotnet, MiniBlog's App_Code).
        [Theory]
        [InlineData("bin_dotnet", "restart.gate")]
        [InlineData("App_Code", "DomainState.cs")]
        public async Task BinAndAppCodeChangesRestartTheApplication(string folder, string file)
        {
            var before = await Current();
            var path = Path.Combine(_server.SampleDirectory, folder, file);
            if (file.EndsWith(".cs")) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            else File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            var after = await Next(before, TimeSpan.FromSeconds(30));
            if (!file.EndsWith(".cs"))
            {
                // Removing it is a change in bin too: the next test starts after that restart.
                File.Delete(path);
                await Next(after, TimeSpan.FromSeconds(30));
            }
        }
    }
}
