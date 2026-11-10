// ASP.NET Core middleware

#if NETCOREAPP

using Microsoft.AspNetCore.Builder;
using Core = Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Web.SessionState;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using WebFormsForCore.Hosting;
using WebFormsForCore.Hosting.IIS;

namespace Microsoft.AspNetCore.Builder
{
	// webForCore: the application in this process, as IIS's worker process ran one start of it (#56). The process is the
	// unit of restart: when the application restarts (a Web.config, bin or App_Code change, HttpRuntime.UnloadAppDomain,
	// a recycling limit), a worker of webforcore-host asks the host for its replacement and stops once the host has
	// switched to it; an application run on its own stops, for whatever started it to start it again.
	public class WebFormsMiddleware
	{
		public string VirtualPath { get; set; }
		public string PhysicalPath { get; set; }
		public string AppId { get; set; }
		public bool HandleAllRequestsWithWebForms = false;
		public bool UseClassicMode = true;
		public IEnumerable<string> HandleExtensions = null;
		public IEnumerable<string> AddHandleExtensions = Array.Empty<string>();
		public IEnumerable<string> DefaultDocuments = null;
		public IEnumerable<string> AddDefaultDocuments = Array.Empty<string>();
		public bool UseAspNetCoreSession = false;
		public readonly RecyclingOptions Recycling = new RecyclingOptions();
		// webForCore: IIS's modules in front of the application, from <system.webServer> (IIS/IISModules.cs).
		public readonly IISModules.Options IIS = new IISModules.Options();
		IISModules iisModules;

		private readonly Core.RequestDelegate next;
		private readonly IHostApplicationLifetime lifetime;
		private readonly ILogger logger;
		private readonly Timer recycleTimer;
		private readonly Application app;
		private volatile bool stopping;

		// The application's host entry points.
		sealed class Application
		{
			public Func<Core.HttpContext, bool> IsLegacyRequest;
			public Func<Core.HttpContext, Task> ProcessRequest;
			public Action Shutdown;
			public Func<string, bool> RouteClaims;
			// Completes once the runtime has drained its requests and released its resources after a shutdown (#181).
			public Func<Task> Unloaded;
			// Aborts the requests still in progress.
			public Action AbortRequests;
			public DateTime Started = DateTime.UtcNow;
			public long Requests;
			public int Retired;
			// An application pool recycle (Recycling: time, requests, private memory), not a restart of the application.
			public volatile bool ProcessRecycle;
			// The host was asked for an overlapped recycle (#193): it says when to recycle.
			public int Overlapping;
		}

		// IIS application pool recycling (applicationHost.config <recycling><periodicRestart>), with IIS's defaults.
		public class RecyclingOptions
		{
			// <periodicRestart time="1.05:00:00"/>; TimeSpan.Zero disables.
			public TimeSpan Time { get; set; } = TimeSpan.FromMinutes(1740);
			// <periodicRestart requests="0"/>; 0 disables.
			public long Requests { get; set; }
			// <periodicRestart privateMemory="0"/> in KB, the process's private bytes; 0 disables.
			public long PrivateMemory { get; set; }
			// webForCore: how often the recycling conditions are checked.
			public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);
		}

