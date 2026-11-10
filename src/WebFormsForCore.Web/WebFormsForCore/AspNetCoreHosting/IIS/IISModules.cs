#if NETCOREAPP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Web.Hosting;
using static WebFormsForCore.Hosting.IIS.WebServerConfiguration;

namespace WebFormsForCore.Hosting.IIS
{
	// webForCore: IIS's native modules that sit in front of ASP.NET, configured from <system.webServer>
	// (WebServerConfiguration), in IIS's order: the concurrent request limit (serverRuntime appConcurrentRequestLimit),
	// IP and dynamic IP restrictions (IPSecurity), request filtering, HTTP redirection, URL Rewrite's inbound rules and,
	// around the rest, its outbound rules (UrlRewrite.cs), the default document (with IIS's courtesy redirect of a
	// directory to its trailing slash), directory browsing, custom and redirect headers, the static file MIME map and
	// client cache headers, and custom errors (httpErrors). WebFormsMiddleware runs them around the application. Static and dynamic compression and W3C
	// logging are ASP.NET Core's middleware, configured the IIS way (IISServices.AddWebForms).
	public sealed class IISModules
	{
		public sealed class Options
		{
			public bool Enabled { get; set; } = true;
			// <serverRuntime appConcurrentRequestLimit="5000"/>: requests over it get 503.2. 0 disables.
			public int AppConcurrentRequestLimit { get; set; } = 5000;
			// Extensions served by managed handlers (machine-level handler mappings); everything else that exists as a
			// file is static content for the MIME map, client cache and static compression.
			public HashSet<string> DynamicExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
				".aspx", ".ashx", ".asmx", ".axd", ".cshtml", ".vbhtml", ".svc", ".xamlx", ".xoml", ".rem", ".soap", ".asax", ".ascx", ".master"
			};
		}

		public const string IsStaticKey = "webForCore.IIS.Static";

		readonly WebServerConfiguration config;
		readonly Options options;
		readonly ILogger logger;
		readonly ILoggerFactory loggerFactory;
		readonly IWebHostEnvironment environment;
		readonly FileExtensionContentTypeProvider defaultMimeMap = new FileExtensionContentTypeProvider();
		int active;
		readonly InboundRewrite inbound;
		readonly OutboundRewrite outbound;
		readonly IPSecurity ipSecurity;

		public IISModules(WebServerConfiguration config, Options options, ILoggerFactory loggerFactory, IWebHostEnvironment environment)
		{
			this.config = config;
			this.options = options;
			this.loggerFactory = loggerFactory;
			this.environment = environment;
			logger = loggerFactory?.CreateLogger<IISModules>() ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
			inbound = new InboundRewrite(config, logger);
			outbound = new OutboundRewrite(logger);
			ipSecurity = new IPSecurity(logger);
		}

		public WebServerConfiguration Configuration => config;

		// Whether a route of the application takes a virtual path (set by WebFormsMiddleware: AspNetCoreHost.RouteClaims in
		// the application's domain).
		public Func<string, bool> RouteClaims { get; set; }

		public async Task Invoke(HttpContext context, RequestDelegate application)
		{
			if (!options.Enabled)
			{
				await application(context);
				return;
			}
			if (options.AppConcurrentRequestLimit > 0 && Interlocked.Increment(ref active) > options.AppConcurrentRequestLimit)
			{
				Interlocked.Decrement(ref active);
				await WriteError(context, 503, 2, "Service Unavailable", "The serverRuntime@appConcurrentRequestLimit setting is being exceeded.");
				return;
			}
			try
			{
				await CustomErrors(context, ctx => Modules(ctx, application));
			}
			finally
			{
				if (options.AppConcurrentRequestLimit > 0) Interlocked.Decrement(ref active);
			}
		}

