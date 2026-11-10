#if NETCOREAPP
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// webforcore-host starts its workers with ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=System.Web: ASP.NET Core then runs this
// before the application's Program.cs configures anything, whatever that contains.
[assembly: HostingStartup(typeof(WebFormsForCore.Hosting.WorkerHostingStartup))]

namespace WebFormsForCore.Hosting
{
	// The worker's services (WorkerProcess), in every application webforcore-host runs.
	public sealed class WorkerHostingStartup : IHostingStartup
	{
		public void Configure(IWebHostBuilder builder)
		{
			if (WorkerProcess.IsWorker) builder.ConfigureServices(WorkerProcess.AddWorkerServices);
		}
	}

	// webForCore (#56): an application started by webforcore-host (src/WebFormsForCore.Host) is one of its worker
	// processes, as w3wp was IIS's. It serves on the Unix socket the host gave it, takes the client's connection details
	// from the host's headers, tells the host when it is ready and when the application restarts, and stops when the host
	// closes its stdin. Run on its own (dotnet run), the application serves its own endpoints, and a restart stops it.
	// The host's side is WorkerProtocol (src/WebFormsForCore.Host/WorkerProtocol.cs); keep the two in step.
	public static class WorkerProcess
	{
		const string SocketVariable = "WEBFORCORE_WORKER_SOCKET";
		const string IdVariable = "WEBFORCORE_WORKER_ID";
		const string ControlVariable = "WEBFORCORE_HOST_CONTROL";
		const string TokenVariable = "WEBFORCORE_HOST_TOKEN";

		internal const string RemoteHeader = "X-WebForCore-Remote";
		internal const string LocalHeader = "X-WebForCore-Local";
		internal const string SchemeHeader = "X-WebForCore-Scheme";
		internal const string ClientCertificateHeader = "X-WebForCore-Client-Certificate";
		internal const string TlsHeader = "X-WebForCore-Tls";
		// #214: the host added "Expect: 100-continue", so that it sends the body only when the application reads it. Kestrel
		// looks for it in the request's headers when the application first reads the body, to send 100 Continue, so it
		// stays there; the worker request hides it from System.Web (HostAddedExpectKey), as the client didn't send it.
		internal const string ExpectHeader = "X-WebForCore-Expect";
		internal const string HostAddedExpectKey = "webForCore.HostAddedExpect";
		internal const string RestartingHeader = "X-WebForCore-Restarting";
		// #219: response header with which the worker asks the host to close the client's connection after the response
		// (Connection: close), where IIS closes it; the connection between the worker and the host stays open.
		internal const string CloseHeader = "X-WebForCore-Close";

		static readonly string socketPath = Variable(SocketVariable);
		static readonly string id = Variable(IdVariable);
		static readonly string controlPath = Variable(ControlVariable);
		static readonly string token = Variable(TokenVariable);

		static string Variable(string name)
		{
			var value = Environment.GetEnvironmentVariable(name);
			return string.IsNullOrEmpty(value) ? null : value;
		}

		// Whether webforcore-host started this process.
		public static bool IsWorker => socketPath != null && id != null && controlPath != null && token != null;

		public static string WorkerId => id;

		// The application's virtual path, set by WebFormsMiddleware ("/" by default).
		internal static string ApplicationVirtualPath { get; set; } = "/";

		// Set up by WorkerHostingStartup: the worker serves only on the host's socket (not the application's own endpoints
		// from --urls or the "Kestrel" configuration section, which are the host's now), applies the host's connection
		// details before the application's middleware, and talks to the host.
		internal static void AddWorkerServices(IServiceCollection services)
		{
			services.PostConfigure<KestrelServerOptions>(options =>
			{
				// The bindings in configuration are the host's: an empty configuration instead of the "Kestrel" section.
				options.Configure(new ConfigurationBuilder().Build());
				options.ListenUnixSocket(socketPath);
			});
			services.AddTransient<IStartupFilter, WorkerStartupFilter>();
			services.AddHostedService<WorkerLifetime>();
		}

