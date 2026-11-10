#if NETCOREAPP

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Permissions;
using System.Text;
using System.Diagnostics;
using System.Web;
using System.Web.Hosting;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Primitives;
using System.Security.Principal;
using Microsoft.AspNetCore;
using Core = Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace System.Web.Hosting
{
	public class AspNetCoreWorkerRequest : SimpleWorkerRequest, IAspNetCoreWorkerRequest
	{
		private const int MaxChunkLength = 64 * 1024;

		private const int MaxHeaderBytes = 32 * 1024;


		// TODO read default files from web.config
		private static readonly string[] DefaultFileNames = new[] { "default.aspx", "default.htm", "default.html" };

		private TaskCompletionSource<bool> Completed = new TaskCompletionSource<bool>();

		private AutoResetEvent CompletedEvent = new AutoResetEvent(false);

		private static readonly char[] IntToHex = new[]
			{
				'0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f'
			};

		private static readonly string[] RestrictedDirs = new[]
			{
				"/bin",
				"/appbrowsers",
				"/appcode",
				"/appdata",
				"/applocalresources",
				"/appglobalresources",
				"/appwebreferences"
			};

		public AspNetCoreHost Host { get; private set; }

		private string allRawHeaders;

		private byte[] body;

		private int bodyLength;

		private int contentLength;

		// security permission to Assert remoting calls to connection
		private int endHeadersOffset;

		private string filePath;

		private byte[] headerBytes;

		private List<ByteString> headerByteStrings;

		private bool headersSent;

		// parsed request data

		private bool isClientScriptPath;

		private string[] knownRequestHeaders;

		private string path;

		private string pathInfo;

		private string pathTranslated;

		private string protocol;

		private string queryString;
		private byte[] queryStringBytes;

		private List<byte[]> responseBodyBytes;

		private StringBuilder responseHeadersBuilder;

		private int responseStatus;

		private bool specialCaseStaticFileHeaders;

		private int startHeadersOffset;

		private string[][] unknownRequestHeaders;

		private string url;

		private string verb;

		public Core.HttpContext Context { get; private set; }
		public AspNetCoreWorkerRequest(AspNetCoreHost host, Core.HttpContext context)
			: base(string.Empty, string.Empty, null)
		{
			this.Host = host;
			Context = context;
		}

		// webForCore: WebFormsMiddleware counts the request as one of HttpRuntime's active requests from before its check that
		// the application is still running until the runtime has completed the response (#214).
		internal const string ReleaseRuntimeCountKey = "webForCore.ReleaseRuntimeCount";

		// webForCore: IIS's fTrySkipCustomErrors, for the httpErrors module (IIS/IISModules.cs) in the hosting layer.
		public const string TrySkipIisCustomErrorsKey = "webForCore.TrySkipIisCustomErrors";
		internal override bool TrySkipIisCustomErrors
		{
			get { return Context.Items.TryGetValue(TrySkipIisCustomErrorsKey, out var value) && value is true; }
			set { Context.Items[TrySkipIisCustomErrorsKey] = value; }
		}

		// webForCore: the client's connection closes after the response (ASP.NET's error page for a body over maxRequestLength,
		// HttpResponse.CloseConnectionAfterError), as on IIS, with the connection to webforcore-host kept open (#219).
		internal void CloseClientConnectionAfterResponse() => WebFormsForCore.Hosting.WorkerProcess.CloseClientConnection(Context);

		// webForCore: the connection closes after the response, with "Connection: close" when the response hasn't started,
		// so that the other side knows. Under webforcore-host, once the response has started (ASP.NET's error for a body over
		// maxRequestLength flushes its error page, then closes, without reading the body), the connection stays open and
		// Kestrel drains the unread body, as for any request. Closed after a response that didn't say so, it was a connection
		// the host's pool sent its next request on, and that request's client was reset now and then
		// (ErrorsAndLimitsTests.LimitStatusIsIISs on Windows). The connection is the host's, not the client's; the host
		// closes the client's after that error page (CloseClientConnectionAfterResponse, #219). Run on its own, the application
		// closes the client's connection as before.
		public override void CloseConnection()
		{
			if (Context.Response.HasStarted)
			{
				if (WebFormsForCore.Hosting.WorkerProcess.IsWorker) return;
			}
			else Context.Response.Headers.Connection = "close";
			Context.Connection.RequestClose();
		}

		public override void EndOfRequest()
		{
#if DebugWF4C
			var path = Context.Request.Path;
			Debug.WriteLine($"EndOfRequestStart {path}");
#endif
			// webForCore: an accepted WebSocket request keeps its response for the handshake (Process runs it next); a
			// timed-out request's response was already completed (TimedOut).
			if (pendingWebSocket != null || pendingTransfer != null || timedOut)
			{
				Completed.TrySetResult(true);
				return;
			}
			// webForCore: the request ends with a response ASP.NET chunked itself (classic mode's unbuffered output:
			// HttpResponse writes Transfer-Encoding: chunked and the chunks, and Kestrel passes them through) but without
			// its last chunk: sending it failed and the error couldn't be written either (an exception in
			// PreSendRequestContent, #184's Recycle exception). Kestrel would end the request and keep the connection,
			// and the client would wait for the rest for ever. The connection is aborted instead, as IIS ends a request
			// whose response is incomplete: the client sees the response fail, not hang (#184).
			if (appChunked && !chunkEnded && Context?.Response != null && Context.Response.HasStarted)
			{
				Context.Abort();
				Completed.TrySetResult(true);
				return;
			}
			try
			{
				if (Context != null && Context.Response != null)
				{
					var task = Context.Response.CompleteAsync();
					if (task != null)
					{
						task.ContinueWith(t =>
							{
#if DebugWF4C
								Debug.WriteLine($"EndOfRequest {path}");
#endif
								if (t.Exception != null) Completed.TrySetException(t.Exception);
								else Completed.TrySetResult(true);
							},
							CancellationToken.None,
							TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
							TaskScheduler.Default);
					}
					else
					{
#if DebugWF4C
						Debug.WriteLine($"EndOfRequest Fail three {path}");
#endif
						Completed.TrySetResult(true);
					}
				} else
				{
#if DebugWF4C
					Debug.WriteLine($"EndOfRequest Fail {path}");
#endif
					Completed.TrySetResult(true);
				}
			}
			catch (Exception ex)
			{
#if DebugWF4C
				Debug.WriteLine($"EndOfRequest Fail two {path}");
#endif
				//Completed.SetException(ex);
				Completed.TrySetResult(true);
			}
		}

		public override void FlushResponse(bool finalFlush)
		{
			lock (responseLock)
			{
				if (SuppressOutput) return;
				using (var noSyncContext = new SafeAsync())
				{
					Context.Response.Body.Flush();
					if (finalFlush) Context.Response.Body.Close();
				}
			}
		}

		// webForCore: output is dropped once the request has timed out (its response was sent), turned into a WebSocket,
		// or been transferred (Server.TransferRequest: the transferred request answers).
		bool SuppressOutput => timedOut || pendingWebSocket != null || pendingTransfer != null;

		// webForCore: integrated-mode APIs, on the classic pipeline of the Kestrel host.

		// Response.SubStatusCode, for the IIS module layer (httpErrors' substatus matching).
		public const string AppSubStatusKey = "webForCore.IIS.AppSubStatus";
		internal void SetSubStatus(int subStatus) => Context.Items[AppSubStatusKey] = subStatus;

		// Request.Headers.Add/Set/Remove (and HTTP_ server variables): the request Kestrel and later reads see.
		internal void SetRequestHeader(string name, string value, bool replace)
		{
			var headers = Context.Request.Headers;
			if (value == null) headers.Remove(name);
			else if (replace || !headers.ContainsKey(name)) headers[name] = value;
			else headers[name] = StringValues.Concat(headers[name], value);
			ParseHeaders();
		}

		// webForCore: server variables the application set (Request.ServerVariables[name] = value, other than HTTP_ ones).
		// IIS's integrated pipeline keeps them in IIS's own server variable block, from which IIS7WorkerRequest reads
		// REMOTE_ADDR, REMOTE_HOST, LOCAL_ADDR, SERVER_NAME, SERVER_PORT and HTTPS: a module that copies the client's address
		// and scheme from a proxy's headers into REMOTE_ADDR and HTTPS changes Request.UserHostAddress, IsSecureConnection and
		// Url, as on IIS (#194). Before, only the ServerVariables collection had the new values.
		Dictionary<string, string> serverVariables;

		// A null value removes the variable: the connection's value is used again.
		internal void SetServerVariable(string name, string value)
		{
			serverVariables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (value == null) serverVariables.Remove(name);
			else serverVariables[name] = value;
		}

		string SetVariable(string name) => serverVariables != null && serverVariables.TryGetValue(name, out var value) ? value : null;

		// Request.InsertEntityBody: the body read so far, for whatever reads the request after ASP.NET (a transferred
		// request, middleware after UseWebForms).
		internal void InsertEntityBody(byte[] buffer, int offset, int count)
		{
			var copy = new byte[count];
			Buffer.BlockCopy(buffer, offset, copy, 0, count);
			Context.Request.Body = new MemoryStream(copy);
			Context.Request.ContentLength = count;
		}

		// Request.Abort.
		internal void AbortConnection() => Context.Abort();

		// Server.TransferRequest: once this request has ended, the pipeline runs again in the same Kestrel request for the
		// new URL, as IIS's child execution does (ScheduleExecuteUrl). preserveForm keeps the body and, without a query in
		// the path, the query string; headers are set on the new request; preserveUser carries User over.
		sealed class Transfer
		{
			public string Path, Query, Method;
			public byte[] Body;
			public bool PreserveForm;
			public System.Collections.Specialized.NameValueCollection Headers;
			public IPrincipal User;
		}

		volatile Transfer pendingTransfer;
		int transferDepth;
		internal IPrincipal TransferredUser { get; private set; }

		internal void ScheduleTransfer(string path, string query, string method, bool preserveForm, byte[] body,
			System.Collections.Specialized.NameValueCollection headers, IPrincipal user)
		{
			if (transferDepth >= 16) throw new HttpException("Too many request transfers (Server.TransferRequest).");
			pendingTransfer = new Transfer { Path = path, Query = query, Method = method, PreserveForm = preserveForm, Body = body, Headers = headers, User = user };
		}

		async Task RunTransfer()
		{
			var transfer = pendingTransfer;
			var request = Context.Request;
			var pathBase = request.PathBase.Value ?? "";
			request.Path = transfer.Path.StartsWith(pathBase + "/", StringComparison.OrdinalIgnoreCase) && pathBase.Length > 0
				? transfer.Path.Substring(pathBase.Length) : transfer.Path;
			if (transfer.Query != null) request.QueryString = new Core.QueryString("?" + transfer.Query);
			else if (!transfer.PreserveForm) request.QueryString = Core.QueryString.Empty;
			if (!string.IsNullOrEmpty(transfer.Method)) request.Method = transfer.Method;
			if (transfer.Headers != null)
				foreach (string name in transfer.Headers)
					request.Headers[name] = transfer.Headers[name];
			if (transfer.PreserveForm && transfer.Body != null) InsertEntityBody(transfer.Body, 0, transfer.Body.Length);
			else if (!transfer.PreserveForm) { request.Body = Stream.Null; request.ContentLength = 0; }
			// The transferred request's RawUrl is its own URL, as it was before RequestUrlKey (#127).
			Context.Items[RequestUrlKey] = pathBase + IisPath(request.Path.Value) + request.QueryString.Value;
			var next =new AspNetCoreWorkerRequest(Host, Context) { TransferredUser = transfer.User, transferDepth = transferDepth + 1 };
			await next.Process();
		}
		readonly object responseLock = new object();
		volatile bool timedOut;

		// webForCore: <httpRuntime executionTimeout>. ASP.NET aborts the request's thread and answers "Request timed out."
		// (500); .NET cannot abort a thread, so RequestTimeoutManager calls this instead: the client gets ASP.NET's
		// response now, Request.TimedOutToken is canceled, and whatever the request's code writes from then on is dropped.
		// The Kestrel request stays open until that code finishes, so its HttpContext stays valid for it.
		internal void TimedOut()
		{
			lock (responseLock)
			{
				if (timedOut || pendingWebSocket != null) return;
				timedOut = true;
				try
				{
					var response = Context.Response;
					if (!response.HasStarted)
					{
						Core.ResponseExtensions.Clear(response);
						response.StatusCode = 500;
						response.ContentType = "text/html; charset=utf-8";
						response.Headers.Connection = "close";
						var body = Encoding.UTF8.GetBytes("<html><head><title>Request timed out.</title></head><body><h1>Server Error in '" +
							HttpUtility.HtmlEncode(Host.VirtualPath) + "' Application.</h1><h2><i>Request timed out.</i></h2>" +
							"<b>Exception Details: </b>System.Web.HttpException: Request timed out.</body></html>");
						response.ContentLength = body.Length;
						using (new SafeAsync()) response.Body.Write(body, 0, body.Length);
					}
					_ = response.CompleteAsync();
				}
				catch (Exception e) when (e is InvalidOperationException || e is ObjectDisposedException || e is IOException)
				{
					// the response could not be completed; the connection is closed when the request ends
				}
			}
		}

		public override string GetAppPath() => Host.VirtualPath;
		public override string GetAppPathTranslated() => Host.PhysicalPath;
		public override string GetFilePath()
		{
			ParseRequest();
			return filePath;
		}
		public override string GetFilePathTranslated()
		{
			ParseRequest();
			return pathTranslated;
		}
		public override string GetHttpVerbName() => Context.Request.Method;
		public override string GetHttpVersion() => Context.Request.Protocol;
		public override string GetKnownRequestHeader(int index) => knownRequestHeaders[index];
		public override string GetLocalAddress() => SetVariable("LOCAL_ADDR") ?? Context.Connection.LocalIpAddress.ToString();
		public override int GetLocalPort() => Context.Connection.LocalPort;
		public override string GetPathInfo() => pathInfo;
		public override byte[] GetPreloadedEntityBody() => null;
		public override string GetQueryString() {
			var str = Context.Request.QueryString.ToString();
			if (str.StartsWith("?")) return str.Substring(1);
			else return str;
		}
		public override byte[] GetQueryStringRawBytes() => Encoding.ASCII.GetBytes(GetQueryString());
		public override string GetRawUrl()
		{
			// webForCore: the URL the client asked for (RequestUrlKey); this was the parsed path, which a directory
			// request's default document had rewritten, so FriendlyUrls redirected "/" to "/Default" (#127).
			if (Context.Items.TryGetValue(RequestUrlKey, out var url) && url is string requestUrl) return requestUrl;
			ParseRequest();
			var query = GetQueryString();
			if (string.IsNullOrEmpty(query)) return path;
			return $"{path}?{query}";
		}
		public override string GetRemoteAddress() => SetVariable("REMOTE_ADDR") ?? Context.Connection.RemoteIpAddress.ToString();
		// REMOTE_HOST is a variable of its own on IIS: the client's address (no reverse DNS), whatever REMOTE_ADDR was set to.
		public override string GetRemoteName() => SetVariable("REMOTE_HOST") ?? Context.Connection.RemoteIpAddress.ToString();
		public override int GetRemotePort() => Context.Connection.RemotePort;
		internal override string GetLocalPortAsString() => SetVariable("SERVER_PORT") ?? base.GetLocalPortAsString();
		public override string GetServerName()
		{
			if (SetVariable("SERVER_NAME") is string name) return name;
			// webForCore: IIS sets SERVER_NAME from the request's Host header (the name the client used), and HttpRequest.Url
			// is built from it. Fall back to the local address only when there is no Host header (HTTP/1.0).
			string host = Context.Request.Host.Host;
			if (!string.IsNullOrEmpty(host))
			{
				return host;
			}
			string localAddress = GetLocalAddress();
			if (localAddress.Equals("127.0.0.1") || localAddress.Equals("::1"))
			{
				return "localhost";
			}
			return localAddress;
		}

		public override string GetServerVariable(string name)
		{
			string processUser = string.Empty;
			string str2 = name;
			if (str2 == null)
			{
				return processUser;
			}
			if (SetVariable(str2) is string set) return set;
			// webForCore: the IIS server variables the classic pipeline asks the worker request for.
			switch (str2)
			{
				case "HTTPS": return Context.Request.IsHttps ? "on" : "off";
				case "HTTPS_KEYSIZE":
				case "CERT_KEYSIZE": return Context.Request.IsHttps ? KeySize() : "";
				case "HTTPS_SECRETKEYSIZE":
				case "CERT_SECRETKEYSIZE": return "";
				case "CERT_FLAGS": return CertificateFlags();
				case "CERT_COOKIE": return ClientCertificate?.Thumbprint.ToLowerInvariant() ?? "";
				case "CERT_ISSUER": return IisName(ClientCertificate?.IssuerName);
				case "CERT_SUBJECT": return IisName(ClientCertificate?.SubjectName);
				case "CERT_SERIALNUMBER":
					var serial = ClientCertificate?.GetSerialNumberString();
					return serial == null ? "" : string.Join("-", Enumerable.Range(0, serial.Length / 2).Select(i => serial.Substring(i * 2, 2).ToLowerInvariant()));
				case "REMOTE_PORT": return GetRemotePort().ToString(CultureInfo.InvariantCulture);
				case "GATEWAY_INTERFACE": return "CGI/1.1";
				case "SERVER_SOFTWARE": return "Kestrel";
				case "INSTANCE_ID": return "1";
				case "INSTANCE_META_PATH": return "/LM/W3SVC/1";
				case "APPL_MD_PATH": return "/LM/W3SVC/1/ROOT" + (Host.VirtualPath == "/" ? "" : Host.VirtualPath.TrimEnd('/'));
				// IIS's WebSocket module sets it on every request; Katana reads it to enable WebSockets (UseWebForms adds them).
				case "WEBSOCKET_VERSION": return "13";
			}
			if (str2 != "ALLRAW")
			{
				if (str2 != "SERVERPROTOCOL")
				{
					if (str2 == "LOGONUSER")
					{
						if (GetUserToken() != IntPtr.Zero)
						{
							processUser = Host.GetProcessUser();
						}
						return processUser;
					}
					if ((str2 == "AUTHTYPE") && (GetUserToken() != IntPtr.Zero))
					{
						processUser = "NTLM";
					}
					return processUser;
				}
			}
			else
			{
				return Context.Request.Headers.ToString();
			}
			return protocol;
		}

		public override string GetUnknownRequestHeader(string name)
		{
			int n = unknownRequestHeaders.Length;

			for (int i = 0; i < n; i++)
			{
				if (string.Compare(name, unknownRequestHeaders[i][0], StringComparison.OrdinalIgnoreCase) == 0)
				{
					return unknownRequestHeaders[i][1];
				}
			}

			return null;
		}

		public override string[][] GetUnknownRequestHeaders()
		{
			return unknownRequestHeaders;
		}

		///////////////////////////////////////////////////////////////////////////////////////////////
		// Implementation of HttpWorkerRequest

		public override string GetUriPath()
		{
			// webForCore: the decoded path, as IIS's (see IisPath); this was the escaped path base and path, with a '/'
			// between them that doubled the path's own.
			ParseRequest();
			return path;
		}

		public override IntPtr GetUserToken()
		{
			return Host.GetProcessToken();
		}

		public string GetProcessUser()
		{
			return Host.GetProcessUser();
		}

		public override string GetAppPoolID()
		{
			return Host.PhysicalPath.GetHashCode().ToString("X");
		}

		public override bool HeadersSent()
		{
			return headersSent;
		}

		public override bool IsClientConnected() => !timedOut && !Context.RequestAborted.IsCancellationRequested;

		// webForCore: HTTPS (Request.IsSecureConnection, Request.Url's scheme, SERVER_PORT_SECURE); HTTPS as the application set
		// it, as IIS7WorkerRequest reads it (#194).
		public override bool IsSecure() => SetVariable("HTTPS") is string https ? https == "on" : Context.Request.IsHttps;

		// webForCore: the client certificate Kestrel negotiated (HttpsConnectionAdapterOptions.ClientCertificateMode
		// AllowCertificate or RequireCertificate), for Request.ClientCertificate and the CERT_ server variables.
		System.Security.Cryptography.X509Certificates.X509Certificate2 ClientCertificate => Context.Connection.ClientCertificate;

		public override byte[] GetClientCertificate() => ClientCertificate?.RawData ?? Array.Empty<byte>();
		public override DateTime GetClientCertificateValidFrom() => ClientCertificate?.NotBefore ?? DateTime.Now;
		public override DateTime GetClientCertificateValidUntil() => ClientCertificate?.NotAfter ?? DateTime.Now;
		public override byte[] GetClientCertificateBinaryIssuer() => ClientCertificate?.IssuerName.RawData ?? Array.Empty<byte>();
		public override int GetClientCertificateEncoding() => ClientCertificate != null ? 1 : 0; // X509_ASN_ENCODING
		public override byte[] GetClientCertificatePublicKey() => ClientCertificate?.GetPublicKey() ?? Array.Empty<byte>();

		// CERT_FLAGS: bit 0, a certificate is present; bit 1, its issuer is not trusted (IIS: not in the server's CA list).
		string certificateFlags;
		string CertificateFlags()
		{
			if (certificateFlags != null) return certificateFlags;
			var certificate = ClientCertificate;
			if (certificate == null) return certificateFlags = "0";
			using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
			chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
			bool trusted;
			try { trusted = chain.Build(certificate); }
			catch (System.Security.Cryptography.CryptographicException) { trusted = false; }
			return certificateFlags = trusted ? "1" : "3";
		}

		// IIS's form of a distinguished name: most significant part first ("C=US, O=Contoso, CN=name").
		static string IisName(System.Security.Cryptography.X509Certificates.X500DistinguishedName name) =>
			name == null ? "" : string.Join(", ", name.Format(true).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		// TLS strength for HTTPS_KEYSIZE / CERT_KEYSIZE.
		string KeySize()
		{
#pragma warning disable SYSLIB0058 // CipherStrength: the closest Kestrel has to IIS's key size
			var tls = Context.Features.Get<Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature>();
			return tls == null || tls.CipherStrength == 0 ? "" : tls.CipherStrength.ToString(CultureInfo.InvariantCulture);
#pragma warning restore SYSLIB0058
		}
		public override bool IsEntireEntityBodyIsPreloaded() => false;
		public override string MapPath(string path)
		{
			string mappedPath;
			bool isClientScriptPath;

			if (string.IsNullOrEmpty(path) || path.Equals("/"))
			{
				// asking for the site root
				mappedPath = Host.VirtualPath == "/" ? Host.PhysicalPath : Environment.SystemDirectory;
			}
			else if (Host.IsVirtualPathAppPath(path))
			{
				// application path
				mappedPath = Host.PhysicalPath;
			}
			else if (Host.IsVirtualPathInApp(path, out isClientScriptPath))
			{
				if (isClientScriptPath)
				{
					mappedPath = Path.Combine(Host.PhysicalClientScriptPath,
								 path.Substring(Host.NormalizedClientScriptPath.Length));
				}
				else
				{
					// inside app but not the app path itself
					string relative = path.Substring(Host.NormalizedVirtualPath.Length);
					mappedPath = Path.Combine(Host.PhysicalPath, relative);
					// webForCore: the file in any case, as on IIS's file system and as HostingEnvironment.MapPath finds it
					// (UserMapPath). Request.PhysicalPath was the URL's case, so a static file asked for in another case
					// was 404 until a request in its own case had cached the mapping (#140).
					if (!OperatingSystem.IsWindows() && !File.Exists(mappedPath) && !Directory.Exists(mappedPath))
						mappedPath = System.Web.Configuration.UserMapPath.ResolveIgnoreCase(Host.PhysicalPath, relative);
				}
			}
			else
			{
				// outside of app -- make relative to app path
				if (path.StartsWith("/", StringComparison.Ordinal))
				{
					mappedPath = Path.Combine(Host.PhysicalPath, path.Substring(1));
				}
				else
				{
					mappedPath = Path.Combine(Host.PhysicalPath, path);
				}
			}

			mappedPath = mappedPath.Replace('/', Path.DirectorySeparatorChar);

			if (mappedPath.EndsWith("\\", StringComparison.Ordinal) &&
				!mappedPath.EndsWith(":\\", StringComparison.Ordinal))
			{
				mappedPath = mappedPath.Substring(0, mappedPath.Length - 1);
			}

			return mappedPath;
		}

		[AspNetHostingPermission(SecurityAction.Assert, Level = AspNetHostingPermissionLevel.Medium)]
		public async Task Process()
		{
			// read the request
			if (!TryParseRequest())
			{
				return;
			}

			if (!Host.RequireAuthentication || true)
			{
				// deny access to code, bin, etc.
				if (IsRequestForRestrictedDirectory())
				{
					Context.Response.StatusCode = 403;
					Context.Response.CompleteAsync();
					return;
				}

				PrepareResponse();

				// Hand the processing over to HttpRuntime
				// Run processing in separate ASP.NET Worker Thread
				//var completed = Completed.Task;
				await Task.Factory.StartNew(() =>
					{
						Thread.CurrentThread.Name = $"ASP.NET WorkerThread {Context.Request.Path}";
#if DebugWF4C
						Debug.WriteLine($"Start ProcessRequest {Context.Request.Path}");
#endif
						HttpRuntime.ProcessRequest(this);
#if DebugWF4C
						Debug.WriteLine($"End ProcessRequest {Context.Request.Path}");
#endif
					},
					TaskCreationOptions.LongRunning);

#if DebugWF4C
				Debug.WriteLine($"Waiting {Context.Request.Path} to finish");
#endif
				await Completed.Task;
#if DebugWF4C
				Debug.WriteLine($"Request {Context.Request.Path} finished");
#endif
				// webForCore: the runtime is done with the request (#214): the worker no longer aborts it when it stops, and
				// the drain no longer waits for it. An accepted WebSocket or a file transfer runs on, and is aborted then.
				if (pendingWebSocket == null && pendingTransfer == null) Host.RequestCompleted(Context);
				(Context.Items[ReleaseRuntimeCountKey] as Action)?.Invoke();
				if (pendingWebSocket != null) await RunWebSocket();
				else if (pendingTransfer != null) await RunTransfer();
			}
		}


		public override int ReadEntityBody(byte[] buffer, int size) => ReadEntityBody(buffer, 0, size);
		public override int ReadEntityBody(byte[] buffer, int offset, int size)
		{
			if (offset < 0) throw new ArgumentOutOfRangeException("offset must be grater than zero.");

			int bytesRead = 0;

			using (var noSyncContext = new SafeAsync())
			{
				var reader = Context.Request.Body;

				// webForCore: at the offset asked for (HttpBufferlessInputStream reads into the middle of its buffer).
				bytesRead = timedOut ? 0 : reader.Read(buffer, offset, size);
			}
			return bytesRead;
		}

		public override void SendCalculatedContentLength(int contentLength)
		{
			if (SuppressOutput) return;
			if (!headersSent)
			{
				Context.Response.ContentLength = contentLength;
			}
		}

		public override void SendKnownResponseHeader(int index, string value)
		{
			if (headersSent || SuppressOutput)
			{
				return;
			}

			switch (index)
			{
				case HeaderServer:
				case HeaderDate:
				case HeaderConnection:
					// ignore these
					return;
				case HeaderAcceptRanges:
					// FIX: #14359
					if (value != "bytes")
					{
						// use this header to detect when we're processing a static file
						break;
					}
					specialCaseStaticFileHeaders = true;
					return;

				case HeaderExpires:
				case HeaderLastModified:
					// FIX: #14359
					if (!specialCaseStaticFileHeaders)
					{
						// NOTE: Ignore these for static files. These are generated
						//       by the StaticFileHandler, but they shouldn't be.
						break;
					}
					return;


				case HeaderTransferEncoding:
					// webForCore: HttpResponse chunks an unbuffered response itself (see EndOfRequest, #184).
					appChunked = value != null && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
					break;

				// FIX: #12506
				case HeaderContentType:

					string contentType = null;

					if (value == "application/octet-stream")
					{
						// application/octet-stream is default for unknown so lets
						// take a shot at determining the type.
						// don't do this for other content-types as you are going to
						// end up sending text/plain for endpoints that are handled by
						// asp.net such as .aspx, .asmx, .axd, etc etc
						//contentType = CommonExtensions.GetContentType(pathTranslated);
					}
					value = contentType ?? value;
					Context.Response.ContentType = value;
					return;
			}

			SendUnknownResponseHeader(GetKnownResponseHeaderName(index), value);
		}

		// webForCore: whether the response's own chunking (Transfer-Encoding: chunked from HttpResponse) was ended with
		// its last chunk ("0\r\n\r\n", HttpResponse's s_chunkEnd), which it sends on its own as the final write (#184).
		bool appChunked;
		bool chunkEnded;

		static bool EndsWithLastChunk(byte[] data, int length) =>
			length >= 5 && data[length - 5] == (byte)'0' && data[length - 4] == (byte)'\r' && data[length - 3] == (byte)'\n'
				&& data[length - 2] == (byte)'\r' && data[length - 1] == (byte)'\n';

		public override void SendResponseFromFile(string filename, long offset, long length)
		{
			if (length == 0)
			{
				return;
			}

			FileStream f = null;
			try
			{
				f = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
				SendResponseFromFileStream(f, offset, length);
			}
			finally
			{
				if (f != null)
				{
					f.Close();
				}
			}
		}

		public override void SendResponseFromFile(IntPtr handle, long offset, long length)
		{
			if (length == 0)
			{
				return;
			}

			using (var sfh = new SafeFileHandle(handle, false))
			{
				using (var f = new FileStream(sfh, FileAccess.Read))
				{
					SendResponseFromFileStream(f, offset, length);
				}
			}
		}

		public override void SendResponseFromMemory(byte[] data, int length)
		{
			if (length > 0)
			{
				lock (responseLock)
				{
					if (SuppressOutput) return;
					using (var noSyncContext = new SafeAsync())
					{
						Context.Response.Body.Write(data, 0, length);
					}
					chunkEnded = EndsWithLastChunk(data, length);
				}
			}
		}

		public override void SendStatus(int statusCode, string statusDescription)
		{
			if (SuppressOutput) return;
			Context.Response.StatusCode = statusCode;
			// webForCore: IIS sends HttpResponse.StatusDescription as the reason phrase (HttpStatusCodeResult(code, description),
			// Response.StatusDescription). Kestrel sends its own unless the response feature carries one (HTTP/1.x only).
			var feature = Context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>();
			if (feature != null && !string.IsNullOrEmpty(statusDescription)
				&& statusDescription != HttpWorkerRequest.GetStatusDescription(statusCode))
			{
				feature.ReasonPhrase = statusDescription;
			}
		}

		public override void SendUnknownResponseHeader(string name, string value)
		{
			if (headersSent || SuppressOutput)
				return;

			if (Context.Response.Headers.ContainsKey(name)) {
				var header = Context.Response.Headers[name];
				Context.Response.Headers[name] = StringValues.Concat(header, value);
			} else
			{
				Context.Response.Headers[name] = value;
			}
		}

		// webForCore: WebSockets through HttpContext.AcceptWebSocketRequest, as IIS 8's WebSocket module provides them to
		// ASP.NET: the request runs through the pipeline, then (instead of its response) the handshake is made and the
		// application's function runs with an AspNetWebSocketContext over Kestrel's WebSocket. UseWebForms adds ASP.NET
		// Core's WebSocket middleware.
		sealed class WebSocketRequest
		{
			public HttpContext HttpContext;
			public Func<System.Web.WebSockets.AspNetWebSocketContext, Task> UserFunc;
			public string SubProtocol;
		}

		volatile WebSocketRequest pendingWebSocket;

		internal bool IsWebSocketRequest() => Context.WebSockets.IsWebSocketRequest;

		internal void AcceptWebSocket(HttpContext context, Func<System.Web.WebSockets.AspNetWebSocketContext, Task> userFunc, string subProtocol)
		{
			lock (responseLock)
			{
				if (timedOut || Context.Response.HasStarted) throw new InvalidOperationException(SR.GetString(SR.WebSockets_CannotBeCalledAfterHandlerExecute));
				pendingWebSocket = new WebSocketRequest { HttpContext = context, UserFunc = userFunc, SubProtocol = subProtocol };
			}
		}

		async Task RunWebSocket()
		{
			var request = pendingWebSocket;
			System.Net.WebSockets.WebSocket socket = null;
			try
			{
				socket = await Context.WebSockets.AcceptWebSocketAsync(request.SubProtocol);
				var context = request.HttpContext;
				context.TransitionToWebSocketState(WebSocketTransitionState.TransitionStarted);
				context.CompleteTransitionToWebSocket();
				var webSocketContext = new System.Web.WebSockets.AspNetWebSocketContextImpl(new HttpContextWrapper(context), this, socket);
				Task task;
				var previous = HttpContext.Current;
				HttpContext.Current = context;
				try
				{
					task = request.UserFunc(webSocketContext);
				}
				finally
				{
					HttpContext.Current = previous;
				}
				request.UserFunc = null;
				if (task != null) await task.ConfigureAwait(false);
			}
			catch (Exception e)
			{
				System.Web.Management.WebBaseEvent.RaiseRuntimeError(e, null);
			}
			finally
			{
				if (socket != null)
				{
					// IIS aborts the socket when the application's function returns.
					if (socket.State != System.Net.WebSockets.WebSocketState.Closed) socket.Abort();
					socket.Dispose();
				}
			}
		}

		// webForCore: only a ".." segment, which Kestrel removes before the path gets here (as http.sys does). This was
		// Cassini's check of '%', '<', '>', ':', '\\', ".." and "//" anywhere in the path, answered with an empty 400: on
		// IIS the characters of <httpRuntime requestPathInvalidCharacters> get ASP.NET's 400 ("A potentially dangerous
		// Request.Path value was detected"), which System.Web's request validation gives here too, double escapes get
		// request filtering's 404.11 (IISModules), and the rest are served (#126).
		private bool IsBadPath()
		{
			ParseRequest();
			return path.Split('/').Any(segment => segment == "..");
		}

		private bool IsRequestForRestrictedDirectory()
		{
			String p = CultureInfo.InvariantCulture.TextInfo.ToLower(path);

			if (Host.VirtualPath != "/")
			{
				p = p.Substring(Host.VirtualPath.Length);
			}

			foreach (String dir in RestrictedDirs)
			{
				if (p.StartsWith(dir, StringComparison.Ordinal))
				{
					if (p.Length == dir.Length || p[dir.Length] == '/')
					{
						return true;
					}
				}
			}

			return false;
		}

		private void ParseHeaders()
		{
			knownRequestHeaders = new string[RequestHeaderMaximum];

			// construct unknown headers as array list of name1,value1,...
			var headers = new List<string>();
			// webForCore: not the "Expect: 100-continue" webforcore-host added (WorkerProcess.ExpectHeader, #214).
			bool hostAddedExpect = Context.Items.ContainsKey(WebFormsForCore.Hosting.WorkerProcess.HostAddedExpectKey);

			foreach (var header in Context.Request.Headers)
			{
				string name = header.Key;
				if (hostAddedExpect && string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase)) continue;
				var strValues = header.Value;
				string str;
				switch (strValues.Count)
				{
					case 0:
						str = (string)null;
						break;
					case 1:
						str = strValues[0];
						break;
					default:
						str = string.Join(';', (IEnumerable<string?>)strValues);
						break;
				}

				// remember
				int knownIndex = GetKnownRequestHeaderIndex(name);
				if (knownIndex >= 0)
				{
					knownRequestHeaders[knownIndex] = str;
				}
				else
				{
					headers.Add(name);
					headers.Add(str);
				}
			} 
            
            // append AspFilterSessionId
            var path = Context.Request.Path.Value ?? string.Empty;
            // Optimize for the common case where there is no cookie
            if (path.IndexOf('(') != -1)
            {
                int endPos = path.LastIndexOf(")/", StringComparison.Ordinal);
                int startPos = (endPos > 2 ? path.LastIndexOf("/(", endPos - 1, endPos, StringComparison.Ordinal) : -1);
                // webForCore: a path without the pattern only has no session id. The filter code this comes from returned
                // here, before the unknown headers below were stored, so a path with a '(' lost them (#170).
                if (startPos >= 0 && IsValidHeader(path, startPos + 2, endPos))
                {
                    var sessionHeader = path.Substring(startPos + 2, endPos - startPos - 2);
                    headers.Add("AspFilterSessionId");
                    headers.Add(sessionHeader);
                }
            }

            // copy to array unknown headers
			int n = headers.Count / 2;
			unknownRequestHeaders = new string[n][];
			int j = 0;

			for (int i = 0; i < n; i++)
			{
				unknownRequestHeaders[i] = new string[2];
				unknownRequestHeaders[i][0] = headers[j++];
				unknownRequestHeaders[i][1] = headers[j++];
			}
		}

        // Make sure sub-string if of the pattern: A(XXXX)N(XXXXX)P(XXXXX) and so on.
        static private bool IsValidHeader(string path, int startPos, int endPos)
        {
            if (endPos - startPos < 3) // Minimum len is "X()"
                return false;

            while (startPos <= endPos - 3) { // Each iteration deals with one "A(XXXX)" pattern

                if (path[startPos] < 'A' || path[startPos] > 'Z') // Make sure pattern starts with a capital letter
                    return false;

                if (path[startPos + 1] != '(') // Make sure next char is '('
                    return false;

                startPos += 2;
                bool found = false;
                for (; startPos < endPos; startPos++) { // Find the ending ')'

                    if (path[startPos] == ')') { // found it!
                        startPos++; // Set position for the next pattern
                        found = true;
                        break; // Break out of this for-loop.
                    }

                    if (path[startPos] == '/') { // Can't contain path separaters
                        return false;
                    }
                }
                if (!found)  {
                    return false; // Ending ')' not found!
                }
            }

            if (startPos < endPos) // All chars consumed?
                return false;

            return true;
        }

		private void ParsePostedContent()
		{
			contentLength = 0;
			bodyLength = 0;

			string contentLengthValue = knownRequestHeaders[HeaderContentLength];
			if (contentLengthValue != null)
			{
				try
				{
					contentLength = Int32.Parse(contentLengthValue, CultureInfo.InvariantCulture);
				}
				// ReSharper disable EmptyGeneralCatchClause
				catch
				// ReSharper restore EmptyGeneralCatchClause
				{
				}
			}

			/*
			if (headerBytes.Length > endHeadersOffset)
			{
				bodyLength = headerBytes.Length - endHeadersOffset;

				if (bodyLength > contentLength)
				{
					bodyLength = contentLength; // don't read more than the content-length
				}

				if (bodyLength > 0)
				{
					body = new byte[bodyLength];
					Buffer.BlockCopy(headerBytes, endHeadersOffset, body, 0, bodyLength);
					//connection.LogRequestBody(body);
				}
			} */
		}

		// webForCore: the request's URL as IIS gives it to ASP.NET (#126, #127). http.sys decodes the path (PATH_INFO,
		// Request.Path: "/Product/Convertible Car"), an escaped '/' included, turns '\' into '/' and merges repeated
		// slashes. Kestrel decodes it too, but keeps "%2F"; only a "%2F" the client sent is decoded here, so that "%252F",
		// which Kestrel decodes to "%2F", stays a double escape for request filtering. The worker request had the escaped
		// form (PathString.ToString()), whose '%' made every escaped request an empty 400 before it reached the application.
		internal static string IisPath(string path, bool escapedSlashes = false)
		{
			if (string.IsNullOrEmpty(path)) return "/";
			if (escapedSlashes) path = path.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
			path = path.Replace('\\', '/');
			while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
			return path;
		}

		// HttpRequest.RawUrl: IIS's CACHE_URL, the URL as the client asked for it, decoded and normalised as above, with
		// the query string as it was sent; unaffected by rewriting, so a directory request's RawUrl is "/" while its Path
		// is the default document's (#127). WebFormsMiddleware records it before the IIS modules run.
		public const string RequestUrlKey = "webForCore.RequestUrl";

		internal static void BeginRequest(Core.HttpContext context)
		{
			var request = context.Request;
			var rawTarget = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? "";
			int query = rawTarget.IndexOf('?');
			var rawPath = query >= 0 ? rawTarget.Substring(0, query) : rawTarget;
			var normalized = IisPath(request.Path.Value, rawPath.Contains("%2F", StringComparison.OrdinalIgnoreCase));
			if (normalized != request.Path.Value) request.Path = new Core.PathString(normalized);
			context.Items.TryAdd(RequestUrlKey, (request.PathBase.Value ?? "") + normalized + request.QueryString.Value);
		}

		bool requestParsed = false;
		private void ParseRequest()
		{
			if (!requestParsed)
			{
				// webForCore: an empty path under a path base is the application's own URL without its trailing slash ("/app",
				// #190): Request.Path is "/app", as on IIS.
				var pathBase = Context.Request.PathBase.Value ?? "";
				path = pathBase.Length > 0 && string.IsNullOrEmpty(Context.Request.Path.Value) ? pathBase : pathBase + IisPath(Context.Request.Path.Value);

				// webForCore: the script is the first segment with an extension of a managed handler, as IIS maps it
				// ("/Page.aspx/a.b/c": FilePath "/Page.aspx", PathInfo "/a.b/c"). This took the last dot's segment, and
				// "/Page.aspx/extra/path" was 404.
				int scriptEnd = -1;
				for (int slash = path.IndexOf('/', 1); slash > 0; slash = path.IndexOf('/', slash + 1))
				{
					var prefix = path.Substring(0, slash);
					if (Host.HandleExtensions.Any(ext => prefix.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) scriptEnd = slash;
					if (scriptEnd > 0) break;
				}

				if (scriptEnd > 0)
				{
					filePath = path[..scriptEnd];
					pathInfo = path[scriptEnd..];
				}
				else
				{
					filePath = path;
					pathInfo = String.Empty;
				}

				pathTranslated = MapPath(filePath);

				requestParsed = true;
			}
		}

		private void PrepareResponse()
		{
			Context.Response.Headers.Clear();
			//Context.Response.Cookies.Clear();
		}

		private void Reset()
		{
			headerBytes = null;
			startHeadersOffset = 0;
			endHeadersOffset = 0;
			headerByteStrings = null;

			isClientScriptPath = false;

			verb = null;
			url = null;
			protocol = null;

			path = null;
			filePath = null;
			pathInfo = null;
			pathTranslated = null;
			queryString = null;
			queryStringBytes = null;

			contentLength = 0;
			bodyLength = 0;
			body = null;

			allRawHeaders = null;
			unknownRequestHeaders = null;
			knownRequestHeaders = null;
			specialCaseStaticFileHeaders = false;
		}

		private void SendResponseFromFileStream(Stream f, long offset, long length)
		{
			long fileSize = f.Length;

			if (length == -1)
			{
				length = fileSize - offset;
			}

			if (length == 0 || offset < 0 || length > fileSize - offset || SuppressOutput)
			{
				return;
			}

			if (offset > 0)
			{
				f.Seek(offset, SeekOrigin.Begin);
			}

			chunkEnded = false;
			using (var noSyncContext = new SafeAsync())
			{
				if (length <= MaxChunkLength)
				{
					var fileBytes = ArrayPool<byte>.Shared.Rent((int)length);
					try
					{
						int bytesRead = f.Read(fileBytes, 0, (int)length);
						//SendResponseFromMemory(fileBytes, bytesRead);
						Context.Response.Body.Write(fileBytes, 0, bytesRead);
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(fileBytes);
					}
				}
				else
				{
					var chunk = ArrayPool<byte>.Shared.Rent(MaxChunkLength);
					try
					{
						var bytesRemaining = (int)length;

						while (bytesRemaining > 0)
						{
							int bytesToRead = (bytesRemaining < MaxChunkLength) ? bytesRemaining : MaxChunkLength;
							int bytesRead = f.Read(chunk, 0, bytesToRead);

							//SendResponseFromMemory(chunk, bytesRead);
							Context.Response.Body.Write(chunk, 0, bytesRead);
							bytesRemaining -= bytesRead;

							// flush to release keep memory
							if ((bytesRemaining > 0) && (bytesRead > 0))
							{
								//FlushResponse(false);
								Context.Response.Body.Flush();
							}
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(chunk);
					}
				}
			}
		}

		private void SkipAllPostedContent()
		{
			if ((contentLength > 0) && (bodyLength < contentLength))
			{
				byte[] buffer = new byte[1024];
				for (int i = contentLength - bodyLength; i > 0; i -= buffer.Length)
				{
					var nread = Context.Request.Body.Read(buffer, 0, Math.Min(i, buffer.Length));
					if ((buffer == null) || (nread == 0))
					{
						return;
					}
				}
			}
		}

		/// <summary>
		/// TODO: defer response until request is written
		/// </summary>
		/// <returns></returns>
		private bool TryParseRequest()
		{
			Reset();

			ParseRequest();

			// Check for bad path
			if (IsBadPath())
			{
				Context.Response.StatusCode = 400;
				Context.Response.CompleteAsync();
				return false;
			}

			// Check if the path is not well formed or is not for the current app
			if (!Host.IsVirtualPathInApp(path, out isClientScriptPath))
			{
				Context.Response.StatusCode = 404;
				Context.Response.CompleteAsync();
				return false;
			}

			ParseHeaders();

			ParsePostedContent();

			return true;
		}

		/*private static string UrlEncodeRedirect(string path)
		{
			// this method mimics the logic in HttpResponse.Redirect (which relies on internal methods)

			// count non-ascii characters
			byte[] bytes = Encoding.UTF8.GetBytes(path);
			int count = bytes.Length;
			int countNonAscii = 0;
			for (int i = 0; i < count; i++)
			{
				if ((bytes[i] & 0x80) != 0)
				{
					countNonAscii++;
				}
			}

			// encode all non-ascii characters using UTF-8 %XX
			if (countNonAscii > 0)
			{
				// expand not 'safe' characters into %XX, spaces to +s
				var expandedBytes = new byte[count + countNonAscii * 2];
				int pos = 0;
				for (int i = 0; i < count; i++)
				{
					byte b = bytes[i];

					if ((b & 0x80) == 0)
					{
						expandedBytes[pos++] = b;
					}
					else
					{
						expandedBytes[pos++] = (byte)'%';
						expandedBytes[pos++] = (byte)IntToHex[(b >> 4) & 0xf];
						expandedBytes[pos++] = (byte)IntToHex[b & 0xf];
					}
				}

				path = Encoding.ASCII.GetString(expandedBytes);
			}

			// encode spaces into %20
			if (path.IndexOf(' ') >= 0)
			{
				path = path.Replace(" ", "%20");
			}

			return path;
		}*/

		#region Nested type: ByteParser

		internal class ByteParser
		{
			private readonly byte[] bytes;

			private int pos;

			public ByteParser(byte[] bytes)
			{
				bytes = bytes;
				pos = 0;
			}

			public int CurrentOffset
			{
				get { return pos; }
			}

			public ByteString ReadLine()
			{
				ByteString line = null;

				for (int i = pos; i < bytes.Length; i++)
				{
					if (bytes[i] == (byte)'\n')
					{
						int len = i - pos;
						if (len > 0 && bytes[i - 1] == (byte)'\r')
						{
							len--;
						}

						line = new ByteString(bytes, pos, len);
						pos = i + 1;
						return line;
					}
				}

				if (pos < bytes.Length)
				{
					line = new ByteString(bytes, pos, bytes.Length - pos);
				}

				pos = bytes.Length;
				return line;
			}
		}

		#endregion

		#region Nested type: ByteString

		internal class ByteString
		{
			private readonly byte[] bytes;

			private readonly int length;

			private readonly int offset;

			public ByteString(byte[] bytes, int offset, int length)
			{
				bytes = bytes;
				offset = offset;
				length = length;
			}

			public byte[] Bytes
			{
				get { return bytes; }
			}

			public bool IsEmpty
			{
				get { return (bytes == null || length == 0); }
			}

			public byte this[int index]
			{
				get { return bytes[offset + index]; }
			}

			public int Length
			{
				get { return length; }
			}

			public int Offset
			{
				get { return offset; }
			}

			public byte[] GetBytes()
			{
				var bytes = new byte[length];
				if (length > 0) Buffer.BlockCopy(bytes, offset, bytes, 0, length);
				return bytes;
			}

			public string GetString(Encoding enc)
			{
				if (IsEmpty) return string.Empty;
				return enc.GetString(bytes, offset, length);
			}

			public string GetString()
			{
				return GetString(Encoding.UTF8);
			}

			public int IndexOf(char ch)
			{
				return IndexOf(ch, 0);
			}

			public int IndexOf(char ch, int offset)
			{
				for (int i = offset; i < length; i++)
				{
					if (this[i] == (byte)ch) return i;
				}
				return -1;
			}

			public ByteString[] Split(char sep)
			{
				var list = new List<ByteString>();

				int pos = 0;
				while (pos < length)
				{
					int i = IndexOf(sep, pos);
					if (i < 0)
					{
						break;
					}

					list.Add(Substring(pos, i - pos));
					pos = i + 1;

					while (this[pos] == (byte)sep && pos < length)
					{
						pos++;
					}
				}

				if (pos < length)
					list.Add(Substring(pos));

				return list.ToArray();
			}

			public ByteString Substring(int offset, int len)
			{
				return new ByteString(bytes, offset + offset, len);
			}

			public ByteString Substring(int offset)
			{
				return Substring(offset, length - offset);
			}
		}

		#endregion
	}
}
#endif