		async Task Modules(HttpContext context, RequestDelegate application)
		{
			var section = SectionFor(context.Request.Path.Value);
			// IIS adds the custom headers to every response, the ones its own modules answer included.
			ResponseHeaders(context, section);
			if (await ipSecurity.Deny(context, section, WriteError)) return;
			if (await RequestFiltering(context, section)) return;
			if (await HttpRedirect(context, section)) return;
			await outbound.Invoke(context, section, async () =>
			{
				if (inbound.Apply(context, section)) return; // a rule ended the response
				await AfterRewrite(context, application);
			});
		}

		async Task AfterRewrite(HttpContext context, RequestDelegate application)
		{
			if (context.Response.HasStarted || context.Response.StatusCode != 200) return; // the rewrite module answered
			var section = SectionFor(context.Request.Path.Value);
			if (await DefaultDocument(context, section)) return;
			if (await DirectoryBrowse(context, section)) return;
			section = SectionFor(context.Request.Path.Value);
			if (await StaticContent(context, section)) return;
			await application(context);
		}

		XElement SectionFor(string path)
		{
			path = string.IsNullOrEmpty(path) ? "/" : path;
			if (!path.EndsWith("/") && Directory.Exists(MapPath(path))) path += "/";
			return config.GetSection(path);
		}