		// An application pool recycle, overlapped as IIS overlapped it (#193): the worker asks the host for its replacement
		// and serves on; once the replacement is ready (and warm), the host sends RecycleCommand on the worker's stdin, and
		// `recycle` runs. False if the host didn't take the request: the caller recycles at once.
		const string RecycleCommand = "recycle";
		static Action recycleRequested;

		internal static bool RequestOverlappedRecycle(string reason, Action recycle)
		{
			if (!IsWorker) return false;
			Volatile.Write(ref recycleRequested, recycle);
			if (Notify("overlap", reason, TimeSpan.FromSeconds(10))) return true;
			Interlocked.CompareExchange(ref recycleRequested, null, recycle);
			return false;
		}

		// Sends a control message to the host and waits for its answer; false if the host didn't take it.
		internal static bool Notify(string kind, string detail, TimeSpan timeout)
		{
			if (!IsWorker) return false;
			try
			{
				using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				socket.SendTimeout = socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
				socket.Connect(new UnixDomainSocketEndPoint(controlPath));
				var line = token + " " + kind + " " + id + (string.IsNullOrEmpty(detail) ? "" : " " + detail.Replace('\n', ' ').Replace('\r', ' ')) + "\n";
				socket.Send(Encoding.UTF8.GetBytes(line));
				var buffer = new byte[16];
				int read = socket.Receive(buffer);
				return Encoding.ASCII.GetString(buffer, 0, read).StartsWith("ok", StringComparison.Ordinal);
			}
			catch (SocketException)
			{
				return false;
			}
		}

		// #219: the client's connection closes after this response, as IIS closes it after request filtering's rejections
		// (whatever page httpErrors then sends) and after ASP.NET's error for a body over maxRequestLength. In a worker the
		// host closes it (CloseHeader), and the worker's connection to the host, which its pool reuses, stays open; run on its
		// own, the application says Connection: close itself. Set when the response starts, so that a response httpErrors
		// replaces keeps it.
		internal static void CloseClientConnection(HttpContext context)
		{
			var response = context.Response;
			if (response.HasStarted || context.Items.ContainsKey(CloseHeader)) return;
			context.Items[CloseHeader] = true;
			response.OnStarting(() =>
			{
				if (IsWorker) response.Headers[CloseHeader] = "1";
				else response.Headers.Connection = "close";
				return Task.CompletedTask;
			});
		}

		// The host's connection details, before any of the application's middleware.
		sealed class WorkerStartupFilter : IStartupFilter
		{
			public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
			{
				app.Use(ApplyConnectionDetails);
				next(app);
			};
		}