		public WebFormsMiddleware(Core.RequestDelegate next, Action<WebFormsOptions> optionsBuilder,
			IHostApplicationLifetime lifetime = null, ILoggerFactory loggerFactory = null, IWebHostEnvironment environment = null,
			WebServerConfigurationAccessor webServerAccessor = null)
		{
			this.next = next;
			this.lifetime = lifetime;
			logger = (ILogger)loggerFactory?.CreateLogger<WebFormsMiddleware>() ?? NullLogger.Instance;
			var path = AppDomain.CurrentDomain.BaseDirectory;
			if (path.EndsWith(Path.DirectorySeparatorChar.ToString())) path = path.Substring(0, path.Length - 1);
			PhysicalPath = Path.GetDirectoryName(path);
			VirtualPath = "/";
			// app.UseAspNetCoreSessionProvider() sets the provider before UseWebForms.
			UseAspNetCoreSession = SessionStateModule.ProviderOverride != null;

			optionsBuilder?.Invoke(new WebFormsOptions(this));

			AppId = ApplicationManager.CreateApplicationId(VirtualPath, PhysicalPath);
			WorkerProcess.ApplicationVirtualPath = VirtualPath;
			if (IIS.Enabled)
			{
				var webServer = new WebServerConfiguration(PhysicalPath);
				// The application's output folder (bin_dotnet) and .NET's build folder are not part of the site.
				webServer.ExcludedDirectories.Add(Path.GetFileName(path));
				webServer.ExcludedDirectories.Add("obj");
				// W3C logs an application keeps in the site are never served (#15).
				var services = webServerAccessor?.Options;
				if (services != null && services.W3CLogging)
				{
					var logs = Path.GetRelativePath(PhysicalPath, Path.GetFullPath(services.LogDirectory));
					if (!logs.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(logs) && logs != ".")
						webServer.ExcludedDirectories.Add(logs.Replace(Path.DirectorySeparatorChar, '/') + "/");
				}
				iisModules = new IISModules(webServer, IIS, loggerFactory, environment);
				iisModules.RouteClaims = virtualPath => app.RouteClaims?.Invoke(virtualPath) ?? false;
				if (webServerAccessor != null) webServerAccessor.Configuration = webServer;
			}
			lifetime?.ApplicationStopping.Register(() =>
			{
				stopping = true;
				app?.Shutdown?.Invoke();
			});
			app = StartApplication();
			WarnOfInvariantCulture();
			recycleTimer = new Timer(_ => CheckRecycling(), null, Recycling.CheckInterval, Recycling.CheckInterval);
		}

		Application StartApplication()
		{
			var app = new Application();
			Action onShutdown = () => OnApplicationShutdown(app);
			var host = AspNetCoreHost.CreateForMiddleware(VirtualPath, PhysicalPath, HandleExtensions?.ToArray(), AddHandleExtensions.ToArray(),
				DefaultDocuments?.ToArray(), AddDefaultDocuments.ToArray(), HandleAllRequestsWithWebForms, UseClassicMode, UseAspNetCoreSession,
				IIS.Enabled, onShutdown);
			app.IsLegacyRequest = (Func<Core.HttpContext, bool>)host[0];
			app.ProcessRequest = (Func<Core.HttpContext, Task>)host[1];
			app.Shutdown = (Action)host[2];
			app.RouteClaims = (Func<string, bool>)host[3];
			app.Unloaded = (Func<Task>)host[4];
			app.AbortRequests = (Action)host[5];
			logger.LogInformation("Started application {AppId} in process {ProcessId}{Worker}.", AppId, Environment.ProcessId,
				WorkerProcess.IsWorker ? $" (worker {WorkerProcess.WorkerId} of webforcore-host)" : "");
			// webForCore: the runtime couldn't start the application (a configuration error, or file change monitoring that
			// couldn't start: off Windows usually the user's inotify instances used up, #107). As on IIS, each request shows
			// the error and the application restarts after it; IIS also wrote the error to the event log.
			if (HttpRuntime.HostingInitFailed && HttpRuntime.InitializationException is Exception error)
				logger.LogError(error, "Application {AppId} didn't start: {Message} Each request shows this error and restarts the application.",
					AppId, error.GetBaseException().Message);
			return app;
		}

		// webForCore: a process without a locale (LANG and LC_ALL unset, or C, POSIX, C.UTF-8) has the invariant culture,
		// which can't happen on IIS (#97). Only when <globalization> doesn't give requests a culture of their own ("auto"
		// alone falls back to the process's): with one, only code outside requests runs invariant, as docs/MIGRATING.md says.
		void WarnOfInvariantCulture()
		{
			static bool Sets(string culture) => !string.IsNullOrEmpty(culture) && !culture.Equals("auto", StringComparison.OrdinalIgnoreCase);
			try
			{
				var globalization = System.Web.Configuration.RuntimeConfig.GetAppConfig().Globalization;
				if (CultureInfo.CurrentCulture.Name.Length == 0 && !Sets(globalization.Culture) ||
					CultureInfo.CurrentUICulture.Name.Length == 0 && !Sets(globalization.UICulture))
					logger.LogWarning("Application {AppId} runs with the invariant culture: the process has no locale (LANG and LC_ALL " +
						"unset, or C, POSIX or C.UTF-8), which can't happen on IIS, so resources chosen by culture name aren't found. " +
						"Set <globalization culture=\"en-US\" uiCulture=\"en-US\" /> in Web.config, or give the environment a locale " +
						"(LANG=en_US.UTF-8): https://github.com/xforcore/webForCore/blob/main/docs/MIGRATING.md#6-code-changes", AppId);
			}
			catch (Exception e)
			{
				// A Web.config error shows on the application's pages.
				logger.LogDebug(e, "Application {AppId}: couldn't read <globalization>.", AppId);
			}
		}