		// Case-insensitively, as IIS's file system is: each segment as it is on disk (#140).
		string MapPath(string path)
		{
			var relative = path.TrimStart('/');
			var mapped = Path.Combine(config.PhysicalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
			if (!OperatingSystem.IsWindows() && !File.Exists(mapped) && !Directory.Exists(mapped))
				mapped = System.Web.Configuration.UserMapPath.ResolveIgnoreCase(config.PhysicalRoot, relative);
			return mapped;
		}

		// Case-insensitively, as IIS's file system is (MapPath).
		string FindFile(string path)
		{
			var mapped = MapPath(path);
			return File.Exists(mapped) ? mapped : null;
		}

		// <security><requestFiltering>: IIS answers 404 with a substatus.
		async Task<bool> RequestFiltering(HttpContext context, XElement section)
		{
			var filtering = Element(section, "security/requestFiltering");
			if (filtering == null) return false;
			var request = context.Request;
			var path = request.Path.Value ?? "/";
			var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? path;
			var rawPath = rawTarget.Split('?')[0];
			var query = request.QueryString.HasValue ? request.QueryString.Value.Substring(1) : "";

			var limits = Element(section, "security/requestFiltering/requestLimits");
			bool alwaysAllowed = Items(section, "security/requestFiltering/alwaysAllowedUrls").Any(e => string.Equals(Attr(e, "url"), path, StringComparison.OrdinalIgnoreCase));
			// #192: the body's limit is maxAllowedContentLength, as IIS's; Kestrel's own (MaxRequestBodySize, 30,000,000
			// bytes by default) cut a larger body that the application allowed (an upload of 31 MB with
			// maxAllowedContentLength and maxRequestLength raised) with a broken connection. Kestrel enforces it on
			// bodies without a Content-Length too; a body with one is answered 413.1 below.
			var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
			if (bodySize != null && !bodySize.IsReadOnly)
				bodySize.MaxRequestBodySize = alwaysAllowed ? null : Long(limits, "maxAllowedContentLength", 30000000);
			if (alwaysAllowed)
				return false;

			if (request.ContentLength > Long(limits, "maxAllowedContentLength", 30000000))
			{
				// #192: IIS 10 answers 413.1 "Request Entity Too Large" (IIS 7 to 8.5: 404.13), as CI's iis-compare job saw.
				// webForCore: Kestrel drains the body after this answer, as it drains any body the application didn't read,
				// so the connection to webforcore-host stays usable. With the limit in place it couldn't, and closed the
				// connection: the host's pool sent its next request on it now and then, or the close reset this answer before
				// the host had read it, and a client was reset (ErrorsAndLimitsTests on Windows). The client's connection is the
				// host's, which keeps it either way.
				if (bodySize != null && !bodySize.IsReadOnly) bodySize.MaxRequestBodySize = null;
				// #219: IIS closes the client's connection after it, as after its other rejections (CI's iis-compare job).
				WorkerProcess.CloseClientConnection(context);
				return await WriteError(context, 413, 1, "Request Entity Too Large", "The request filtering module is configured to deny a request that exceeds the request content length.")
					.ContinueWith(_ => true, TaskContinuationOptions.ExecuteSynchronously);
			}
			if (rawPath.Length > Long(limits, "maxUrl", 4096))
				return await Reject(context, 14, "The request filtering module is configured to deny a request where the URL is too long.");
			if (query.Length > Long(limits, "maxQueryString", 2048))
				return await Reject(context, 15, "The request filtering module is configured to deny a request where the query string is too long.");
			foreach (var header in Items(section, "security/requestFiltering/requestLimits/headerLimits"))
			{
				var name = Attr(header, "header");
				if (name != null && request.Headers.TryGetValue(name, out var value) && value.ToString().Length > Long(header, "sizeLimit", long.MaxValue))
				{
					// IIS 10 answers 431.0 "Request Header Fields Too Large" (IIS 7 to 8.5: 404.10), as CI's iis-compare job saw (#219).
					WorkerProcess.CloseClientConnection(context);
					return await WriteError(context, 431, 0, "Request Header Fields Too Large", "The request filtering module is configured to deny a request header that is too large.")
						.ContinueWith(_ => true, TaskContinuationOptions.ExecuteSynchronously);
				}
			}

			var verbs = Element(section, "security/requestFiltering/verbs");
			var verb = Items(section, "security/requestFiltering/verbs").FirstOrDefault(e => string.Equals(Attr(e, "verb"), request.Method, StringComparison.OrdinalIgnoreCase));
			if (verb != null ? !Bool(verb, "allowed", true) : !Bool(verbs, "allowUnlisted", true))
				return await Reject(context, 6, "The request filtering module is configured to deny the HTTP verb.");

			if (!Bool(filtering, "allowHighBitCharacters", true) && rawTarget.Any(c => c > 0x7f))
				return await Reject(context, 12, "The request filtering module is configured to deny a request that contains high bit characters.");
			if (!Bool(filtering, "allowDoubleEscaping", false))
			{
				// IIS decodes the URL a second time and rejects it if that changes it; '+' counts as an escape.
				if (path.Contains('+') || Uri.UnescapeDataString(path) != path)
					return await Reject(context, 11, "The request filtering module is configured to deny a request that contains a double escape sequence.");
			}
			foreach (var sequence in Items(section, "security/requestFiltering/denyUrlSequences").Select(e => Attr(e, "sequence")).Where(s => !string.IsNullOrEmpty(s)))
				if (path.Contains(sequence, StringComparison.OrdinalIgnoreCase))
					return await Reject(context, 5, "The request filtering module is configured to deny the URL sequence.");
			foreach (var sequence in Items(section, "security/requestFiltering/denyQueryStringSequences").Select(e => Attr(e, "sequence")).Where(s => !string.IsNullOrEmpty(s)))
				if (Uri.UnescapeDataString(query).Contains(sequence, StringComparison.OrdinalIgnoreCase))
					return await Reject(context, 18, "The request filtering module is configured to deny the query string sequence.");

			var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var hidden = Items(section, "security/requestFiltering/hiddenSegments").Select(e => Attr(e, "segment")).Concat(config.ExcludedDirectories);
			if (segments.Any(segment => hidden.Any(h => string.Equals(h, segment, StringComparison.OrdinalIgnoreCase))))
				return await Reject(context, 8, "The request filtering module is configured to deny a path in the URL that contains a hiddenSegment section.");
			var relative = string.Join("/", segments);
			foreach (var excluded in config.ExcludedDirectories.Where(d => d.Contains('/')).Select(d => d.Trim('/')).Where(d => d.Length > 0))
				if (relative.Equals(excluded, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
					return await Reject(context, 8, "The request filtering module is configured to deny a path in the URL that contains a hiddenSegment section.");

			var fileExtensions = Element(section, "security/requestFiltering/fileExtensions");
			var last = segments.LastOrDefault() ?? "";
			var extension = Path.GetExtension(last);
			if (extension.Length > 0)
			{
				var rule = Items(section, "security/requestFiltering/fileExtensions").FirstOrDefault(e => string.Equals(Attr(e, "fileExtension"), extension, StringComparison.OrdinalIgnoreCase));
				if (rule != null ? !Bool(rule, "allowed", true) : !Bool(fileExtensions, "allowUnlisted", true))
					return await Reject(context, 7, "The request filtering module is configured to deny the file extension.");
			}
			return false;
		}

		// #219: IIS 10 closes the client's connection after a rejection, also when httpErrors answers it with a page of its
		// own (CI's iis-compare job).
		Task<bool> Reject(HttpContext context, int subStatus, string reason)
		{
			WorkerProcess.CloseClientConnection(context);
			return WriteError(context, 404, subStatus, "Not Found", reason).ContinueWith(_ => true, TaskContinuationOptions.ExecuteSynchronously);
		}

		// <httpRedirect>
		async Task<bool> HttpRedirect(HttpContext context, XElement section)
		{
			var redirect = Element(section, "httpRedirect");
			if (!Bool(redirect, "enabled", false)) return false;
			var path = context.Request.Path.Value ?? "/";
			string destination = null;
			foreach (var wildcard in Items(section, "httpRedirect"))
			{
				var pattern = Attr(wildcard, "wildcard");
				if (pattern != null && WildcardMatch(path, pattern)) { destination = Attr(wildcard, "destination"); break; }
			}
			bool exact = Bool(redirect, "exactDestination", false);
			if (destination == null)
			{
				destination = Attr(redirect, "destination");
				if (string.IsNullOrEmpty(destination)) return false;
				if (Bool(redirect, "childOnly", false) && (path == "/" || path == "")) return false;
				if (!exact)
				{
					// The rest of the URL below the level that configured the redirect.
					var level = Attr(redirect, LevelPathAttribute.ToString(), "/");
					var rest = path.StartsWith(level, StringComparison.OrdinalIgnoreCase) ? path.Substring(level.Length) : path;
					destination = destination.TrimEnd('/') + "/" + rest.TrimStart('/');
				}
			}
			if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase) || destination.EndsWith("://" + context.Request.Host + path, StringComparison.OrdinalIgnoreCase))
				return false;
			if (!exact && context.Request.QueryString.HasValue) destination += context.Request.QueryString.Value;
			context.Response.StatusCode = Attr(redirect, "httpResponseStatus", "Found") switch
			{
				"Permanent" => 301,
				"Temporary" => 307,
				"PermRedirect" => 308,
				_ => 302,
			};
			context.Response.Headers.Location = destination;
			await WriteBody(context, "text/html; charset=UTF-8", $"<head><title>Document Moved</title></head>\n<body><h1>Object Moved</h1>This document may be found <a HREF=\"{WebUtility.HtmlEncode(destination)}\">here</a></body>");
			return true;
		}

		static bool WildcardMatch(string path, string pattern) =>
			System.Text.RegularExpressions.Regex.IsMatch(path, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);

		// <defaultDocument>, and IIS's courtesy redirect of a directory URL without its trailing slash.
		async Task<bool> DefaultDocument(HttpContext context, XElement section)
		{
			var path = context.Request.Path.Value ?? "";
			// webForCore: an empty path is the application's own URL without its trailing slash: "/app" for an application
			// under a virtual path (#190), a directory URL like any other.
			var mapped = MapPath(path.Length == 0 ? "/" : path);
			if (!Directory.Exists(mapped)) return false;
			if (!path.EndsWith("/"))
			{
				// A route of the application takes it first (RouteExistingFiles, or the application's own URL), as
				// UrlRoutingModule does on IIS (#13).
				if (RouteClaims?.Invoke(context.Request.PathBase + path) == true) return false;
				// An absolute URL, as IIS sends it (#127).
				var location = context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + path + "/" + context.Request.QueryString;
				context.Response.StatusCode = 301;
				context.Response.Headers.Location = location;
				await WriteBody(context, "text/html; charset=UTF-8", $"<head><title>Document Moved</title></head>\n<body><h1>Object Moved</h1>This document may be found <a HREF=\"{WebUtility.HtmlEncode(location)}\">here</a></body>");
				return true;
			}
			var defaultDocument = Element(section, "defaultDocument");
			if (!Bool(defaultDocument, "enabled", true)) return false;
			foreach (var document in Items(section, "defaultDocument/files").Select(e => Attr(e, "value")).Where(v => !string.IsNullOrEmpty(v)))
			{
				var file = FindFile(path + document);
				if (file != null)
				{
					// The document's name as configured, as IIS rewrites to it ("/" is "/default.aspx" with
					// applicationHost.config's list), whatever its case on disk (#127); the file is found in any case (#140).
					context.Request.Path = path + document;
					return false;
				}
			}
			return false;
		}

		// <directoryBrowse enabled="true">: a directory without a default document is listed, in IIS's format, with the
		// columns of showFlags (Date, Time, Size, Extension, LongDate). Hidden segments, the application's output folder and
		// dot files are left out. When browsing is disabled the request goes on to the application, as an extensionless URL
		// does in IIS's integrated pipeline (routing).
		async Task<bool> DirectoryBrowse(HttpContext context, XElement section)
		{
			var path = context.Request.Path.Value ?? "/";
			if (!path.EndsWith("/")) return false;
			var browse = Element(section, "directoryBrowse");
			if (!Bool(browse, "enabled", false)) return false;
			var directory = MapPath(path);
			if (!Directory.Exists(directory)) return false;
			var flags = Attr(browse, "showFlags", "Date, Time, Size, Extension").Split(',').Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var hidden = Items(section, "security/requestFiltering/hiddenSegments").Select(e => Attr(e, "segment")).Concat(config.ExcludedDirectories)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
			var basePath = context.Request.PathBase + path;
			var host = context.Request.Host.Host;
			var html = new StringBuilder();
			html.Append($"<html><head><title>{WebUtility.HtmlEncode(host)} - {WebUtility.HtmlEncode(basePath)}</title></head><body><H1>{WebUtility.HtmlEncode(host)} - {WebUtility.HtmlEncode(basePath)}</H1><hr>\r\n\r\n<pre>");
			if (path != "/")
			{
				var parent = context.Request.PathBase + path.Substring(0, path.TrimEnd('/').LastIndexOf('/') + 1);
				html.Append($"<A HREF=\"{WebUtility.HtmlEncode(parent)}\">[To Parent Directory]</A><br><br>");
			}
			var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
				.Where(e => !e.Name.StartsWith(".") && !hidden.Contains(e.Name) && (e.Attributes & FileAttributes.Hidden) == 0)
				.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
			foreach (var entry in entries)
			{
				bool isDirectory = entry is DirectoryInfo;
				var modified = entry.LastWriteTime;
				var line = new StringBuilder();
				if (flags.Contains("LongDate")) line.Append(modified.ToString("dddd, MMMM dd, yyyy", culture).PadLeft(30));
				else if (flags.Contains("Date")) line.Append(modified.ToString("M/d/yyyy", culture).PadLeft(10));
				if (flags.Contains("Time")) line.Append(modified.ToString("h:mm tt", culture).PadLeft(10));
				if (flags.Contains("Size")) line.Append((isDirectory ? "&lt;dir&gt;" : ((FileInfo)entry).Length.ToString(culture)).PadLeft(isDirectory ? 21 : 13));
				var name = isDirectory || flags.Contains("Extension") ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name);
				var href = basePath + Uri.EscapeDataString(entry.Name) + (isDirectory ? "/" : "");
				html.Append(line).Append($" <A HREF=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(name)}</A><br>");
			}
			html.Append("</pre><hr></body></html>");
			await WriteBody(context, "text/html; charset=UTF-8", html.ToString());
			return true;
		}

