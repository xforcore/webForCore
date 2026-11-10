# IIS compatibility on Kestrel

On Windows, an ASP.NET 4.x app gets a lot from IIS before its code runs: request filtering, URL Rewrite, default
documents, custom errors, compression, logging, recycling and so on. webForCore rebuilds those parts in front of the
application, on Kestrel, and reads their settings from the `<system.webServer>` section of the app's own `web.config`.
An app keeps its IIS configuration as it is.

The reference behaviour is IIS 10 with ASP.NET 4.8. Every feature marked **Works** below has at least one test in the
IIS gate (`test/webForCore.IIS.Gate.Tests`, 58 tests) that drives `test/webForCore.IIS.Sample` over HTTP.

Status key: **Works**: implemented and tested. **Partial**: some of it, or by other means. **Not built**: could be done
and hasn't been. **Not possible**: Windows-native and can't exist on Linux.

## Turning it on

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebForms();            // compression, W3C logging
var app = builder.Build();
app.UseWebForms(o => o.HandleAllRequestsWithWebForms());   // IIS modules on by default
app.Run();
```

| Option | Default | Effect |
| --- | --- | --- |
| `UseWebForms(o => o.IIS(i => i.Enabled = false))` | on | Turns off the `<system.webServer>` modules |
| `UseWebForms(o => o.VirtualPath("/app"))` | `/` | The application's virtual path, as IIS's application `/app` of a site: `AppDomainAppVirtualPath`, `Request.ApplicationPath` and every URL made from `~/` have it, and only the URLs at and below it are the application's ([MIGRATING.md](MIGRATING.md#an-application-under-a-path)) |
| `UseWebForms(o => o.Recycling(r => ...))` | IIS's defaults | `Time` (1740 min), `Requests`, `PrivateMemory` (KB), `CheckInterval` (30 s): reaching one recycles the worker process (see [Process model](#process-model)) |
| `AddWebForms(o => o.Compression = false)` | on | IIS's static and dynamic compression |
| `AddWebForms(o => o.LogDirectory = ...)` | `logs/<site>/W3SVC1` next to the site folder | W3C log folder; never served, even if it is inside the site |

## How configuration is read

`WebServerConfiguration` (`src/WebFormsForCore.Web/WebFormsForCore/AspNetCoreHosting/IIS/`) builds the effective
`<system.webServer>` for each URL the way IIS does:
- It starts from IIS 10's applicationHost.config defaults, then applies the app's `web.config`, its `<location>`
  elements and the `web.config` files in subfolders.
- Attributes override inherited ones; collections follow `<add>`, `<remove>` and `<clear>`.
- Files are re-read when they change.

There's no `applicationHost.config`, so locking sections (`overrideMode`, `allowDefinition`) has no effect.

## Request pipeline modules

| IIS feature | Status | Behaviour on webForCore |
| --- | --- | --- |
| **Request filtering** (`<requestFiltering>`) | Works | IIS's default deny lists and your settings: file extensions 404.7 (`.cs`, `.config`, `.asax`, ...), hidden segments 404.8 (`bin`, `App_Code`, `App_Data`, ..., plus the build output folder and `obj`), verbs 404.6, double escaping and `+` 404.11, high-bit characters 404.12, `denyUrlSequences` 404.5, `denyQueryStringSequences` 404.18, `maxAllowedContentLength` 413.1 (IIS 10's; IIS 7 to 8.5 answered 404.13), which is also the body's limit for the worker's Kestrel ([below](#aspnets-errors-and-request-limits)), `maxUrl` 404.14, `maxQueryString` 404.15, `headerLimits` 431.0 (IIS 10's, as CI's `iis-compare` job saw; IIS 7 to 8.5 answered 404.10, which webForCore did until 0.10.1), `alwaysAllowedUrls`. As IIS 10 does, the client's connection closes after a rejection (`Connection: close`), also when `<httpErrors>` answers it with a page of its own (#219); webforcore-host closes it, and keeps its connection to the worker |
| **Concurrent request limit** (`serverRuntime appConcurrentRequestLimit`) | Works | 503.2 above the limit (5000) |
| **Custom headers** (`<httpProtocol>`) | Works | `customHeaders` on every response, including IIS's `X-Powered-By: ASP.NET`; `redirectHeaders` on 3xx; `removeServerHeader` |
| **HTTP redirection** (`<httpRedirect>`) | Works | Destination with the rest of the URL appended, `exactDestination`, `childOnly`, wildcards, status Permanent/Found/Temporary/PermRedirect |
| **URL Rewrite, inbound** (`<rewrite><rules>`) | Works | Rules at every level (web.config files and `<location>`s), applied as IIS distributes them: a level's rules apply below it, matched relative to that level, parents' rules first. `stopProcessing`, redirects, rewrites (a rewrite to `?query` alone is to the level's root), conditions, rewrite maps, `isFile`/`isDirectory`, the ECMAScript, Wildcard and ExactMatch syntaxes, `{CACHE_URL}` (`scheme://host:port/path?query`). Rules ASP.NET Core's parser can't read (some server variables, such as request headers other than Accept, Connection, Cookie, Forwarded, Host, Referer and User-Agent) fail closed: requests below their level get 500, and the error is logged |
| **URL Rewrite, outbound** (`<outboundRules>`) | Works | `RESPONSE_*` headers, and HTML bodies by `filterByTags` (A, Area, Base, Form, Frame, Head, IFrame, Img, Input, Link, Script, `<customTags>`) or the whole body. preConditions, conditions, `{R:n}`/`{C:n}`, rewrite maps, `{ToLower:}`/`{UrlEncode:}`/`{UrlDecode:}`. A compressed body isn't rewritten (IIS: 500.52) |
| URL Rewrite: rules that set server variables (`<serverVariables>`) | Not built | The rule's other actions apply; the variables aren't set. A module can set them (`Request.ServerVariables["REMOTE_ADDR"] = ...`), with the effect they have on IIS ([MIGRATING.md](MIGRATING.md#behind-a-reverse-proxy)) |
| **IP and domain restrictions** (`<ipSecurity>`) | Works | First matching `<add>` decides (address with mask or prefix, or `domainName` with `enableReverseDns`), else `allowUnlisted`; `enableProxyMode` checks `X-Forwarded-For`; behind a reverse proxy whose headers are applied ([MIGRATING.md](MIGRATING.md#behind-a-reverse-proxy)), the address checked is the client's (#194); deny actions Forbidden (403.6, 403.8), Unauthorized, NotFound, AbortRequest |
| **Dynamic IP restrictions** (`<dynamicIpSecurity>`) | Works | `denyByConcurrentRequests` (403.501), `denyByRequestRate` (403.502), logging-only mode, proxy mode |
| **Default documents** (`<defaultDocument>`) | Works | IIS's list or yours. The document is found in any case, and the request is rewritten to its name as the list has it (`/` is `/default.aspx` with IIS's list), while `Request.RawUrl` stays the URL asked for (`/`), as on IIS (#127); 301 to the absolute URL with a missing trailing slash added, unless one of the application's routes takes the URL (`RouteExistingFiles`), as `UrlRoutingModule` does on IIS. The routes are known once the application has started (`Application_Start` runs with its first request) |
| **Directory browsing** (`<directoryBrowse>`) | Works | IIS's listing format and `showFlags`; hidden segments, the output folder and dot files are left out |
| Directory without a default document, browsing off | Partial | The request goes on to the application, as an extensionless URL does in integrated mode; IIS answers 403.14 |
| **Static content** (`<staticContent>`) | Works | `<mimeMap>` over IIS's default map, 404.3 for an unmapped file, `<clientCache>` (max-age, expires, no-cache, custom `Cache-Control`) |
| **HTTP errors** (`<httpErrors>`) | Works | `errorMode` (DetailedLocalOnly by default), `existingResponse` Auto/Replace/PassThrough, matching by status and substatus, File/ExecuteURL/Redirect. ASP.NET's own error pages set `TrySkipIisCustomErrors`, so Auto passes them through, as on IIS |
| **Managed modules** (`<modules>`) | Works | Entries with a managed type join `<system.web><httpModules>`; `<remove>`/`<clear>` act on the merged list, as integrated mode's single list does |
| **Managed handlers** (`<handlers>`) | Works | Managed entries are mapped before `<system.web><httpHandlers>`; `TransferRequestHandler` is skipped |
| **URL authorization** (`<security><authorization>`) | Works | Any matching deny denies, then allow, otherwise 401 |
| **Compression** (`<urlCompression>`, `<httpCompression>`) | Works | Through `AddWebForms()`: static on and dynamic off (IIS's defaults), MIME types, `minFileSizeForComp` (2,700 bytes), gzip |
| **W3C logging** | Works | Daily files with IIS's fields, in ASP.NET Core's field order; `cs-uri-query` keeps its `?`. No `sc-substatus`, `sc-win32-status` or `cs-username`. Outside the site by default, as IIS keeps them; a log folder inside the site answers 404.8. Entries are flushed every second; those not yet flushed when the process stops are lost |
| Output caching (`<caching>`, kernel cache) | Partial | ASP.NET's output cache works, with `<outputCache defaultProvider>`'s providers too: `Microsoft.Web.RedisOutputCacheProvider` shares pages between servers (not those with `VaryByParam`: 4.0.1's bug, on IIS too; SessionCache.Gate.Tests). IIS's kernel and user-mode caches aren't there |
| Server-side includes | Not built | |
| Failed request tracing, request monitor | Not built | Use ASP.NET Core logging and `dotnet-trace` |
| Configuration errors as 500.19 | Partial | System.Web reports `<system.web>` errors per request, as IIS does; a malformed `<system.webServer>` section is ignored |
| Event log, ETW providers, performance counters | Not possible | Use structured logging and .NET metrics |
| ISAPI, ISAPI filters, CGI, native modules | Not possible | Native Windows code |

## ASP.NET's errors and request limits

The `<system.web>` settings that decide error pages and request sizes, beside IIS's `<httpErrors>` and request filtering
(#192). Each row is compared with IIS 10 by CI's `iis-compare` job (IIS.Gate.Tests' `ErrorsAndLimitsTests` on
`test/webForCore.Requests.Sample`'s `/errors/` and `/limits/` folders, [below](#request-handling)), except
`HandleErrorAttribute` (Mvc.Gate.Tests, on Kestrel only).

| Setting | Status | Behaviour on webForCore, as on IIS 10 |
| --- | --- | --- |
| `<customErrors mode="On" defaultRedirect>`, `<error statusCode redirect>` | Works | An unhandled exception redirects (302) to `defaultRedirect`, an `HttpException` with a status (a 404, or a page that doesn't exist) to its `<error statusCode>` page, with `aspxerrorpath=<path>`. A status the application sets itself (`Response.StatusCode = 500`) isn't an error to customErrors. Without `defaultRedirect`: ASP.NET's generic "Runtime Error" page, with the error's status |
| `mode="RemoteOnly"` (the default), `mode="Off"` | Works | RemoteOnly gives a local request (from the server itself, through webforcore-host too) the detailed error page, and others the custom one. The detailed page shows webForCore's source lines, stack and version information |
| `redirectMode="ResponseRewrite"` | Works | The error page runs in place of the failed one (`Server.Execute`), at the failed URL, with `Server.GetLastError()`. The status is what the error page leaves: **200** unless it sets one, as on .NET Framework (set `Response.StatusCode` in the error page to keep the error's) |
| With `<httpErrors>` | Works | customErrors decides first: its redirect is a 302, which `<httpErrors>` leaves alone. Otherwise, by `existingResponse`: `PassThrough` keeps the application's error response; `Replace` replaces it; `Auto` replaces it unless the application set `Response.TrySkipIisCustomErrors = true`, which ASP.NET's own error pages, MVC's `HandleErrorAttribute` and Web API do, as IIS's **integrated** pipeline does (in IIS's classic pipeline Auto keeps every ASP.NET response that has a body). An `ExecuteURL` replacement is the child request's response, with its status, in both of IIS's pipelines: a static page answers 200, as IIS's static file handler does, and an ASP.NET page 200 unless it sets one (until 0.10.0 webForCore kept the error's status). That includes the errors IIS answers itself, such as request filtering's 404.8 for a hidden segment (`App_Data`, `bin`): the request never reaches the application, but with `errorMode="Custom"` the `ExecuteURL` page runs, with the client's `RawUrl`, and whatever it answers (200, or a redirect it makes) is the response, as on IIS. Subtext's Web.config sends every 404 to an error page whose URL its own module redirects, so its `/App_Data/` answers that 301 |
| MVC's `HandleErrorAttribute` | Works | Renders the `Error` view with a 500 and `TrySkipIisCustomErrors` only when customErrors is on for the request (`HttpContext.IsCustomErrorEnabled`), including RemoteOnly for a remote request; an `HttpException` other than a 500 goes on to customErrors |
| `<httpRuntime maxRequestLength>` (KB, 4096 by default) | Works | Checked when the body is read (`Request.Form`, `Files`, `InputStream`, `Params`, a Web Forms page's post-back): `HttpException` "Maximum request length exceeded." (500, the detailed or custom error page), after which ASP.NET closes the connection. A handler that doesn't read the body never sees it. At the limit the body is read; one byte over it fails |
| `maxRequestLength` and request filtering's `maxAllowedContentLength` (bytes, 30,000,000 by default) | Works | The smaller wins, with its own failure: over `maxAllowedContentLength`, request filtering answers **413.1** before the application runs (whether or not it would read the body; IIS 7 to 8.5 answered 404.13); between `maxAllowedContentLength` and a smaller `maxRequestLength`, the application runs and reading the body fails as above. Raise both for large uploads. `maxAllowedContentLength` is also the worker's Kestrel body limit, for a body without a `Content-Length` too; until 0.10.0 Kestrel's own 30,000,000 bytes stayed, and a larger upload broke the connection although both settings allowed it |
| `<httpRuntime requestLengthDiskThreshold>` (KB, 80 by default) | Works | A body over it is buffered to a file in the compilation folder's `uploads` folder (`Temporary_ASP.NET_Files`, [Process model](#process-model)) and deleted at the end of the request; up to it, in memory |
| Multipart uploads near the limits | Works | `Request.Files` at the limit and one byte over each limit, in either order of the two limits, as forms and raw bodies |

Known differences in the details (`tools/ci/iis-known-differences-requests.json`): the "Maximum request length exceeded"
page is sent chunked and the connection kept, where IIS ends it by closing the connection (same status and page);
request filtering's 413.1 is webForCore's short error page; a static `ExecuteURL` page has ASP.NET's static file
headers; customErrors' answer for a page that doesn't exist has no `X-AspNet-Version` or default `Cache-Control`, as in
the integrated pipeline (the classic one adds them).

## What the worker request gives ASP.NET

IIS hands ASP.NET a worker request (`HttpWorkerRequest`); on Kestrel this is `AspNetCoreWorkerRequest`.

| Feature | Status | Notes |
| --- | --- | --- |
| HTTP/1.1, keep-alive, chunked | Works | Kestrel |
| HTTP/2, HTTP/3 | Partial | Kestrel supports them; not tested with the fork |
| TLS, SNI, several bindings | Partial | Kestrel endpoints and certificates (`appsettings.json`); IIS bindings aren't read |
| HTTPS server variables, client certificates | Works | `IsSecureConnection`, `HTTPS`, `HTTPS_KEYSIZE`, `Request.ClientCertificate`, `CERT_*` variables in IIS's formats. The certificate is the one Kestrel negotiated (`ClientCertificateMode`); there's no renegotiation. `CERT_SERVER_*` and `HTTPS_SERVER_*` aren't available |
| Server variables | Works | IIS's set, including `INSTANCE_ID`, `APPL_MD_PATH`, `REMOTE_PORT`, `SERVER_SOFTWARE` (Kestrel); variables outside the classic list are asked of the worker request, as integrated mode does. Behind a reverse proxy, `REMOTE_ADDR` and `HTTPS` are the client's and the proxy's once its `X-Forwarded-*` headers are applied ([MIGRATING.md](MIGRATING.md#behind-a-reverse-proxy), #194) |
| WebSockets | Works | `HttpContext.AcceptWebSocketRequest` / `IsWebSocketRequest` as with IIS 8's module, `AspNetWebSocketContext`; Katana and SignalR use it. A restart aborts open sockets once the application's registered objects have stopped, as .NET Framework does (#181); clients reconnect to the new worker |
| `IsClientConnected` | Works | False once the client has gone or the request timed out |
| Bufferless input (`GetBufferlessInputStream`) | Works | |
| `executionTimeout` | Partial | .NET can't abort a thread. The client gets ASP.NET's 500 "Request timed out." on time, `Request.TimedOutToken` is cancelled, and the request ends at its next pipeline step; the page's code runs to the end, and its output is dropped |
| Integrated-pipeline APIs | Works | The fork runs the classic pipeline, but the APIs that need integrated mode work: `CurrentNotification`/`IsPostNotification`, `MapRequestHandler`/`LogRequest`/`PostLogRequest` events, writable `Request.Headers` and `ServerVariables` (setting `REMOTE_ADDR`, `HTTPS`, `LOCAL_ADDR`, `SERVER_NAME` or `SERVER_PORT` changes `Request.UserHostAddress`, `IsSecureConnection` and `Url`, as IIS7WorkerRequest reads them from IIS, #194), `Response.Headers` (a cache header set through it stays when ASP.NET's cache policy writes its own, as in IIS's header block), `AddOnSendingHeaders`, `InsertEntityBody`, `Request.Abort`, `Response.SubStatusCode`, `Server.TransferRequest` (a new request through the whole pipeline on the same connection; a chain deeper than 16 transfers fails, a guard against loops) |
| Response semantics | Works | Integrated mode's Content-Type rule, server name from the Host header, reason phrases, `X-AspNet-Version` and `Cache-Control: private` as integrated mode adds them (around a managed handler, on a flush and on an error; not on a response a module ends before the handler, as Katana's `OwinHttpModule` does) |
| Request URLs | Works | As IIS gives them to ASP.NET (#126): `Request.Path`, `FilePath`, `PathInfo` and `PATH_INFO` decoded (`/Product/Convertible Car`), with an escaped `/` and a `\` made `/` and repeated slashes merged, as http.sys does; `PathInfo` after the first segment with a managed handler's extension (`/Page.aspx/a.b/c`); `RawUrl` the URL asked for, decoded the same way, with the query string as sent, before any rewrite or default document (#127). `requestPathInvalidCharacters` gets ASP.NET's 400 and double escapes request filtering's 404.11. `HTTP_URL`, `UNENCODED_URL` and `CACHE_URL` are the integrated pipeline's and aren't set, as in IIS's classic pipeline |
| File names in URLs | Works | Case-insensitive on every platform, as on IIS's file system: a static file, page, handler or directory asked for in another case is found, the first time (#140). `Request.PhysicalPath` is the file's path as it is on disk |
| `Response.End`, `Redirect(url, true)`, `Server.Transfer`, `CompleteRequest` | Works | The handler stops and the pipeline goes on to `EndRequest` with the response still buffered, as after .NET Framework's thread abort, so `EndRequest`'s modules can change it: forms authentication turns a 401 ended with `Response.End()` (`WebSecurity.RequireAuthenticatedUser`) into its redirect to the log-in page (#129). Code that catches the `ThreadAbortException` and doesn't rethrow it runs on, as .NET can't rethrow it ([NAMESPACES.md](NAMESPACES.md)) |

## Process model

| IIS feature | Status | On webForCore |
| --- | --- | --- |
| App pool worker process | Works | `webforcore-host` (in the application's output, `bin_dotnet/webforcore-host/`) plays WAS's part: it owns the bindings and TLS and runs the application in worker processes, forwarding each request to the current one over a private Unix socket (#56). Run it with `dotnet bin_dotnet/webforcore-host/webforcore-host.dll`; starting the host itself (at boot, again if it ends) is the service manager's or container runtime's job, as it was Windows' for IIS. The client's address and port, the binding's, the scheme, the client certificate and the TLS strength reach `Request.ServerVariables` as on IIS; the Host header and the raw URL are the client's. Behind a reverse proxy, the host applies the `X-Forwarded-For`, `-Proto` and `-Host` headers of the proxies `WebForCore:Host:ForwardedHeaders` trusts before it forwards the request, and only those: the worker's own peer is always the host ([MIGRATING.md](MIGRATING.md#behind-a-reverse-proxy), #194) |
| AppDomain per application | Works | Each worker process runs one start of the application, so static state, `AppDomain.CurrentDomain` data (`DataDirectory`) and events are per start, as they were per AppDomain. The shared framework is the process's, as the GAC's assemblies were the AppDomain's |
| Restart on web.config, `bin` or `App_Code` change; `HttpRuntime.UnloadAppDomain` | Works | On every platform (`bin` and `App_Code` on Linux since #51). On Linux a worker holds a handful of inotify instances whatever the number of folders the site monitors (7 for a site monitoring files in 200 folders, #145, #152): System.Web watches the directories in the application's folder through three recursive watchers of that folder, one for each kind of change it watches for, and each change goes to the directories that asked for it, as their own watchers reported it; only a directory outside the folder (the compilation folder, the folder's parent) or below a symbolic link has a watcher of its own. ASP.NET Core's configuration reload and the `<system.webServer>` watcher take the others. Before 0.9.0 each monitored directory took an instance, and many two: #107's "12–15 per worker" held for small sites only, and Orchard 1.10, Kooboo CMS and nopCommerce needed 48 to more than 128. `<httpRuntime fcnMode>` applies (#146): `Single` watches the application's folder with one recursive watcher, and `Disabled` monitors nothing, so changes don't restart the application, as on IIS. `bin_dotnet` (the application's assemblies) counts as `bin`: a change in it restarts the application in `Default` and `Single` mode, and on Windows as on Linux the same changes do as on IIS 10 (CI's `iis-compare` job, #167). In `Single` mode, as on IIS, a change anywhere below `bin`/`bin_dotnet`, `App_Code` or another special folder restarts the application (a folder's LastWriteTime set included), and so does one in a folder whose name only starts with a special folder's (`binaries`), as IIS compares their names as prefixes; starting doesn't, though Windows reports a freshly copied site's folders as modified the first time the application opens them (their deferred LastWriteTime); IIS 10 restarts a freshly copied site for those at its first request in some runs. The limit, 128 per user by default (`fs.inotify.max_user_instances`), is shared by all of the user's processes, and a restart briefly holds two workers' instances: if several applications (or test runs) run as one user and one fails with "The configured user limit (128) on the number of inotify instances has been reached", raise it (`sysctl fs.inotify.max_user_instances=512`, and a file in `/etc/sysctl.d/` to keep it) or run them as different users. Each watched directory also takes an inotify watch (`fs.inotify.max_user_watches`, usually hundreds of thousands): a site with very many folders may need it raised. When monitoring can't start, the application doesn't start, the worker logs why, and each request shows "Failed to start monitoring changes" with the reason, as IIS showed a monitoring failure (#107). The worker tells the host, which starts the next worker; requests that arrive meanwhile wait for it, and none fails: one that reached the old worker as its application began shutting down goes to the new worker too, where it used to get "Server Too Busy" (503) now and then under load (#209), with its body (#214): webforcore-host reads a body of up to 49,152 bytes ahead (IIS's `uploadReadAheadSize`) and gives each worker it sends the request to its own copy, and sends a larger one, or one of unknown length, only once the application reads it (`Expect: 100-continue` to the worker, which the application doesn't see), so that a worker that turns a request away hasn't taken its body. Until 0.10.1 a POST or PUT got 503 then. The old worker finishes the requests it has, as an old AppDomain did, while the new one serves new requests (#181): `HttpRuntime` waits for them for `<httpRuntime shutdownTimeout>` (90 s) and a sixth of it again, then refuses any that are still to start and waits for those running, however long they take, as IIS 10 does in both pipelines (in CI's `iis-compare` job a 30 s request ran to its end with `shutdownTimeout="10"`); then the application ends (`Application_End`) and the worker exits. `<hostingEnvironment shutdownTimeout>` (30 s) is the wait for the application's registered objects (`IRegisteredObject`), not for its requests; once they have stopped, open WebSockets are aborted, as on IIS, and their clients reconnect to the new worker. On IIS `executionTimeout` aborts such a request's thread, which .NET can't do (see `executionTimeout` above); webforcore-host kills an old worker still running its shutdown time limit (90 s) after the drain time (`shutdownTimeout` and a sixth of it), or after a recycle by `Recycling(...)` itself, an application pool recycle, which WAS gave the shutdown time limit alone. Up to 0.9.0 the old worker's requests were aborted after `<hostingEnvironment shutdownTimeout>`, and after 30 s at the most: DNN's installer, which streams its progress and changes `bin`, was cut. The workers share the compilation folder, as AppDomains did: the next one empties it when App_Code, `bin` or Web.config changed, but leaves the assemblies a running worker has loaded, marked `.delete` for a later start to remove, as .NET Framework could not delete them either, so the old worker still compiles pages against them (#89). Off Windows that is a lock each worker holds on them; it also makes an assembly a worker replaces itself (a page edited while it runs) count towards `numRecompilesBeforeAppRestart` (15), as on IIS. A restart costs a process start (and compiling what isn't precompiled). An application run on its own (`dotnet run`, without the host) stops on a restart, for whatever started it to start it again |
| Recycling by time, request count, private memory | Works | `Recycling(...)`, with IIS's defaults: a new worker, overlapped as on IIS (the old one serves until the new one is ready and warm, #193) |
| Freeing old domains | Works | The old worker process exits: everything it held goes with it |
| Compilation folder (Temporary ASP.NET Files) | Works | `<compilation tempDirectory>` when it is set, as on IIS. Otherwise `Temporary_ASP.NET_Files` in the temp folder (`Path.GetTempPath()`: `%TEMP%` on Windows, `$TMPDIR` or `/tmp` elsewhere), the folder .NET Framework fell back to when it couldn't write to its own (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\Temporary ASP.NET Files`), which .NET doesn't have. Off Windows `/tmp` is shared by every user, so the name ends with the user's name, `Temporary_ASP.NET_Files-www-data`, with characters other than ASCII letters, digits, `.`, `_` and `-` replaced by `_` (#57). Up to 0.7.0 it ended with the user's number (`-33`): the first start after the upgrade compiles the application again, and the old folder can be deleted |
| Health monitoring, rapid-fail protection | Works | The host replaces a worker that exits; after 5 failures in 5 minutes it starts no more and answers 503 (IIS stops the application pool). Startup and shutdown time limits (90 s) are IIS's. Settings: `WebForCore:Host` (`StartupTimeLimit`, `ShutdownTimeLimit`, `RapidFailProtection`, `RapidFailProtectionMaxCrashes`, `RapidFailProtectionInterval`, `QueueTimeout`, `RequestIdleTimeout`, `AcceptUntrustedClientCertificates`, `ForwardedHeaders` ([MIGRATING.md](MIGRATING.md#behind-a-reverse-proxy)), and those of the idle timeout, start mode and Application Initialization below). |
| Request queue, 503 on overflow | Partial | `appConcurrentRequestLimit` (503.2); no HTTP.sys queue |
| Idle timeout, AlwaysRunning, preload, Application Initialization | Works | Settings in `WebForCore:Host` (#193). **`StartMode`** (`AlwaysRunning`, the default, or `OnDemand`), the pool's `startMode`: with `AlwaysRunning` the first worker starts with the host, as it always has; with `OnDemand`, at the first request. IIS's default is `OnDemand`, but the host runs one application as a service, as production pools set to `AlwaysRunning` do. Either way the application itself (`Application_Start`) starts with its first request, unless it is warmed. **`<applicationInitialization>`** is read from the application's `Web.config` (its `<system.webServer>` and `<location path=".">`, with `<add>`, `<remove>` and `<clear>`): a new worker is sent the `initializationPage` requests, one after the other, with `hostName` as the Host header, IIS's user agent `IIS Application Initialization Warmup` and the client address 127.0.0.1, before it takes traffic, whatever starts it (the host, a request, a replacement); after an application restart (a `Web.config`, `bin` or `App_Code` change, `HttpRuntime.UnloadAppDomain`), only with `doAppInitAfterRestart="true"`, as on IIS. A page that fails (an error, a 404, a timeout) is logged and the worker serves after it, as on IIS; the warm-up gets the `StartupTimeLimit` (90 s), after which the worker serves anyway. Requests that arrive meanwhile wait for the warm worker, or get `remapManagedRequestsTo`'s file (every request, static files too: the host can't tell which are managed). `skipManagedModules` has no effect: every module is managed, and the requests run the whole pipeline. **`PreloadEnabled`** (`preloadEnabled`, default false): without initialization pages, a new worker is sent a request for `/`. `InitializationPages` (`InitializationPage`, `HostName`), `DoAppInitAfterRestart` and `RemapManagedRequestsTo` replace `Web.config`'s. **An application pool recycle is overlapped**, as on IIS: when `Recycling(...)` reaches a limit, the old worker serves on while the host starts and warms the new one, then finishes its requests and exits (the shutdown time limit, 90 s); until 0.10.0 requests waited for the new worker. An application restart isn't overlapped (on IIS the old AppDomain took no new requests either): requests wait while the new worker starts and warms up, and the old one finishes its own (#181). A warming worker gets no traffic, so a load balancer's health checks reach the serving worker or wait. **`IdleTimeout`** (`idleTimeout`, default `00:00:00`, off) and **`IdleTimeoutAction`** (`Terminate`, `Suspend`): a worker without a request for that long is stopped and its application ends (`Application_End`); the next request starts a new one, or with `AlwaysRunning` one starts at once, as WAS restarts an `AlwaysRunning` pool's worker. IIS's default is 20 minutes; the host's is off, as before, as `AlwaysRunning` pools are usually set, since stopping a host's only application loses its in-process sessions and caches and makes the next client wait for a start. `Suspend` leaves the worker running: IIS paged a suspended worker out and kept its application, which .NET can't do. Initialization failures, idle stops and overlapped recycles don't count towards rapid-fail protection; a worker that exits while it warms up does |
| Web gardens | Not built | One app folder per process: run several processes behind a load balancer, with out-of-process session state |
| Web farm: session state and output cache shared by several servers | Works | As on IIS, through `web.config`: `<sessionState mode="SQLServer">`, `Microsoft.Web.RedisSessionStateProvider`, the async session state module with `SqlSessionStateProviderAsync` (2.1.0-preview1 or later on .NET), `Microsoft.Web.RedisOutputCacheProvider`; or ASP.NET Core's distributed cache (`UseAspNetCoreSessionProvider()`). Locking, `Session.Abandon`, expiry and the serializability rule as on IIS. `HttpRuntime.AppDomainAppId` is `/LM/W3SVC/1/ROOT`, as on IIS servers whose site IDs match ([MIGRATING.md](MIGRATING.md#session-state)) |
| Web farm (several servers behind a load balancer) | Works | Several hosts, each with its own copy of the site, as several IIS servers: the same `<machineKey>` everywhere, out-of-process session state, and for SignalR a scale-out backplane (Redis or SQL Server, Microsoft's 2.4.3 packages, [MIGRATING.md](MIGRATING.md#scaling-out-signalr)); no sticky sessions needed, as on IIS. The IIS gate tests run two instances on one Redis and on one SQL Server: messages, groups, a restart and requests alternating between the instances (`ScaleoutTests`). SignalR's Azure Service Bus backplane isn't built (.NET Framework only) |
| Pool identity | Partial | Run each service as its own Linux user |
| CPU and memory limits | Partial | The service manager's or container runtime's (cgroups on Linux) |
| Several sites, applications, virtual directories in one process | Partial | One application per process, at the site's root or under a virtual path (`UseWebForms(o => o.VirtualPath("/app"))`, as IIS's application `/app`, [MIGRATING.md](MIGRATING.md#an-application-under-a-path), #190); a site's other applications run as processes of their own, behind a proxy that routes by path. Virtual directories inside an application aren't built |

## Authentication

| IIS feature | Status | Notes |
| --- | --- | --- |
| Anonymous | Works | |
| Forms, ASP.NET Identity, OWIN cookies, OAuth | Works | All in managed code |
| Sharing `<machineKey>` output with .NET Framework 4.8 (forms tickets, `MachineKey.Protect` and `Encode`, OWIN and ASP.NET Identity cookies) | Works | With the same `<machineKey>`, each runtime reads the other's output, both ways: validation SHA1, HMACSHA256 and HMACSHA512, decryption AES and 3DES, `compatibilityMode` Framework20SP1, Framework20SP2 and Framework45, and none with `<httpRuntime targetFramework="4.5">` or later. Katana's cookies (`UseCookieAuthentication` on `Microsoft.Owin.Host.SystemWeb`, Identity's application cookie with `SecurityStampValidator` too) are protected with the machine key, not DPAPI, as on IIS. Output that is the same on every call (`MachineKey.Encode` with `Validation`) is the same bytes; the rest has a random IV, on both. Not shared: an application without `targetFramework` and without `compatibilityMode` (Framework20SP1 on .NET Framework, Framework45 here: forms tickets; set `compatibilityMode` on both), and `IsolateApps` or `IsolateByAppId` after an explicit key (the key is changed with a hash of the virtual path or the application ID, which .NET Framework computes from Windows' sort keys, `StringComparer.InvariantCultureIgnoreCase`, so it differs even with IIS's application ID). Fixed in 0.10.0: HMACSHA256 and HMACSHA512 in the Framework20 modes and `MachineKey.Encode` (#191). Tested by `MachineKeyInteropTests` and CI's `machinekey-framework-interop` job ([TESTING.md](TESTING.md)) |
| URL authorization (`<system.web>` and `<system.webServer>`) | Works | |
| Windows (Negotiate, Kerberos, NTLM) | Not built | Kestrel's Negotiate handler exists but isn't connected to System.Web's `User` |
| Basic, Digest | Not built | There are no Windows accounts to check against |
| Impersonation | Not possible | There's no Windows identity to impersonate |
| WS-Federation through Windows Identity Foundation (`WSFederationAuthenticationModule`, `SessionAuthenticationModule`, `<system.identityModel>`) | Not available | .NET has no `System.IdentityModel` (WIF), #119. Katana's `Microsoft.Owin.Security.WsFederation` signs in with WS-Federation on OWIN ([NAMESPACES.md](NAMESPACES.md#summary-of-what-doesnt-work)) |

## Deployment tools

| IIS tool | webForCore |
| --- | --- |
| `aspnet_compiler` | `aspnetcore_compiler` (`src/WebFormsForCore.AspNetCompiler`), with aspnet_compiler's switches. See [Precompiled sites](#precompiled-sites) |
| `aspnet_regiis -pe/-pd/-pc/-px/-pi` | `webforcore-regiis` (`src/WebFormsForCore.RegIIS`). See [Protected configuration](#protected-configuration) |
| `aspnet_regsql` | `webforcore-regsql` (`src/WebFormsForCore.RegSql`, the .NET tool `webForCore.RegSql`), with aspnet_regsql's switches: `-A`/`-R` (membership, roles, profile, personalization, web events; `-Q`), `-ssadd`/`-ssremove` (`-sstype t`, `p` or `c`), `-sqlexportonly`, and `-ed`/`-dd`/`-et`/`-dt`/`-lt` (SQL cache dependency), on `-S`/`-U`/`-P`/`-E` or `-C`. The scripts are .NET Framework 4.8's, which System.Web carries for `SqlServices` too, and `-sqlexportonly` writes what aspnet_regsql writes, byte for byte (#123). Not the wizard (`-W`) |
| `appcmd`, IIS Manager | None. Edit `web.config`, and the service manager's configuration |

### Precompiled sites

`aspnetcore_compiler` writes the precompiled site into the folder `-b` names. A deployment uses the host's output folder
(`-b bin_dotnet`) and puts the host's build there too; the app then runs as usual (`dotnet bin_dotnet/<app>.dll` from
the site folder).

| Precompilation | What the target holds | On webForCore |
| --- | --- | --- |
| Fixed (no `-u`) | Marker files for the pages, `.compiled` maps, `App_Code.dll`, `App_global.asax.dll`, `App_GlobalResources.dll`, `App_Web_*.dll` | Pages are served from the precompiled assemblies. A page added later gets IIS's 500 "The file '...' has not been pre-compiled, and cannot be requested" |
| Updatable (`-u`) | The pages' markup with `CodeFile` replaced by `inherits="Class, App_Web_x"`; no code files | The markup is compiled at run time against the precompiled code-behind; edits and new pages take effect |

A compile error fails the precompilation with a non-zero exit code, reported as aspnet_compiler reports it
(`file(line): error CSxxxx: message`, with the physical path). Tested by `PrecompilationTests`.

### Protected configuration

`DataProtectionConfigurationProvider` (DPAPI) and `RsaProtectedConfigurationProvider` work on every platform. On
Windows they use Windows' DPAPI and key containers, as on .NET Framework. Elsewhere:

- **DPAPI is emulated** (`WebFormsForCore.Security.LocalDataProtection`): one master key per scope, as DPAPI keeps one
  per machine and one per user, in a file only its owner can read. Keys live in `$WEBFORCORE_KEYS` if set; otherwise
  machine-scope keys go in `/var/lib/webforcore/keys` when it is writable, and everything else in `~/.webforcore/keys`.
  A key is created the first time it's needed. Payloads are AES-256-CBC with an HMAC-SHA256, keyed by HKDF from the
  master key, a salt and the entropy; the wrong key or entropy fails with DPAPI's "Key not valid for use in specified
  state." Katana's `DpapiDataProtectionProvider` uses the same emulation, in user scope.
- **Katana on System.Web doesn't use DPAPI** (#191): `Microsoft.Owin.Host.SystemWeb` gives the middleware the machine
  key's data protection (`MachineKey.Protect`), so cookies, and ASP.NET Identity's tokens when `IdentityFactoryOptions`'
  `DataProtectionProvider` (`app.GetDataProtectionProvider()`) creates the token provider as the templates do, are
  protected with `<machineKey>` and shared with .NET Framework as in [Authentication](#authentication). Only an
  application that creates a `DpapiDataProtectionProvider` itself (or Katana's HttpListener host, which has no machine
  key) uses DPAPI, and that output never leaves the machine and user that protected it, on .NET Framework too. Even
  there it isn't .NET Framework's: on Windows webForCore calls DPAPI with different entropy (a SHA-256 of the
  application name and purposes), because .NET Framework's `DpapiDataProtector` isn't in the reference source.
- **A section encrypted by Windows DPAPI** is recognised by its blob header and fails with an explanation: those keys
  never leave the Windows machine. Decrypt it there (`aspnet_regiis -pdf`), then protect it again here
  (`webforcore-regiis -pef`) or with RSA and an exported key container.
- **RSA key containers are files**, `<container>.rsakey.xml` in the machine or user key directory, holding the key as
  XML. A bare `RSAKeyValue`, as `aspnet_regiis -px` exports it, is accepted, so a key exported from Windows can be
  imported. Keys are created on first use (2048-bit) unless they must already exist.
- **`webforcore-regiis`** has aspnet_regiis's configuration switches: `-pef`/`-pdf` (encrypt or decrypt a section of a
  site folder's web.config), `-pc` (create a container; `-exp` exportable, `-size`), `-pz` (delete one), `-px` (export;
  `-pri` with the private key), `-pi` (import), `-prov` and `-pku` (user scope). There's no IIS metabase, so `-pe`/`-pd`
  with `-app` aren't supported.

Tested by `ProtectedConfigurationTests`: RSA- and DPAPI-encrypted sections read by a page, the Windows-DPAPI
explanation, and a regiis export and import between two key directories.

## Compatibility decisions

Where .NET Framework, IIS and .NET disagree, these choices decide what webForCore does.

| Decision | Why |
| --- | --- |
| The reference is **.NET Framework 4.8 with `<httpRuntime targetFramework>` 4.5 or later**. The 4.0 compatibility quirks of apps without `targetFramework` (for example `HttpUtility`'s looser decoding) aren't emulated | The 4.5+ behaviour is the one Microsoft's reference source and tests describe, and any app can set `targetFramework` |
| **IIS 10**, in **classic pipeline mode**, with the integrated-only APIs provided on the Kestrel worker request ([above](#what-the-worker-request-gives-aspnet)) | Classic mode is how the reference source's `HttpRuntime` runs outside IIS; the integrated-only APIs are what apps call |
| **`HttpUtility` is Microsoft's** (in System.Web), not .NET's `System.Web.HttpUtility.dll` | It goes through `HttpEncoder.Current`, so `<httpRuntime encoderType>` applies and the encoding is .NET Framework's. The framework's copy is left out of the compiler's references (CS0433 otherwise) |
| **`System.Data.SqlClient`**, not Microsoft.Data.SqlClient, behind `SqlDataSource` and the SQL providers (membership, roles, profile, session, cache dependency) | The objects apps get and cast are the types they had on .NET Framework |
| **Files without a BOM are read in the ANSI code page** when `<globalization fileEncoding>` isn't set: Windows' system code page on Windows, and on Linux the one Windows has for the locale (`LANG`; windows-1252 for English and `LANG=C`), not .NET's UTF-8 `Encoding.Default` (#90, [MIGRATING.md](MIGRATING.md#6-code-changes)) | .NET Framework read them with `Encoding.Default`, the ANSI code page, so that is how files on an IIS server were saved. An application whose files are UTF-8 without a BOM sets `fileEncoding="utf-8"`, as it had to on IIS |
| **Session state is in process by default, in .NET Framework's `InProcSessionStateStore`**, as `<sessionState mode="InProc">` on IIS: values are the objects themselves, not serialized (#180). **Out of process, web.config's `mode="SQLServer"` and Microsoft's Redis and SQL providers run as on IIS** ([MIGRATING.md](MIGRATING.md#session-state)); **through `UseAspNetCoreSessionProvider()` it is out-of-process state in ASP.NET Core's distributed cache**, keyed by System.Web's session ID, not ASP.NET Core's session, and every value must be serializable, as with StateServer and SQLServer ([MIGRATING.md](MIGRATING.md#session-state)) | One session cookie, `<sessionState cookieName>` (`ASP.NET_SessionId`), and `<sessionState timeout>`, as on IIS; ASP.NET Core's session would add its own `.AspNetCore.Session` cookie and timeout (#79). The provider replaces StateServer (the ASP.NET State Service) and SQLServer, or shares sessions between servers; an application that was InProc on IIS doesn't need it |
| **Newlines are the platform's** in product output (`\n` on Linux): Razor's generated code, indented JSON and XML, exception messages. Only tests change | Rewriting product output would diverge from Microsoft's code. Tests compare newline-agnostically, or give Windows input where they hard-code positions |
| **BinaryFormatter output is .NET Framework's**: webForCore's .NET Framework 4.0 assemblies are written as `Version=4.0.0.0`, and .NET Framework types .NET no longer serializes have surrogates ([NAMESPACES.md](NAMESPACES.md#other-assemblies)) | Session state, view state and the output cache keep the format out-of-process stores and other servers expect |
| **Assemblies have the identity of the Microsoft assembly they replace** (name, version, culture, public key token), except that **System.Web is 11.0.0.0**, and System.Configuration.ConfigurationManager (.NET Framework's System.Configuration under .NET's name) 14.5.0.0 (#141) | Libraries built against Microsoft's assemblies only compile and bind against the same identity (BundleTransformer against System.Web.Optimization 1.1.0.0, CS0012). System.Web must outrank the shared framework's 4.0.0.0 facade of the same name, which the host would load otherwise, and System.Configuration.ConfigurationManager the package of that name that other packages bring; a higher version still binds a reference to Microsoft's. CI checks every packaged assembly (`tools/ci/check_assembly_identities.cs`) |

### Compared with IIS 10

CI's `iis-compare` job ([TESTING.md](TESTING.md#compared-with-iis-10), #78) sends the Web API gate's 67 requests
(`test/webForCore.WebApi.Sample`, through `GlobalConfiguration` and through OWIN under `/owin`) to the same sample built
for .NET Framework 4.8 with Microsoft's Web API 5.3.0 and Katana 4.2.3 packages, on IIS 10 (Windows Server 2025), and
compares the responses. In IIS's **integrated** pipeline every status, `Content-Type`, `Location`, `Allow`, body
(stack traces aside) and other header matches, including `Cache-Control: no-cache`, `Pragma` and `Expires: -1` on Web
API's own responses, `X-Powered-By`, `X-AspNet-Version` and `Cache-Control: private`, except these, which
`tools/ci/iis-known-differences.json` lists (the job fails on any other difference, and on a listed one that stops
occurring):

| Difference | Requests | Why |
| --- | --- | --- |
| `Server` is `Kestrel`, not `Microsoft-IIS/10.0` | all 67 | webForCore's host is Kestrel |
| An unmatched extensionless URL (no route matches) gets ASP.NET's 404 page, with `X-AspNet-Version`; IIS serves its native 404 page ("HTTP Error 404.0 - Not Found", `StaticFile` handler), without it. The status is 404 on both | 2 | In IIS's integrated pipeline a URL that no managed handler claims falls through to IIS's static file handler; webForCore gives every request to ASP.NET |
| `/api/trace/listeners`, the names in `System.Diagnostics.Trace.Listeners`: IIS has the listener the sample's Web.config adds in `<system.diagnostics>` (`["Default","webconfig","code"]`); webForCore hasn't (`["Default","code"]`) | 1 | webForCore doesn't apply `<system.diagnostics>` ([NAMESPACES.md](NAMESPACES.md#summary-of-what-doesnt-work)): .NET reads it only through `TraceConfiguration.Register()` in .NET's System.Configuration.ConfigurationManager, which webForCore replaces with .NET Framework's System.Configuration. Listeners added in code receive Web API's trace records on both |

ASP.NET adds `X-AspNet-Version` and the default `Cache-Control: private` around a managed handler, when the response is
flushed and when it reports an error. The responses Katana's `OwinHttpModule` ends under `/owin`, before a handler
runs, have neither, on IIS and on webForCore, apart from the one that is flushed (`/api/status/text`: Web API doesn't
buffer content that knows its length, so each write flushes).

In IIS's **classic** pipeline, webForCore's reference ([above](#compatibility-decisions)), this sample doesn't run at
all. Katana's `OwinHttpModule` needs the integrated pipeline, so every request fails with a 500 ("This operation requires
IIS integrated pipeline mode"); and with the template's `Web.config`, `PUT`, `DELETE` and `OPTIONS` never reach ASP.NET
(IIS's 404.0), because the classic-mode extensionless handler accepts only `GET`, `HEAD`, `POST` and `DEBUG`. The job
records that run too, for reference. webForCore runs such an application as IIS's integrated pipeline does, apart from
the differences above, with the integrated-only APIs Katana uses provided on the Kestrel worker request.

#### OpenID Connect

The job also compares sign-in with Katana's OpenID Connect middleware: `test/webForCore.Oidc.Sample` (the "Work or
School Accounts" template's `Startup.Auth.cs` as a customer application configures it for Entra ID) built for .NET
Framework 4.8 with Microsoft's Katana 4.2.3 packages, IdentityModel 5.6 and IdentityServer3.Contrib.AccessTokenValidation,
on IIS's **integrated** pipeline (Katana's System.Web host needs it there), against `test/webForCore.Oidc.Provider`, goes
through the scenarios Oidc.Gate.Tests ran on webForCore: the challenge, the form_post, the page with the user's claims,
the saved tokens, sign-out, eight rejected responses with the exception type `AuthenticationFailed` saw,
`SkipToNextMiddleware`, the unhandled failure, a tampered state, JWT and IdentityServer3 bearer tokens, and Katana's
default ticket format. Random values (ports, nonces, codes, tokens, protected cookies and state) are replaced with
placeholders, and IdentityModel's telemetry parameters (`x-client-SKU`, `x-client-ver`) and exception messages, which
name the IdentityModel build, are left out (NAMESPACES.md). Every status, `Location` with its parameters, `Set-Cookie`
with its attributes (`SameSite=None` on the nonce cookie, the expiry dates), body (the claims, in the same order and
mapping), `Cache-Control`, `Pragma`, `Expires`, `X-AspNet-Version` and `X-Powered-By` matches, except these, which
`tools/ci/iis-known-differences-oidc.json` lists:

| Difference | Requests | Why |
| --- | --- | --- |
| `Server` is `Kestrel`, not `Microsoft-IIS/10.0` | all 47 | webForCore's host is Kestrel |
| The 401 page the challenge's redirect carries ends with `Microsoft .NET Version:10.0.x; ASP.NET Version:1.2.0.0`, not `.NET Framework Version:4.0.30319; ASP.NET Version:4.8.x` | 2 | The page names the runtime |

The first run found one more: `Expires: -1`, which Katana's cookie middleware sets through `Response.Headers` when it
signs in or out, was missing on webForCore, because the classic pipeline writes the cache policy (modified by the cookie)
in place of the cache headers an application added. In IIS's integrated pipeline the header is in IIS's header block,
and the policy replaces only the headers it writes; webForCore now does the same for headers set through
`Response.Headers` (Oidc.Gate.Tests' `CookieMiddlewareResponsesKeepExpires`).

#### Third-party packages

The job also compares `test/webForCore.Packages.Sample`, with third-party net4x packages a customer application keeps
as they are (Ninject.Web.Common.WebHost, .OwinHost and Ninject.Web.WebApi.OwinHost; WebActivatorEx; Swashbuckle.Core
5.6.0; CacheCow.Server.WebApi 2.5.0), built for .NET Framework 4.8 with the same packages and Microsoft's Web API 5.3.0
and Katana 4.2.3, on IIS's **integrated** pipeline (Katana's System.Web host needs it there). It gets the requests
Packages.Gate.Tests sent on webForCore, in the same order: Ninject's request scopes through System.Web, an OWIN
middleware and Web API; Swashbuckle's document, its UI and every file the UI loads; and CacheCow's ETags, 304s and the
new ETags after a `PUT` and a `POST`. Every status, body and header matches, `X-AspNet-Version` and
`Cache-Control: private` on Swashbuckle's UI files (Web API streams them, so they are flushed) included, except the one
`tools/ci/iis-known-differences-packages.json` lists:

| Difference | Requests | Why |
| --- | --- | --- |
| `Server` is `Kestrel`, not `Microsoft-IIS/10.0` | all 39 | webForCore's host is Kestrel |

The first run found one more: CacheCow's 304s had `Content-Length: 0` on webForCore (Web API's OWIN adapter sets it
through `Response.Headers` for an empty response) and none on IIS. IIS's integrated pipeline sends no `Content-Length`
with a 304; its classic pipeline sends one a page appends with `AppendHeader` (request handling's
`/status/NotModified.aspx`, `A304KeepsTheContentLengthAPageAppends`). webForCore now does both: a `Content-Length` set
through the emulated integrated `Response.Headers` is left out of a 304, and one appended is sent.

#### Request handling

The job also compares request handling (#126, #127, #129, #140): `test/webForCore.Requests.Sample`, a Web Forms site of
pages, `App_Code` and static files that IIS serves as it is, gets the requests IIS.Gate.Tests' `RequestHandlingTests`
sent to it on Kestrel (escaped and invalid characters, path info, default documents, files in another case,
`Response.End`, `Redirect`, `Server.Transfer` and `CompleteRequest` before forms authentication's `EndRequest`; and
customErrors, httpErrors and the request length limits, #192, [above](#aspnets-errors-and-request-limits)). The
reference is IIS's **classic** pipeline, webForCore's; every status, `Location`, body and other header matches, except
these, which `tools/ci/iis-known-differences-requests.json` lists:

| Difference | Why |
| --- | --- |
| `Server` is `Kestrel`, not `Microsoft-IIS/10.0` | webForCore's host is Kestrel |
| A static file has `Cache-Control: public`, another `ETag`, `X-AspNet-Version`, and no `Accept-Ranges` or `Last-Modified` | ASP.NET's static file handler serves it (the worker request leaves out its `Last-Modified`, as Cassini, which it comes from, did); IIS's static file module serves it on IIS |
| A file that doesn't exist gets ASP.NET's 404 page, with `X-AspNet-Version`; IIS serves its native 404.0 page. Same status | As above |
| Request filtering's rejections (404.5, 404.6, 404.7, 404.8, 404.11, 404.14, 404.15, 404.18, 431.0) and the MIME map's 404.3 are webForCore's short error page (`text/html`), without IIS's `Cache-Control: private`; with `<httpErrors errorMode="Custom">` and no page for 431, IIS sends its own static 431 page. Same status and substatus | IIS's detailed error pages aren't reproduced |
| ASP.NET's 400 page for a character of `requestPathInvalidCharacters` shows webForCore's source lines, stack frames and version information | The error and its message are the same; the details are the platform's |
| ASP.NET's detailed error page (customErrors RemoteOnly locally, or Off; "Maximum request length exceeded") shows webForCore's source lines, stack frames and version information; the generic Runtime Error page's sample Web.config has `\n` newlines | As above; newlines are the platform's ([decisions](#compatibility-decisions)) |
| Over `maxRequestLength`, the error page ASP.NET sends before closing the connection is chunked; IIS sends it without a length. Both close the client's connection after it (`Connection: close`, #219) | Kestrel frames every HTTP/1.1 response; same status and page |
| Request filtering's 413.1 is webForCore's short error page (`text/html`), without IIS's `Cache-Control: private` | As for 404.11 |
| An `ExecuteURL` static page (httpErrors) has ASP.NET's static file headers | As for any static file |
| `existingResponse="Auto"` replaces an error response the application sent without `TrySkipIisCustomErrors` | As IIS's integrated pipeline does; IIS's classic pipeline leaves every ASP.NET response with a body |
| customErrors' answer for a page that doesn't exist has no `X-AspNet-Version` or default `Cache-Control: private` | As IIS's integrated pipeline, whose rule webForCore follows ([decisions](#compatibility-decisions)) |

The integrated pipeline is recorded too: it also sets `HTTP_URL`, `UNENCODED_URL` and `CACHE_URL` (`ServerVariables.aspx`).

#### Virtual path

The job also compares an application under a virtual path (#190) and a module that sets server variables behind a
proxy (#194): `test/webForCore.VirtualPath.Sample`, Web Forms pages, MVC and Web API routes, System.Web.Optimization
bundles, forms authentication (`requireSSL`) and session state, and Swashbuckle, built for .NET Framework 4.8 with
Microsoft's MVC 5.3.0, Web API 5.3.0, System.Web.Optimization 1.1.3 and Swashbuckle.Core 5.6.0 packages, is IIS's
application `/app` of a site whose root is empty (integrated pipeline, which MVC's and Web API's extensionless URLs
need on IIS), and gets the requests IIS.Gate.Tests' `VirtualPathTests` sent to it on Kestrel with
`UseWebForms(o => o.VirtualPath("/app"))`: the paths and URLs System.Web, MVC, Web API, bundling, forms authentication
and Swashbuckle make, the application asked for in another case and without its trailing slash, a directory without
its slash, URLs outside the application, and `REMOTE_ADDR` and `HTTPS` set by a module. The values of the forms and
session cookies, which are random, are left out; their attributes are compared. Every status, `Location`, body and
other header matches, except these, which `tools/ci/iis-known-differences-vpath.json` lists:

| Difference | Why |
| --- | --- |
| `Server` is `Kestrel`, not `Microsoft-IIS/10.0` | webForCore's host is Kestrel |
| A URL outside the application gets an empty 404, without `Content-Type`, `Cache-Control`, `X-AspNet-Version` or `X-Powered-By`; on IIS the site's root application (empty, but ASP.NET's) answers with ASP.NET's 404 page. Same status | On webForCore it isn't the application's: it goes on to the rest of the ASP.NET Core pipeline after `UseWebForms` |
| The tags `Scripts.Render` and `Styles.Render` write end with `\n` on Linux, `\r\n` on IIS | Newlines are the platform's ([above](#compatibility-decisions)) |
| A bundle's `Expires` and `Last-Modified` | They are the time the bundle was built, the time of each run |

The first run found nothing else: the paths, URLs, redirects (`/app/Content` to `/app/Content/`, the log-in page with
its `ReturnUrl`), cookies, MVC's route taking `/app` without its slash, Web API's links, bundle URLs and contents,
Swashbuckle's document, and the module's `REMOTE_ADDR` and `HTTPS` (`UserHostAddress`, `IsSecureConnection`, `Url`,
`requireSSL`, `[RequireHttps]`) all match IIS 10's. `APPL_MD_PATH` is compared without the site's number, which is IIS's.

#### LinqDataSource

And LinqDataSource over LINQ to SQL (#142): `test/webForCore.Linq.Scenarios` sends `test/webForCore.Requests.Sample`'s
`linq/` pages, on IIS's **classic** pipeline with .NET Framework 4.8's System.Web.Extensions and System.Data.Linq, the
requests IIS.Gate.Tests' `LinqDataSourceTests` sent on Kestrel, against SQL Server Express with the same database: a
GridView and a DetailsView on a `LinqDataSource` (`Where` with a query-string parameter, `OrderBy`, paging and sorting
by the data source, `Selecting`, `Updating`, `Updated`, `Inserted` and `Deleted` handlers), postbacks that page, sort,
edit, update (and fail with LINQ to SQL's `ChangeConflictException` on a row whose `rowversion` changed), delete and
insert, `AutoGenerateWhereClause` with a `Select` projection, a `Selecting` handler's own `e.Result`, and Dynamic Data's
`MetaModel.RegisterContext` with the `DataContext`. Each page's form is compared, apart from the values of `__VIEWSTATE`
and `__EVENTVALIDATION` (each server's machine key protects them) and `WebResource.axd`'s parameters, with line breaks
read as `\n` (System.Web's `WriteLine(string)` calls write the platform's newline, [above](#compatibility-decisions)). Every page,
grid row, pager, edit box, event log line and the conflict's message are the same as IIS's; the one difference, which
`tools/ci/iis-known-differences-linq.json` lists, is `__VIEWSTATEGENERATOR`'s value (`D1602CB0` on IIS, `73C738F5` on
webForCore for `linq/Products.aspx`): `Page.GetClientStateIdentifier` hashes the page's directory and class name with
`StringUtil.GetNonRandomizedHashCode`, which on .NET Framework is `StringComparer.InvariantCultureIgnoreCase.GetHashCode`
(Windows' NLS sort keys) and on webForCore System.Web's own stable hash. System.Web reads the value back only when a
postback's view state fails its MAC check, to tell an error in the page's own view state (reported) from view state
meant for another page (ignored): a page posted back to the server that rendered it isn't affected.

How each part came to be is in git: the fork commits in `external/` and this repository's history
([MODIFICATIONS.md](MODIFICATIONS.md)).