		// The application's hosting environment is shutting down (file change, UnloadAppDomain, recycling, idle).
		void OnApplicationShutdown(Application app)
		{
			if (Interlocked.Exchange(ref app.Retired, 1) != 0) return;
			var reason = HostingEnvironment.ShutdownReason;
			logger.LogInformation("Application {AppId} is shutting down ({Reason}).", AppId, reason);
			if (stopping) return;
			// #181: the requests in progress run on while HttpRuntime.Dispose waits for them, for <httpRuntime shutdownTimeout>
			// and a sixth of it again, as on .NET Framework; the process stops after that (StopWhenUnloadedAsync).
			if (WorkerProcess.IsWorker)
			{
				// The host sends new requests to the next worker; this one finishes those it has and exits. The host allows it
				// the drain time beyond its shutdown time limit, except for an application pool recycle, which IIS allowed
				// the shutdown time limit alone.
				var drain = app.ProcessRecycle ? 0 : (int)Math.Ceiling(DrainTime().TotalSeconds);
				if (!WorkerProcess.Notify("recycle", drain.ToString(CultureInfo.InvariantCulture) + " " + reason, TimeSpan.FromSeconds(10)))
				{
					logger.LogWarning("Application {AppId}: the host didn't take the recycle message; stopping the process.", AppId);
					Stop();
					return;
				}
			}
			else
			{
				// Without webforcore-host there is no next worker: the process stops, for its service manager to start it again.
				logger.LogWarning("Application {AppId} restarted outside webforcore-host: stopping the process once its requests have finished.", AppId);
			}
			_ = StopWhenUnloadedAsync(app);
		}

		// What HttpRuntime.Dispose waits for the requests at most: <httpRuntime shutdownTimeout>, then a sixth of it again.
		static TimeSpan DrainTime()
		{
			var timeout = TimeSpan.FromSeconds(System.Web.Configuration.HttpRuntimeSection.DefaultShutdownTimeout);
			try
			{
				var runtime = System.Web.Configuration.RuntimeConfig.GetAppLKGConfig().HttpRuntime;
				if (runtime != null) timeout = runtime.ShutdownTimeout;
			}
			catch (Exception)
			{
				// HttpRuntime.Dispose falls back to the default too.
			}
			return timeout + timeout / 6;
		}

		// Once the runtime has drained its requests and released its resources (Application_End), where .NET Framework
		// unloaded the AppDomain: the requests still in progress are aborted, as the unload aborted them, and the process
		// stops.
		async Task StopWhenUnloadedAsync(Application app)
		{
			await app.Unloaded();
			app.AbortRequests();
			Stop();
		}

		// Once the process has started: stopping it while it starts (a file change during startup) cancels its start, and the
		// application's app.Run() throws. The callback runs at once if it has already started.
		void Stop() => lifetime?.ApplicationStarted.Register(lifetime.StopApplication);

		void Recycle(Application app, string reason)
		{
			if (app == null || app.Retired != 0) return;
			// An application pool recycle is overlapped, as IIS overlapped it (#193): a worker of webforcore-host serves on
			// while the host starts (and warms) the next, and recycles when the host says so.
			if (WorkerProcess.IsWorker)
			{
				if (Interlocked.Exchange(ref app.Overlapping, 1) != 0) return;
				if (WorkerProcess.RequestOverlappedRecycle(reason, () => RecycleNow(app, reason)))
				{
					logger.LogInformation("Recycling application {AppId}: {Reason}. It serves until the next worker is ready.", AppId, reason);
					return;
				}
			}
			RecycleNow(app, reason);
		}

		void RecycleNow(Application app, string reason)
		{
			if (app.Retired != 0) return;
			logger.LogInformation("Recycling application {AppId}: {Reason}.", AppId, reason);
			app.ProcessRecycle = true;
			app.Shutdown();
		}