		// <httpProtocol><customHeaders>, <redirectHeaders>; <requestFiltering removeServerHeader>.
		void ResponseHeaders(HttpContext context, XElement section)
		{
			if (!context.Items.TryAdd("webForCore.IIS.Headers", true)) return; // once per request (ExecuteURL runs it again)
			var custom = Items(section, "httpProtocol/customHeaders").Select(e => (Attr(e, "name"), Attr(e, "value"))).Where(h => h.Item1 != null).ToList();
			var redirect = Items(section, "httpProtocol/redirectHeaders").Select(e => (Attr(e, "name"), Attr(e, "value"))).Where(h => h.Item1 != null).ToList();
			bool removeServer = Bool(Element(section, "security/requestFiltering"), "removeServerHeader", false);
			if (custom.Count == 0 && redirect.Count == 0 && !removeServer) return;
			context.Response.OnStarting(() =>
			{
				var headers = context.Response.Headers;
				foreach (var (name, value) in custom) headers.Append(name, value);
				int status = context.Response.StatusCode;
				if (status >= 300 && status < 400)
					foreach (var (name, value) in redirect) headers.Append(name, value);
				if (removeServer) headers.Remove("Server");
				return Task.CompletedTask;
			});
		}

		// The static file module's part: <staticContent> MIME map (an extension it does not map is 404.3) and <clientCache>.
		async Task<bool> StaticContent(HttpContext context, XElement section)
		{
			var path = context.Request.Path.Value ?? "/";
			var extension = Path.GetExtension(path);
			if (options.DynamicExtensions.Contains(extension) || IsManagedHandlerExtension(section, extension)) return false;
			var file = FindFile(path);
			if (file == null) return false;
			context.Items[IsStaticKey] = true;
			var mimeMap = Items(section, "staticContent").FirstOrDefault(e => string.Equals(Attr(e, "fileExtension"), extension, StringComparison.OrdinalIgnoreCase));
			string contentType = Attr(mimeMap, "mimeType");
			if (contentType == null && !defaultMimeMap.TryGetContentType(path, out contentType))
			{
				await WriteError(context, 404, 3, "Not Found", "The page you are requesting cannot be served because of the extension configuration. If the page is a script, add a handler. If the file should be downloaded, add a MIME map.");
				return true;
			}
			var clientCache = Element(section, "staticContent/clientCache");
			context.Response.OnStarting(() =>
			{
				var response = context.Response;
				if (response.StatusCode == 200 || response.StatusCode == 304)
				{
					if (response.StatusCode == 200) response.ContentType = contentType;
					switch (Attr(clientCache, "cacheControlMode", "NoControl"))
					{
						case "DisableCache":
							response.Headers.CacheControl = "no-cache";
							break;
						case "UseMaxAge":
							if (TimeSpan.TryParse(Attr(clientCache, "cacheControlMaxAge", "1.00:00:00"), System.Globalization.CultureInfo.InvariantCulture, out var maxAge))
								response.Headers.CacheControl = "max-age=" + (long)maxAge.TotalSeconds;
							break;
						case "UseExpires":
							var expires = Attr(clientCache, "httpExpires");
							if (expires != null) response.Headers.Expires = expires;
							break;
					}
					var customControl = Attr(clientCache, "cacheControlCustom");
					if (!string.IsNullOrEmpty(customControl))
						response.Headers.CacheControl = string.IsNullOrEmpty(response.Headers.CacheControl) ? customControl : response.Headers.CacheControl + "," + customControl;
				}
				return Task.CompletedTask;
			});
			return false;
		}

