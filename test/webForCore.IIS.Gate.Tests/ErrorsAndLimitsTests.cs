using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace IISGate
{
    // #192: <customErrors> (mode, defaultRedirect, <error statusCode>, redirectMode), its interplay with IIS's <httpErrors>
    // (existingResponse) and Response.TrySkipIisCustomErrors, and <httpRuntime maxRequestLength> and
    // requestLengthDiskThreshold against request filtering's maxAllowedContentLength, on test/webForCore.Requests.Sample's
    // /errors/ and /limits/ folders (each folder's Web.config sets them). Every response is recorded with
    // RequestHandlingTests' and compared with IIS 10's by CI's iis-compare job; the values asserted here are IIS's.
    [Collection(RequestsSampleCollection.Name)]
    public class ErrorsAndLimitsTests
    {
        private readonly RequestsSampleServer _server;

        public ErrorsAndLimitsTests(RequestsSampleServer server)
        {
            _server = server;
        }

        public static readonly TheoryData<string, int> ErrorRequests = new TheoryData<string, int>
        {
            // customErrors mode="On": an exception redirects to defaultRedirect, a 404 (an HttpException, or a page that
            // doesn't exist) to its <error statusCode="404">, with the failing URL in aspxerrorpath. A status the page
            // sets itself isn't an error to customErrors.
            { "/errors/on/Throw.aspx", 302 },
            { "/errors/on/Http404.aspx", 302 },
            { "/errors/on/Missing.aspx", 302 },
            { "/errors/on/Status500.aspx", 500 },
            // RemoteOnly, for a local request, and Off: ASP.NET's detailed error page.
            { "/errors/remoteonly/Throw.aspx", 500 },
            { "/errors/remoteonly/Http404.aspx", 404 },
            { "/errors/off/Throw.aspx", 500 },
            { "/errors/off/Http404.aspx", 404 },
            // redirectMode="ResponseRewrite": the error page's output at the failing URL (Server.Execute), with the
            // status the error page has, 200 unless it sets one.
            { "/errors/rewrite/Throw.aspx", 200 },
            { "/errors/rewrite/Http404.aspx", 200 },
            { "/errors/rewrite/Missing.aspx", 200 },
            // On without a defaultRedirect: ASP.NET's generic "Runtime Error" page.
            { "/errors/nodefault/Throw.aspx", 500 },
            { "/errors/nodefault/Http404.aspx", 404 },
            // <httpErrors errorMode="Custom"> with customErrors off, by existingResponse. A replaced response is the
            // ExecuteURL child request's: a static page (iis500.htm) is IIS's static file handler's 200, and a page
            // (IisError.aspx) that sets no status is 200 too.
            { "/errors/iis-auto/Throw.aspx", 500 },
            { "/errors/iis-auto/Http404.aspx", 404 },
            { "/errors/iis-auto/Status500.aspx", 200 },
            { "/errors/iis-auto/Status500Skip.aspx", 500 },
            { "/errors/iis-replace/Throw.aspx", 200 },
            { "/errors/iis-replace/Status500.aspx", 200 },
            { "/errors/iis-replace/Status500Skip.aspx", 200 },
            { "/errors/iis-replace-page/Throw.aspx", 200 },
            { "/errors/iis-replace-page/Status500.aspx", 200 },
            { "/errors/iis-passthrough/Throw.aspx", 500 },
            { "/errors/iis-passthrough/Status500.aspx", 500 },
            { "/errors/iis-passthrough/Status500Skip.aspx", 500 },
            // customErrors on with httpErrors Replace: customErrors's redirect isn't an error.
            { "/errors/iis-on/Throw.aspx", 302 },
            { "/errors/iis-on/Http404.aspx", 302 },
            { "/errors/iis-on/Status500.aspx", 200 },
        };

        [Theory]
        [MemberData(nameof(ErrorRequests))]
        public async Task ErrorStatusIsIISs(string path, int status)
        {
            var (response, _) = await Send(HttpMethod.Get, path);
            Assert.Equal(status, (int)response.StatusCode);
        }

        // customErrors mode="On" redirects to defaultRedirect, or to the page of the error's status, with aspxerrorpath.
        [Theory]
        [InlineData("/errors/on/Throw.aspx", "/errors/Oops.aspx?aspxerrorpath=/errors/on/Throw.aspx")]
        [InlineData("/errors/on/Http404.aspx", "/errors/NotFound.aspx?aspxerrorpath=/errors/on/Http404.aspx")]
        [InlineData("/errors/on/Missing.aspx", "/errors/NotFound.aspx?aspxerrorpath=/errors/on/Missing.aspx")]
        [InlineData("/errors/iis-on/Throw.aspx", "/errors/Oops.aspx?aspxerrorpath=/errors/iis-on/Throw.aspx")]
        public async Task CustomErrorsRedirects(string path, string location)
        {
            var (response, _) = await Send(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(location, response.Headers.Location?.OriginalString);
            // The redirect's body is text/html, also for a page that doesn't exist, where no handler ran (#192: it had
            // no Content-Type).
            Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        }

        // redirectMode="ResponseRewrite": the error page runs in place of the failed one (Server.Execute), at its URL,
        // and the response has the status the error page leaves, 200 unless it sets one, as in .NET Framework's
        // HttpResponse.RedirectToErrorPage.
        [Fact]
        public async Task ResponseRewriteRunsTheErrorPageInPlace()
        {
            var (thrown, body) = await Send(HttpMethod.Get, "/errors/rewrite/Throw.aspx");
            Assert.Equal(HttpStatusCode.OK, thrown.StatusCode);
            Assert.StartsWith("Oops.aspx\nstatus: 200\naspxerrorpath: (none)\n", body);
            Assert.Contains("RawUrl: /errors/rewrite/Throw.aspx\n", body);
            // Page.HandleError's HttpUnhandledException has no message: .NET Framework's default, not .NET's
            // ExternalException's "External component has thrown an exception." (#192).
            Assert.Contains("last error: System.Web.HttpUnhandledException: Exception of type 'System.Web.HttpUnhandledException' was thrown.\n" +
                "inner error: System.InvalidOperationException: Thrown by the page.\n", body);
            var (notFound, notFoundBody) = await Send(HttpMethod.Get, "/errors/rewrite/Http404.aspx");
            Assert.Equal(HttpStatusCode.OK, notFound.StatusCode);
            Assert.StartsWith("NotFound.aspx\n", notFoundBody);
        }

        // Subtext (the smoke test, webForCore 0.10.0): its forbidden-path probes (/App_Data/, /bin_dotnet/...) got a 301
        // "Object moved" where 0.9.0 answered 404. Request filtering still answers 404.8 for a hidden segment before any
        // module runs, as on IIS; Subtext's Web.config then sends every 404 to its error page by httpErrors ExecuteURL
        // (errorMode="Custom"), and that page's URL (/Subtext.Web/pages/..., the folder Subtext was developed in) names a
        // blog subfolder, so Subtext's BlogRequestModule redirects the child request permanently. The response is the
        // child request's, with its status, as on IIS 10 (#192); until 0.10.0 webForCore put the 404 back on it, so
        // Subtext's 301 went out as a 404 with a Location. The child request sees the error page's Path and the
        // client's RawUrl.
        [Theory]
        [InlineData("/errors/iis-hidden/App_Data/data.txt")]
        [InlineData("/errors/iis-hidden/App_Data/")]
        public async Task HiddenSegmentRunsTheHttpErrorsPage(string path)
        {
            var (response, body) = await Send(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("IisError.aspx\nstatus: 200\n", body);
            Assert.Contains("Path: /errors/IisError.aspx\n", body);
            Assert.Contains("RawUrl: " + path + "\n", body);
        }

        [Theory]
        [InlineData("/errors/iis-hidden-moved/App_Data/data.txt")]
        [InlineData("/errors/iis-hidden-moved/App_Data/")]
        public async Task HiddenSegmentsHttpErrorsPageRedirectIsTheResponse(string path)
        {
            var (response, body) = await Send(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
            Assert.Equal("/errors/NotFound.aspx?path=%2ferrors%2fMoved.aspx&rawurl=" + Uri.EscapeDataString(path).Replace("%2F", "%2f"),
                response.Headers.Location?.OriginalString);
            Assert.Contains("<title>Object moved</title>", body);
        }

        // #219: request filtering's rejections and the MIME map's 404.3, answered by IIS's error page (errors/filtering) and
        // by an httpErrors ExecuteURL page (errors/filtering-custom, 200 from IisError.aspx), and two requests that aren't
        // rejected. Close: whether the client's connection closes after the response (Connection: close).
        public static readonly TheoryData<string, string, int, int, bool> FilteringRequests = new TheoryData<string, string, int, int, bool>
        {
            // method, path, status (filtering), status (filtering-custom), the connection closes
            { "GET", "blocked.txt", 404, 200, true },                        // 404.5 denyUrlSequences
            { "DELETE", "Page.aspx", 404, 200, true },                       // 404.6 verbs
            { "GET", "file.forbidden", 404, 200, true },                     // 404.7 fileExtensions
            { "GET", "App_Data/data.txt", 404, 200, true },                  // 404.8 hiddenSegments
            { "GET", "data.txt (X-Big)", 431, 431, true },                   // 431.0 headerLimits (IIS 7 to 8.5: 404.10)
            { "GET", "a%2525b.txt", 404, 200, true },                        // 404.11 double escaping
            { "GET", "data.txt?q=" + new string('q', 60), 404, 200, true },  // 404.15 maxQueryString
            { "GET", new string('l', 200) + ".txt", 404, 200, true },        // 404.14 maxUrl
            { "GET", "data.txt?x=blockedq", 404, 200, true },                // 404.18 denyQueryStringSequences
            { "POST", "Page.aspx (200 bytes)", 413, 200, true },             // 413.1 maxAllowedContentLength
            { "GET", "file.unknownext", 404, 200, false },                   // 404.3 the MIME map
            { "GET", "data.txt", 200, 200, false },
        };

        [Theory]
        [MemberData(nameof(FilteringRequests))]
        public async Task FilteringRejectionsAreIISs(string method, string request, int status, int customStatus, bool close)
        {
            foreach (var (folder, expected) in new[] { ("filtering", status), ("filtering-custom", customStatus) })
            {
                string path = "/errors/" + folder + "/" + request;
                HttpContent content = null;
                string requestBody = null, contentType = null;
                Dictionary<string, string> headers = null;
                if (path.EndsWith(" (X-Big)"))
                {
                    path = path[..^" (X-Big)".Length];
                    headers = new Dictionary<string, string> { ["X-Big"] = new string('b', 20) };
                }
                if (path.EndsWith(" (200 bytes)"))
                {
                    path = path[..^" (200 bytes)".Length];
                    requestBody = "a=" + new string('x', 198);
                    contentType = "application/x-www-form-urlencoded";
                    content = new StringContent(requestBody, Encoding.ASCII);
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
                var (response, _) = await Send(new HttpMethod(method), path, content, $"{method} /errors/{folder}/{request}", requestBody, contentType, headers: headers);
                Assert.True(expected == (int)response.StatusCode, $"{method} {path}: {(int)response.StatusCode}");
                Assert.True(close == (response.Headers.ConnectionClose == true), $"{method} {path}: Connection: {string.Join(", ", response.Headers.Connection)}");
            }
        }

        // The detailed error page (RemoteOnly for a local request, and Off), and the generic one (On, no defaultRedirect).
        [Fact]
        public async Task TheErrorPagesAreASPNETs()
        {
            var (_, remoteOnly) = await Send(HttpMethod.Get, "/errors/remoteonly/Throw.aspx");
            Assert.Contains("<title>Thrown by the page.</title>", remoteOnly);
            var (_, off) = await Send(HttpMethod.Get, "/errors/off/Throw.aspx");
            Assert.Contains("<title>Thrown by the page.</title>", off);
            var (_, generic) = await Send(HttpMethod.Get, "/errors/nodefault/Throw.aspx");
            Assert.Contains("<title>Runtime Error</title>", generic);
            Assert.DoesNotContain("Thrown by the page.", generic);
        }

        // <httpErrors existingResponse>: Auto replaces an error response unless the application set
        // TrySkipIisCustomErrors (ASP.NET's own error pages set it), as IIS's integrated pipeline does; Replace always
        // replaces; PassThrough never does. The replacement is the ExecuteURL child request's response, with its status:
        // 200 from a static page, as IIS's static file handler answers, and from a page that sets none.
        [Theory]
        [InlineData("/errors/iis-auto/Status500.aspx", 200)]
        [InlineData("/errors/iis-auto/Status500Skip.aspx", 0)]
        [InlineData("/errors/iis-replace/Status500.aspx", 200)]
        [InlineData("/errors/iis-replace/Status500Skip.aspx", 200)]
        [InlineData("/errors/iis-passthrough/Status500.aspx", 0)]
        [InlineData("/errors/iis-passthrough/Status500Skip.aspx", 0)]
        [InlineData("/errors/iis-on/Status500.aspx", 200)]
        [InlineData("/errors/iis-replace-page/Status500.aspx", 201)]
        public async Task HttpErrorsReplacesByExistingResponse(string path, int replacedWith)
        {
            var (response, body) = await Send(HttpMethod.Get, path);
            if (replacedWith == 200)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("The 500 page of &lt;httpErrors&gt;", body);
            }
            else if (replacedWith == 201)
            {
                // By the page.
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.StartsWith("IisError.aspx\nstatus: 200\n", body);
            }
            else
            {
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.StartsWith("500 set by the page", body);
            }
        }

        // Request bodies against <httpRuntime maxRequestLength> (/limits/app: 8 KB, under maxAllowedContentLength's
        // 16384 bytes; /limits/iis: 16 KB, over maxAllowedContentLength's 8192 bytes), as forms, multipart uploads and
        // raw bodies, at the limits and one byte over them.
        public static readonly TheoryData<string, string, int, int> LimitRequests = new TheoryData<string, string, int, int>
        {
            // maxRequestLength is the smaller: a body over it reaches the application, and reading it throws
            // "Maximum request length exceeded" (500); over maxAllowedContentLength it doesn't: IIS 10 answers 413.1
            // (IIS 7 to 8.5 answered 404.13).
            { "/limits/app/Form.ashx", "form", 8192, 200 },
            { "/limits/app/Form.ashx", "form", 8193, 500 },
            { "/limits/app/Form.ashx", "form", 16384, 500 },
            { "/limits/app/Form.ashx", "form", 16385, 413 },
            { "/limits/app/Files.ashx", "multipart", 8192, 200 },
            { "/limits/app/Files.ashx", "multipart", 8193, 500 },
            { "/limits/app/Input.ashx", "raw", 8192, 200 },
            { "/limits/app/Input.ashx", "raw", 8193, 500 },
            { "/limits/app/Page.aspx", "form", 8193, 500 },
            { "/limits/app/Ignore.ashx", "form", 16384, 200 },
            { "/limits/app/Catch.ashx", "form", 8193, 200 },
            // maxAllowedContentLength is the smaller: request filtering answers 413.1 before the application runs,
            // whether or not it would read the body.
            { "/limits/iis/Form.ashx", "form", 8192, 200 },
            { "/limits/iis/Form.ashx", "form", 8193, 413 },
            { "/limits/iis/Files.ashx", "multipart", 8192, 200 },
            { "/limits/iis/Files.ashx", "multipart", 8193, 413 },
            { "/limits/iis/Ignore.ashx", "form", 8193, 413 },
            // requestLengthDiskThreshold (16 KB): a body over it is buffered to a file.
            { "/limits/disk/Files.ashx", "multipart", 16384, 200 },
            { "/limits/disk/Files.ashx", "multipart", 16385, 200 },
            { "/limits/disk/Form.ashx", "form", 16384, 200 },
            { "/limits/disk/Form.ashx", "form", 16385, 200 },
        };

        [Theory]
        [MemberData(nameof(LimitRequests))]
        public async Task LimitStatusIsIISs(string path, string kind, int length, int status)
        {
            var (response, _) = await Post(path, kind, length);
            Assert.Equal(status, (int)response.StatusCode);
        }

        // Over maxRequestLength, Request.Form throws HttpException "Maximum request length exceeded" and ASP.NET closes
        // the connection after its error page; a handler that doesn't read the body never sees the limit.
        [Fact]
        public async Task MaxRequestLengthFailsWhenTheBodyIsRead()
        {
            var (thrown, body) = await Post("/limits/app/Form.ashx", "form", 8193);
            Assert.Equal(HttpStatusCode.InternalServerError, thrown.StatusCode);
            Assert.Contains("<title>Maximum request length exceeded.</title>", body);
            var (caught, caughtBody) = await Post("/limits/app/Catch.ashx", "form", 8193);
            Assert.Equal(HttpStatusCode.OK, caught.StatusCode);
            Assert.StartsWith("HttpException 500: Maximum request length exceeded.\n", caughtBody);
            var (ignored, ignoredBody) = await Post("/limits/app/Ignore.ashx", "form", 16384);
            Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
            Assert.StartsWith("body not read\nContent-Length: 16384\n", ignoredBody);
            var (within, withinBody) = await Post("/limits/app/Files.ashx", "multipart", 8192);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
            Assert.StartsWith("files: 1, file upload.bin ", withinBody);
        }

        // Over maxRequestLength, ASP.NET answers 500 and closes the connection without reading the body (its Content-Length
        // is over the limit); over maxAllowedContentLength, request filtering answers 413.1 and Kestrel closes it, since it
        // can't read a body over its limit. Each client gets its answer, as from IIS. The worker closed its connection to the
        // host after an answer that didn't say so, and the host's pool sometimes sent the next request on it: that request's
        // client was reset (LimitStatusIsIISs failed now and then on build-windows; about one request in a hundred here).
        [Fact]
        public async Task RequestsOverTheLimitsAreAnsweredEveryTime()
        {
            var cases = new[]
            {
                ("/limits/app/Files.ashx", "multipart", 8193, 500),
                ("/limits/app/Form.ashx", "form", 16384, 500),
                ("/limits/iis/Files.ashx", "multipart", 8193, 413),
                ("/limits/app/Files.ashx", "multipart", 8192, 200),
                ("/limits/iis/Form.ashx", "form", 8193, 413),
            };
            const string head = "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"upload.bin\"\r\n\r\n", tail = "\r\n--b--\r\n";
            var failures = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                foreach (var (path, kind, length, status) in cases)
                {
                    string body = kind == "form" ? "a=" + new string('x', length - 2) : head + new string('x', length - head.Length - tail.Length) + tail;
                    var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(kind == "form" ? "application/x-www-form-urlencoded" : "multipart/form-data; boundary=b");
                    try
                    {
                        using var response = await _server.Client.PostAsync(path, content);
                        if ((int)response.StatusCode != status) failures.Add($"{i} {path} {length}: {(int)response.StatusCode}");
                    }
                    catch (HttpRequestException e)
                    {
                        failures.Add($"{i} {path} {length}: {e.GetBaseException().Message}");
                    }
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} of {60 * cases.Length} requests:\n{string.Join("\n", failures)}");
        }

        // Over maxAllowedContentLength, request filtering's 413.1 "Request Entity Too Large", before the application runs:
        // IIS 10's answer (IIS 7 to 8.5 answered 404.13, as webForCore did before #192).
        [Fact]
        public async Task MaxAllowedContentLengthIsRequestFilterings413_1()
        {
            var (response, body) = await Post("/limits/iis/Ignore.ashx", "form", 8193);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Contains("HTTP Error 413.1 - Request Entity Too Large", body);
            Assert.DoesNotContain("body not read", body);
        }

        // requestLengthDiskThreshold: up to it the body is in memory; over it, in a file in the compilation folder.
        [Theory]
        [InlineData("/limits/disk/Files.ashx", "multipart", 16384, false)]
        [InlineData("/limits/disk/Files.ashx", "multipart", 16385, true)]
        [InlineData("/limits/disk/Form.ashx", "form", 16384, false)]
        [InlineData("/limits/disk/Form.ashx", "form", 16385, true)]
        public async Task ABodyOverTheDiskThresholdIsBufferedToAFile(string path, string kind, int length, bool toDisk)
        {
            var (response, body) = await Post(path, kind, length);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.EndsWith($"buffered to disk: {toDisk}\n", body);
        }

        // Limits raised above the defaults (maxRequestLength 60 MB, maxAllowedContentLength 62914560 bytes): a body over
        // 30,000,000 bytes, Kestrel's default limit and IIS's default maxAllowedContentLength, is read whole.
        [Fact]
        public async Task ABodyOverKestrelsDefaultLimitIsRead()
        {
            var (response, body) = await Post("/limits/large/Input.ashx", "raw", 31_000_000);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("input: 31000000 bytes\nContent-Length: 31000000\nbuffered to disk: True\n", body);
        }

        // A body of exactly the given length: a form field, a multipart upload of one file, or raw bytes; the recorded
        // text is the body itself, or a character and its count for a raw body.
        private async Task<(HttpResponseMessage Response, string Body)> Post(string path, string kind, int length)
        {
            string contentType, text;
            int repeat = 1;
            switch (kind)
            {
                case "form":
                    contentType = "application/x-www-form-urlencoded";
                    text = "a=" + new string('x', length - 2);
                    break;
                case "multipart":
                    const string boundary = "----webForCoreBoundary192";
                    contentType = "multipart/form-data; boundary=" + boundary;
                    string head = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"upload.bin\"\r\nContent-Type: application/octet-stream\r\n\r\n";
                    string tail = $"\r\n--{boundary}--\r\n";
                    text = head + new string('x', length - head.Length - tail.Length) + tail;
                    break;
                default:
                    contentType = "application/octet-stream";
                    text = "x";
                    repeat = length;
                    break;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(repeat > 1 ? new string(text[0], repeat) : text);
            Assert.Equal(length, bytes.Length);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return await Send(HttpMethod.Post, path, content, $"POST {path} ({kind}, {length} bytes)", text, contentType, repeat);
        }

        private async Task<(HttpResponseMessage Response, string Body)> Send(HttpMethod method, string path, HttpContent content = null,
            string key = null, string requestBody = null, string contentType = null, int repeat = 1, Dictionary<string, string> headers = null)
        {
            var request = new HttpRequestMessage(method, new Uri(_server.BaseAddress.GetLeftPart(UriPartial.Authority) + path)) { Content = content };
            if (headers != null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            var response = await _server.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            RequestHandlingTests.Record(key ?? method.Method + " " + path, response, body, headers, requestBody, contentType, repeat);
            return (response, body);
        }
    }
}