		void CheckRecycling()
		{
			if (app.Retired != 0 || stopping) return;
			if (Recycling.Time > TimeSpan.Zero && DateTime.UtcNow - app.Started >= Recycling.Time)
				Recycle(app, $"periodic restart after {Recycling.Time}");
			else if (Recycling.PrivateMemory > 0)
			{
				using var process = System.Diagnostics.Process.GetCurrentProcess();
				if (process.PrivateMemorySize64 / 1024 >= Recycling.PrivateMemory)
					Recycle(app, $"private memory {process.PrivateMemorySize64 / 1024} KB over the limit of {Recycling.PrivateMemory} KB");
			}
		}

		public void RestartApplication() => Recycle(app, "restart requested");

		public void AllowSynchronousIO(Core.HttpContext context)
		{
			var syncIoFeature = context.Features.Get<Core.Features.IHttpBodyControlFeature>();
			if (syncIoFeature != null)
			{
				syncIoFeature.AllowSynchronousIO = true;
			}
		}

		public Task Invoke(Core.HttpContext context)
		{
			// webForCore: the request's path as IIS gives it, and its URL before any rewrite, for Request.RawUrl (#126, #127).
			AspNetCoreWorkerRequest.BeginRequest(context);
			if (!InApplication(context.Request)) return next.Invoke(context);
			return iisModules != null ? iisModules.Invoke(context, InvokeApplication) : InvokeApplication(context);
		}

		// webForCore: an application under a virtual path (VirtualPath("/app")) is IIS's application /app of a site (#190).
		// Its requests are the URLs at and below the path, in any case, as IIS matches them; the path becomes the request's
		// PathBase, as UsePathBase would give it (and may already have), so that the IIS modules see the URL relative to the
		// application, as IIS applies the application's Web.config, and System.Web sees the whole URL. The others aren't the
		// application's: they go on to the rest of the pipeline, which answers 404 at its end, where IIS gave them to the
		// site's other applications. They were the application's, and a path outside it failed with 500.
		bool InApplication(Core.HttpRequest request)
		{
			if (VirtualPath == "/") return true;
			string path = (request.PathBase.Value ?? "") + (request.Path.Value ?? "");
			if (!path.StartsWith(VirtualPath, StringComparison.OrdinalIgnoreCase)
				|| path.Length > VirtualPath.Length && path[VirtualPath.Length] != '/') return false;
			request.PathBase = new Core.PathString(path.Substring(0, VirtualPath.Length));
			request.Path = new Core.PathString(path.Substring(VirtualPath.Length));
			return true;
		}

		async Task InvokeApplication(Core.HttpContext context)
		{
			// webForCore: the request counts as one of the runtime's (HttpRuntime's active requests) before the check that
			// the application is still running, and until its response is complete (#214). HttpRuntime.Dispose drains the
			// requests only after Retired is set, so either the request sees Retired and goes to the next worker, or the
			// drain waits for it. It used to be counted only once it reached HttpRuntime.ProcessRequestInternal, after a
			// thread hop: a request in that gap when the drain found none running got "Server Too Busy" (#209), or was
			// aborted with the requests still in progress when the worker stopped (AbortRequests), and one whose response
			// was still being completed was aborted too. The count ends once the runtime has completed the response
			// (AspNetCoreWorkerRequest.Process): an accepted WebSocket runs on after that, and is aborted when the worker
			// stops, as on IIS.
			int released = 0;
			void Release()
			{
				if (Interlocked.Exchange(ref released, 1) == 0) HttpRuntime.DecrementActivePipelineCount();
			}
			HttpRuntime.IncrementActivePipelineCount();
			context.Items[AspNetCoreWorkerRequest.ReleaseRuntimeCountKey] = (Action)Release;
			try
			{
				if (Volatile.Read(ref app.Retired) != 0)
				{
					// The application is shutting down: the request goes to the next worker (the host sends it again, with
					// its body, #214), as IIS sent new requests to the new process during a recycle.
					context.Response.StatusCode = Core.StatusCodes.Status503ServiceUnavailable;
					context.Response.Headers[WorkerProcess.RestartingHeader] = "1";
					context.Response.Headers.RetryAfter = "1";
					return;
				}
				if (app.IsLegacyRequest(context))
				{
					AllowSynchronousIO(context);
					var requests = Interlocked.Increment(ref app.Requests);
					await app.ProcessRequest(context);
					if (Recycling.Requests > 0 && requests == Recycling.Requests)
						Recycle(app, $"{requests} requests");
				}
				else
				{
					// ASP.NET Core's own middleware, not the runtime's: the drain doesn't wait for it.
					Release();
					await next.Invoke(context);
				}
			}
			finally
			{
				Release();
			}
		}