		// <handlers> entries with a managed type for "*.ext".
		static bool IsManagedHandlerExtension(XElement section, string extension) =>
			extension.Length > 0 && Items(section, "handlers").Any(h => Attr(h, "type") != null
				&& string.Equals(Attr(h, "path"), "*" + extension, StringComparison.OrdinalIgnoreCase));

		// <httpErrors>: replaces error responses with the configured page, by errorMode and existingResponse.
		async Task CustomErrors(HttpContext context, RequestDelegate inner)
		{
			var errors = Element(SectionFor(context.Request.Path.Value), "httpErrors");
			var mode = Attr(errors, "errorMode", "DetailedLocalOnly");
			bool custom = mode == "Custom" || mode == "DetailedLocalOnly" && !IsLocal(context);
			var existing = Attr(errors, "existingResponse", "Auto");
			if (!custom || existing == "PassThrough" && !errors.Elements("error").Any())
			{
				await inner(context);
				return;
			}
			var section = SectionFor(context.Request.Path.Value);
			var bodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
			var buffer = new ErrorBufferStream(bodyFeature.Stream, () => context.Response.StatusCode >= 400 && FindError(section, context.Response.StatusCode, SubStatus(context)) != null);
			context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer, bodyFeature));
			try
			{
				await inner(context);
			}
			finally
			{
				context.Features.Set(bodyFeature);
			}
			if (!buffer.Buffering)
			{
				await buffer.FlushAsync();
				return;
			}
			int status = context.Response.StatusCode;
			bool fromModule = context.Items.ContainsKey(SubStatusKey);
			bool trySkip = context.Items.TryGetValue(AspNetCoreWorkerRequest.TrySkipIisCustomErrorsKey, out var skip) && skip is true;
			var error = FindError(section, status, SubStatus(context));
			bool replace = error != null && (fromModule || existing == "Replace" || existing == "Auto" && !trySkip);
			if (!replace || context.Response.HasStarted)
			{
				await buffer.ReleaseAsync();
				return;
			}
			var responseMode = Attr(error, "responseMode", Attr(errors, "defaultResponseMode", "File"));
			var target = Attr(error, "path", Attr(errors, "defaultPath"));
			if (string.IsNullOrEmpty(target))
			{
				await buffer.ReleaseAsync();
				return;
			}
			context.Response.Headers.ContentLength = null;
			switch (responseMode)
			{
				case "Redirect":
					context.Response.StatusCode = 302;
					context.Response.Headers.Location = target;
					await WriteBody(context, "text/html; charset=UTF-8", "");
					break;
				case "ExecuteURL":
					// A child request for the error URL, on a cleared response; it keeps the error's status.
					await buffer.DiscardAsync();
					context.Response.Clear();
					var originalPath = context.Request.Path;
					var originalQuery = context.Request.QueryString;
					var parts = target.Split('?', 2);
					context.Request.Path = parts[0];
					context.Request.QueryString = parts.Length > 1 ? new QueryString("?" + parts[1]) : QueryString.Empty;
					context.Items.Remove(SubStatusKey);
					// The child request decides the status, as on IIS: a static page is the static file handler's 200, and
					// an ASP.NET page's is 200 unless it sets one (#192, CI's iis-compare job, in both pipelines; webForCore
					// kept the error's status before).
					try
					{
						await inner(context);
					}
					finally
					{
						context.Request.Path = originalPath;
						context.Request.QueryString = originalQuery;
					}
					break;
				default:
					var file = Path.IsPathRooted(target) ? target : FindFile("/" + target.TrimStart('/', '\\').Replace('\\', '/'));
					if (file == null || !File.Exists(file))
					{
						await buffer.ReleaseAsync();
						return;
					}
					await WriteBody(context, "text/html", await File.ReadAllTextAsync(file));
					break;
			}
		}

		const string SubStatusKey = "webForCore.IIS.SubStatus";
		// The module's substatus, or the application's (Response.SubStatusCode).
		static int SubStatus(HttpContext context) =>
			context.Items.TryGetValue(SubStatusKey, out var value) && value is int sub ? sub
			: context.Items.TryGetValue(AspNetCoreWorkerRequest.AppSubStatusKey, out var app) && app is int appSub ? appSub : 0;

		static XElement FindError(XElement section, int status, int subStatus)
		{
			var errors = Element(section, "httpErrors");
			if (errors == null) return null;
			XElement any = null;
			foreach (var error in errors.Elements("error"))
			{
				if (Long(error, "statusCode", -1) != status) continue;
				var sub = Long(error, "subStatusCode", -1);
				if (sub == subStatus) return error;
				if (sub == -1) any ??= error;
			}
			return any;
		}

		static bool IsLocal(HttpContext context)
		{
			var remote = context.Connection.RemoteIpAddress;
			return remote == null || IPAddress.IsLoopback(remote) || remote.Equals(context.Connection.LocalIpAddress);
		}

		// An error IIS itself answers (request filtering, the MIME map, the request limit), as IIS's error page.
		async Task WriteError(HttpContext context, int status, int subStatus, string title, string reason)
		{
			context.Items[SubStatusKey] = subStatus;
			context.Response.StatusCode = status;
			var code = subStatus > 0 ? $"{status}.{subStatus}" : status.ToString();
			logger.LogDebug("IIS module answered {Status} for {Path}: {Reason}", code, context.Request.Path, reason);
			await WriteBody(context, "text/html", $"<!DOCTYPE html>\n<html><head><title>IIS Error {code} - {WebUtility.HtmlEncode(title)}</title></head>\n" +
				$"<body><h1>HTTP Error {code} - {WebUtility.HtmlEncode(title)}</h1>\n<h2>{WebUtility.HtmlEncode(reason)}</h2></body></html>");
		}

		static async Task WriteBody(HttpContext context, string contentType, string body)
		{
			var bytes = Encoding.UTF8.GetBytes(body);
			context.Response.ContentType = contentType;
			context.Response.ContentLength = bytes.Length;
			if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes);
		}

		// Holds an error response's body until the custom error module has decided whether to replace it.
		sealed class ErrorBufferStream : Stream
		{
			readonly Stream inner;
			readonly Func<bool> shouldBuffer;
			MemoryStream buffer;
			bool decided;

			public ErrorBufferStream(Stream inner, Func<bool> shouldBuffer) { this.inner = inner; this.shouldBuffer = shouldBuffer; }

			public bool Buffering => buffer != null;

			void Decide()
			{
				if (decided) return;
				decided = true;
				if (shouldBuffer()) buffer = new MemoryStream();
			}

			public Task DiscardAsync()
			{
				buffer = null;
				decided = true;
				return Task.CompletedTask;
			}

			public async Task ReleaseAsync()
			{
				if (buffer == null) return;
				var data = buffer;
				buffer = null;
				data.Position = 0;
				await data.CopyToAsync(inner);
			}

			public override void Write(byte[] data, int offset, int count) { Decide(); if (buffer != null) buffer.Write(data, offset, count); else inner.Write(data, offset, count); }
			public override async Task WriteAsync(byte[] data, int offset, int count, CancellationToken cancellationToken)
			{
				Decide();
				if (buffer != null) buffer.Write(data, offset, count); else await inner.WriteAsync(data, offset, count, cancellationToken);
			}
			public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
			{
				Decide();
				if (buffer != null) buffer.Write(data.Span); else await inner.WriteAsync(data, cancellationToken);
			}
			public override void Flush() { if (buffer == null && decided) inner.Flush(); }
			public override Task FlushAsync(CancellationToken cancellationToken) => buffer == null && decided ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;
			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override bool CanWrite => true;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
		}
	}
}
#endif