		// Applies the host's headers: the client's address and port, the binding's, the scheme, and the client's TLS
		// certificate and protocol. The worker's socket is in the host's private directory, so only the host connects.
		static Task ApplyConnectionDetails(HttpContext context, Func<Task> next)
		{
			var headers = context.Request.Headers;
			var connection = context.Features.Get<IHttpConnectionFeature>();
			if (connection != null)
			{
				if (TryEndpoint(headers[RemoteHeader], out var remote, out var remotePort))
				{
					connection.RemoteIpAddress = remote;
					connection.RemotePort = remotePort;
				}
				if (TryEndpoint(headers[LocalHeader], out var local, out var localPort))
				{
					connection.LocalIpAddress = local;
					connection.LocalPort = localPort;
				}
			}
			string scheme = headers[SchemeHeader];
			if (scheme == "https" || scheme == "http") context.Request.Scheme = scheme;
			string certificate = headers[ClientCertificateHeader];
			if (!string.IsNullOrEmpty(certificate))
			{
				try
				{
					context.Features.Set<ITlsConnectionFeature>(new ForwardedTlsConnection(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificate))));
				}
				catch (Exception e) when (e is FormatException || e is System.Security.Cryptography.CryptographicException)
				{
					// Not a certificate: the request has none.
				}
			}
			string tls = headers[TlsHeader];
			if (!string.IsNullOrEmpty(tls))
			{
				var parts = tls.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol)
					&& int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var strength))
					context.Features.Set<ITlsHandshakeFeature>(new ForwardedTlsHandshake((SslProtocols)protocol, strength));
			}
			if (!string.IsNullOrEmpty(headers[ExpectHeader])) context.Items[HostAddedExpectKey] = true;
			foreach (var name in new[] { RemoteHeader, LocalHeader, SchemeHeader, ClientCertificateHeader, TlsHeader, ExpectHeader }) headers.Remove(name);
			return next();
		}

		// "[address]:port"
		static bool TryEndpoint(string value, out IPAddress address, out int port)
		{
			address = null;
			port = 0;
			if (string.IsNullOrEmpty(value) || value[0] != '[') return false;
			int end = value.LastIndexOf("]:", StringComparison.Ordinal);
			return end > 0 && IPAddress.TryParse(value.AsSpan(1, end - 1), out address)
				&& int.TryParse(value.AsSpan(end + 2), NumberStyles.None, CultureInfo.InvariantCulture, out port);
		}

		sealed class ForwardedTlsConnection : ITlsConnectionFeature
		{
			public ForwardedTlsConnection(X509Certificate2 certificate) => ClientCertificate = certificate;
			public X509Certificate2 ClientCertificate { get; set; }
			public Task<X509Certificate2> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
		}

#pragma warning disable SYSLIB0058 // the members System.Web reads (CipherStrength for HTTPS_KEYSIZE) are obsolete
		sealed class ForwardedTlsHandshake : ITlsHandshakeFeature
		{
			public ForwardedTlsHandshake(SslProtocols protocol, int cipherStrength)
			{
				Protocol = protocol;
				CipherStrength = cipherStrength;
			}
			public SslProtocols Protocol { get; }
			public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.None;
			public int CipherStrength { get; }
			public HashAlgorithmType HashAlgorithm => HashAlgorithmType.None;
			public int HashStrength => 0;
			public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;
			public int KeyExchangeStrength => 0;
		}
#pragma warning restore SYSLIB0058

		// Tells the host when the worker serves, and stops the worker when the host closes its stdin.
		sealed class WorkerLifetime : IHostedService
		{
			readonly IHostApplicationLifetime lifetime;
			readonly ILogger logger;

			public WorkerLifetime(IHostApplicationLifetime lifetime, ILogger<WorkerLifetime> logger)
			{
				this.lifetime = lifetime;
				this.logger = logger;
			}

			public Task StartAsync(CancellationToken cancellationToken)
			{
				lifetime.ApplicationStarted.Register(() =>
				{
					// The application's virtual path (UseWebForms(o => o.VirtualPath(...))), under which the host sends the
					// initialization pages, as IIS takes them relative to the application (#190).
					if (!Notify("ready", ApplicationVirtualPath, TimeSpan.FromSeconds(10)))
						logger.LogWarning("Worker {Id}: the host didn't take the ready message.", id);
				});
				var watch = new Thread(WatchStdin) { IsBackground = true, Name = "webForCore worker stdin" };
				watch.Start();
				return Task.CompletedTask;
			}

			// The host keeps the worker's stdin open while it wants the worker; when it closes (the host stops, or ends),
			// the worker stops. A line on it is a command: "recycle", the end of an overlapped recycle (#193).
			void WatchStdin()
			{
				try
				{
					using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.ASCII);
					string line;
					while ((line = stdin.ReadLine()) != null)
					{
						if (line == RecycleCommand) Interlocked.Exchange(ref recycleRequested, null)?.Invoke();
					}
				}
				catch (IOException) { }
				logger.LogInformation("Worker {Id}: the host closed its connection: stopping.", id);
				lifetime.StopApplication();
			}

			public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		}
	}
}
#endif