		public virtual bool IsLegacyRequest(Core.HttpContext context) => app.IsLegacyRequest(context);
	}

	public class WebFormsOptions
	{
		WebFormsMiddleware Instance;

		public WebFormsOptions(WebFormsMiddleware instance) { Instance = instance; }
		public WebFormsOptions HandleExtensions(params string[] extensions) { Instance.HandleExtensions = extensions; return this; }
		public WebFormsOptions AddHandleExtensions(params string[] extensions) { Instance.AddHandleExtensions = Instance.AddHandleExtensions.Concat(extensions).ToArray(); return this; }
		public WebFormsOptions DefaultDocuments(params string[] docs) { Instance.DefaultDocuments = docs; return this; }
		public WebFormsOptions AddDefaultDocuments(params string[] docs) { Instance.AddDefaultDocuments = Instance.AddDefaultDocuments.Concat(docs).ToArray(); return this; }
		// The application's virtual path, as IIS's application /app of a site: HttpRuntime.AppDomainAppVirtualPath,
		// Request.ApplicationPath and the URLs System.Web, MVC and Web API make from "~/" start with it, and only the requests
		// at and below it are the application's (docs/MIGRATING.md, #190). "/" by default.
		public WebFormsOptions VirtualPath(string path) {
			if (!path.StartsWith('/')) path = "/" + path;
			// webForCore: without a trailing slash, as IIS names applications ("/app/" is "/app").
			if (path.Length > 1) path = path.TrimEnd('/');
			Instance.VirtualPath = path.Length == 0 ? "/" : path;
			return this;
		}
		public WebFormsOptions PhysicalPath(string path) { Instance.PhysicalPath = path; return this; }
		public WebFormsOptions HandleAllRequestsWithWebForms() { Instance.HandleAllRequestsWithWebForms = true; return this; }

        public WebFormsOptions UseClassicMode() { Instance.UseClassicMode = true; return this; }
        public WebFormsOptions UseIntegratedMode() { Instance.UseClassicMode = false; return this; }
        public WebFormsOptions UseAspNetCoreSessionProvider()
        {
            // Equivalent to a web.config entry of:
            // <sessionState mode="Custom" customProvider="AspNetCoreSession">
            //     <providers>
            //         <add name="AspNetCoreSession" type="System.Web.SessionState.AspNetCoreSessionProvider, System.Web" />
            //     </providers>
            // </sessionState>
            // Set when the application starts (AspNetCoreHost.CreateForMiddleware).
            Instance.UseAspNetCoreSession = true;
            return this;
        }
        public WebFormsOptions Recycling(Action<WebFormsMiddleware.RecyclingOptions> configure) { configure(Instance.Recycling); return this; }
        // webForCore: see WebFormsMiddleware.IIS; IIS(o => o.Enabled = false) leaves <system.webServer> to the host.
        public WebFormsOptions IIS(Action<IISModules.Options> configure) { configure(Instance.IIS); return this; }
    }

    public static class WebFormsMiddlewareExtensions
	{
		public static IApplicationBuilder UseWebForms(this IApplicationBuilder builder, Action<WebFormsOptions> optionsBuilder = null)
		{
			AssemblyLoaderNetCore.Init();
			if (optionsBuilder == null) optionsBuilder = options => { };
			// webForCore: W3C logging and compression, when services.AddWebForms() registered them (IIS/IISServices.cs).
			WebFormsServiceCollectionExtensions.UseWebFormsServices(builder);
			// webForCore: HttpContext.AcceptWebSocketRequest needs ASP.NET Core's WebSocket handshake (IIS's WebSocket module).
			builder.UseWebSockets();
			return builder.UseMiddleware<WebFormsMiddleware>(optionsBuilder);
        }
        public static IApplicationBuilder UseAspNetCoreSessionProvider(this IApplicationBuilder builder)
        {
            AssemblyLoaderNetCore.Init();

            // Equivalent to a web.config entry of:
            // <sessionState mode="Custom" customProvider="AspNetCoreSession">
            //     <providers>
            //         <add name="AspNetCoreSession" type="System.Web.SessionState.AspNetCoreSessionProvider, System.Web" />
            //     </providers>
            // </sessionState>
            SessionStateModule.UseAspNetCoreSessionProvider();

			return builder;
        }

    }
}

#endif
