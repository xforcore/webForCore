#if NETCOREAPP

using System;
using System.Globalization;
using System.Security.Permissions;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Loader;
using System.Reflection;
using System.Linq;
using System.IO;
using System.Web;
using System.Web.Hosting;
using Core = Microsoft.AspNetCore.Http;

namespace System.Web.Hosting
{
    public class AspNetCoreHost : MarshalByRefObject, IRegisteredObject
    {
        private bool disableDirectoryListing;

        private string installPath;

        private string lowerCasedClientScriptPathWithTrailingSlash;

        private string lowerCasedVirtualPath;

        private string lowerCasedVirtualPathWithTrailingSlash;

        // webForCore: the requests in progress, for AbortRequests.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Core.HttpContext, byte> requests =
            new System.Collections.Concurrent.ConcurrentDictionary<Core.HttpContext, byte>();
        private int unregistered;
        // webForCore: set when the hosting environment stops the host (#49).
        private int stopped;

        private string physicalClientScriptPath;

        private string physicalPath;

        private int port;

        private bool requireAuthentication;

		private IntPtr processToken;

		private string processUser;

        private string virtualPath;
        public bool HandleAllRequestsWithWebForms { get; set; } = false;
		public string[] HandleExtensions { get; set; } = new string[] { ".aspx", ".ashx", ".asmx", ".axd" };
        // webForCore: only files System.Web knows nothing about. The machine.config maps the others as .NET Framework's root
        // web.config does (HttpForbiddenHandler for *.cs, *.vb, *.resx, *.master, *.asax, *.ascx, *.config, ...: 403;
        // *.cshtml and *.vbhtml served by Web Pages when it is enabled), so they go to System.Web as on IIS.
        public string[] ProhibitedExtensions { get; set; } = new string[] { ".razor", "appsettings.json", ".deps.json", ".runtimeconfig.json" };
        // webForCore: IIS request filtering's default hidden segments (answered 404), plus .NET's build folder. The app's
        // output folder (bin_dotnet) is hidden too (IsHiddenSegment). Without this, with HandleAllRequestsWithWebForms,
        // System.Web's static file handler served the app's assemblies and build files.
        public string[] HiddenSegments { get; set; } = new string[] { "bin", "App_Code", "App_GlobalResources", "App_LocalResources",
            "App_WebReferences", "App_Data", "App_Browsers", "obj" };
        public string[] DefaultDocuments { get; set; } = new string[] { "index.htm", "index.html", "Index.html", "Index.htm", "Default.htm", "iistart.htm", "Default.aspx", "default.aspx" };
        public bool UseClassicMode = false;
        public Compilation.ClientBuildManager ClientBuildManager { get; set; } 
		public AppDomain AppDomain
        {
            get { return AppDomain.CurrentDomain; }
        }
		public AssemblyLoadContext LoadContext
		{
			get { return ApplicationManager.GetLoadContext(Assembly.GetCallingAssembly()); }
		}

		public AspNetCoreHost()
        {
            HostingEnvironment.RegisterObject(this);

        }

        public bool DisableDirectoryListing
        {
            get { return disableDirectoryListing; }
        }

        public string InstallPath
        {
            get { return installPath; }
        }

        public string NormalizedClientScriptPath
        {
            get { return lowerCasedClientScriptPathWithTrailingSlash; }
        }

        public string NormalizedVirtualPath
        {
            get { return lowerCasedVirtualPathWithTrailingSlash; }
        }

        public string PhysicalClientScriptPath
        {
            get { return physicalClientScriptPath; }
        }

        public string PhysicalPath
        {
            get { return physicalPath; }
        }

        public int Port
        {
            get { return port; }
        }

        public bool RequireAuthentication
        {
            get { return requireAuthentication; }
        }

        public string VirtualPath
        {
            get { return virtualPath; }
        }

        public HostingEnvironment HostingEnvironment { get; set; }

        // webForCore: called once when the hosting environment starts shutting the application down.
        public Action OnShutdown;

        // webForCore: creates the host for WebFormsMiddleware, the application's only start in this process (#56). The
        // host is returned as delegates.
        internal static object[] CreateForMiddleware(string virtualPath, string physicalPath, string[] handleExtensions,
            string[] addHandleExtensions, string[] defaultDocuments, string[] addDefaultDocuments, bool handleAllRequestsWithWebForms,
            bool useClassicMode, bool useAspNetCoreSession, bool iisModules, Action onShutdown)
        {
            AssemblyLoaderNetCore.Init();
            // webForCore: HttpRuntime sets "DataDirectory" in the application's load-context data
            // (ApplicationManager.SetLoadContextData); on IIS it is the AppDomain's, where data providers that expand
            // |DataDirectory| themselves read it (System.Data.SQLite: Bonobo Git Server's database was created in
            // bin_dotnet; SqlClient's AttachDbFilename). One application per worker process, so it is that application's (#50).
            AppDomain.CurrentDomain.SetData("DataDirectory", Path.Combine(physicalPath, HttpRuntime.DataDirectoryName));
            if (iisModules) WebFormsForCore.Hosting.IIS.WebServerModules.Configuration = new WebFormsForCore.Hosting.IIS.WebServerConfiguration(physicalPath);
            if (useAspNetCoreSession) SessionState.SessionStateModule.UseAspNetCoreSessionProvider();

            var applicationManager = new ApplicationManager();
            // create BuildManagerHost in the worker app domain
            Type buildManagerHostType = typeof(System.Web.Compilation.BuildManagerHost);
            // webForCore: HttpRuntime.AppDomainAppId is IIS's metabase path of the application, as the worker request's
            // APPL_MD_PATH (site 1), not a hash of the folder's path (Cassini's ApplicationHost): the same on every server
            // of a farm, as on IIS when the servers' site IDs match. SQL Server session state keys sessions by it
            // (ASPStateTempApplications, SqlSessionStateProviderAsync), so servers with the site in different folders
            // didn't share sessions.
            var appId = "/LM/W3SVC/1/ROOT" + (virtualPath == "/" ? "" : virtualPath.TrimEnd('/'));
            var vpath = System.Web.VirtualPath.Create(virtualPath);
            var buildManagerHost = (System.Web.Compilation.BuildManagerHost)applicationManager.CreateInstanceInNewWorkerLoadContext(buildManagerHostType, appId, vpath, physicalPath, false);
            // call BuildManagerHost.RegisterAssembly to make Host type loadable in the worker app domain
            var hostType = typeof(AspNetCoreHost);
            buildManagerHost.RegisterAssembly(hostType.Assembly.FullName, hostType.Assembly.Location);
            // create Host in the worker app domain
            var host = (AspNetCoreHost)applicationManager.CreateObject(appId, hostType, virtualPath, physicalPath, false);

            if (handleExtensions != null) host.HandleExtensions = handleExtensions;
            if (addHandleExtensions.Length > 0) host.HandleExtensions = host.HandleExtensions.Concat(addHandleExtensions).ToArray();
            if (defaultDocuments != null) host.DefaultDocuments = defaultDocuments;
            if (addDefaultDocuments.Length > 0) host.DefaultDocuments = host.DefaultDocuments.Concat(addDefaultDocuments).ToArray();
            host.HandleAllRequestsWithWebForms = handleAllRequestsWithWebForms;
            host.UseClassicMode = useClassicMode;
            // webForCore: the hosting environment can stop the application before the middleware listens for it: a change
            // while the worker was starting (#49). The middleware then hears of it now, and the worker is replaced, rather
            // than it serving on with a runtime that answers every request with 503 (Server Too Busy).
            Interlocked.Exchange(ref host.OnShutdown, onShutdown);
            if (Volatile.Read(ref host.stopped) != 0 || HostingEnvironment.ShutdownInitiated)
                Interlocked.Exchange(ref host.OnShutdown, null)?.Invoke();
            host.Configure(virtualPath, physicalPath);

            return new object[] {
                new Func<Core.HttpContext, bool>(host.IsLegacyRequest),
                new Func<Core.HttpContext, Task>(host.ProcessRequest),
                new Action(host.Shutdown),
                new Func<string, bool>(host.RouteClaims),
                new Func<Task>(() => HttpRuntime.Unloaded),
                new Action(host.AbortRequests),
            };
        }

        // webForCore: whether one of the application's routes takes the URL, for the IIS modules in front of it. On IIS,
        // UrlRoutingModule maps a request to its route's handler before the native modules' courtesy redirect of a
        // directory to its trailing slash, which runs only for the static file handler; RouteTable.GetRouteData skips
        // existing files and directories unless RouteExistingFiles is set, as on IIS (#13). The routes are those of the
        // started application: Application_Start runs with the first request it processes. GetRouteData doesn't skip the
        // application's own URL ("~/"), which an application under a virtual path has without its trailing slash too
        // ("/app", #190).
        public bool RouteClaims(string virtualPath)
        {
            var routes = System.Web.Routing.RouteTable.Routes;
            if (routes.Count == 0 || !routes.RouteExistingFiles && !IsVirtualPathAppPath(virtualPath)) return false;
            try
            {
                var request = new HttpRequest(string.Empty, "http://localhost" + virtualPath, string.Empty);
                var context = new HttpContextWrapper(new HttpContext(request, new HttpResponse(System.IO.TextWriter.Null)));
                return routes.GetRouteData(context) != null;
            }
            catch (Exception)
            {
                return false; // a route that can't be evaluated outside a request leaves the URL to IIS's behaviour
            }
        }

        #region IRegisteredObject Members

        void IRegisteredObject.Stop(bool immediate)
        {
            // webForCore: tell the middleware first, so that new requests go to the next worker while this one drains.
            Interlocked.Exchange(ref stopped, 1);
            Interlocked.Exchange(ref OnShutdown, null)?.Invoke();

            // webForCore: unregister at once, as IIS's ISAPIRuntime does (#181). The requests in progress are not this
            // object's to wait for: once the registered objects have stopped, HttpRuntime.Dispose waits for them for
            // <httpRuntime shutdownTimeout> (90 s), as on .NET Framework, and <hostingEnvironment shutdownTimeout> (30 s)
            // is the wait for the application's own registered objects only. This waited for the requests here, and
            // Stop(true) aborted them after <hostingEnvironment shutdownTimeout>: a request running across a restart (DNN's
            // installer, which changes bin) was cut.
            Unregister();
        }

        // webForCore: the runtime has completed the request's response (AspNetCoreWorkerRequest.Process): AbortRequests leaves
        // it to Kestrel to finish sending. Aborting it reset a response the host was still reading, so the client's
        // connection was reset, for a request the application had processed (#214).
        internal void RequestCompleted(Core.HttpContext context) => requests.TryRemove(context, out _);

        // webForCore: aborts the requests still in progress once HttpRuntime.Dispose has waited for them, where .NET
        // Framework unloaded the AppDomain (#181).
        internal void AbortRequests()
        {
            foreach (var context in requests.Keys)
            {
                try { context.Abort(); } catch { }
            }
        }

        // webForCore: only unregister, as a registered object does (Cassini's Host). This called HttpRuntime.Close(),
        // which marked the runtime as shutting down, so that the hosting environment's shutdown, which called Stop,
        // then skipped releasing the runtime and unloading the domain (HttpRuntime.ShutdownAppDomain).
        private void Unregister()
        {
            if (Interlocked.Exchange(ref unregistered, 1) == 0) HostingEnvironment.UnregisterObject(this);
        }

        #endregion

        public void Configure(string virtualPath, string physicalPath,
                              bool requireAuthentication)
        {
            Configure(virtualPath, physicalPath, requireAuthentication, false);
        }

        public void Configure(string virtualPath, string physicalPath)
        {
            Configure(virtualPath, physicalPath, false, false);
        }

        public void Configure(string virtualPath, string physicalPath,
                              bool requireAuthentication, bool disableDirectoryListing)
        {
            installPath = null;
            if (!virtualPath.StartsWith('/')) virtualPath = "/" + virtualPath;
            this.virtualPath = virtualPath;
            this.requireAuthentication = requireAuthentication;
            this.disableDirectoryListing = disableDirectoryListing;
            lowerCasedVirtualPath = CultureInfo.InvariantCulture.TextInfo.ToLower(virtualPath);
            lowerCasedVirtualPathWithTrailingSlash = virtualPath.EndsWith("/", StringComparison.Ordinal)
                                                          ? virtualPath
                                                          : virtualPath + "/";
            lowerCasedVirtualPathWithTrailingSlash =
                CultureInfo.InvariantCulture.TextInfo.ToLower(lowerCasedVirtualPathWithTrailingSlash);
            this.physicalPath = physicalPath;

			var assembly = GetType().Assembly;
            ApplicationManager.SetLoadContextData(".appPath", physicalPath, assembly);
			ApplicationManager.SetLoadContextData(".appVPath", virtualPath, assembly);

			physicalClientScriptPath = HttpRuntime.AspClientScriptPhysicalPath + Path.DirectorySeparatorChar;
            lowerCasedClientScriptPathWithTrailingSlash =
                CultureInfo.InvariantCulture.TextInfo.ToLower(HttpRuntime.AspClientScriptVirtualPath + "/");

        }

		private void ObtainProcessToken()
		{
			/*
			if (Interop.ImpersonateSelf(2))
			{
				Interop.OpenThreadToken(Interop.GetCurrentThread(), 0xf01ff, true, ref processToken);
				Interop.RevertToSelf();
				// ReSharper disable PossibleNullReferenceException
				// ReSharper restore PossibleNullReferenceException
			} */
		    processUser = WindowsIdentity.GetCurrent().Name;
		}


		public SecurityIdentifier GetProcessSid() => WindowsIdentity.GetCurrent().User;
        public IntPtr GetProcessToken() => WindowsIdentity.GetCurrent().Token;
        public string GetProcessUser() => WindowsIdentity.GetCurrent().Name;

        public override object InitializeLifetimeService()
        {
            // never expire the license
            return null;
        }

        public bool IsVirtualPathAppPath(string path)
        {
            if (path == null)
            {
                return false;
            }
            path = CultureInfo.InvariantCulture.TextInfo.ToLower(path);
            return (path == lowerCasedVirtualPath || path == lowerCasedVirtualPathWithTrailingSlash);
        }

        public bool IsVirtualPathInApp(string path, out bool isClientScriptPath)
        {
            isClientScriptPath = false;

            if (path == null)
            {
                return false;
            }

            if (virtualPath == "/" && path.StartsWith("/", StringComparison.Ordinal))
            {
                if (path.StartsWith(lowerCasedClientScriptPathWithTrailingSlash, StringComparison.Ordinal))
                {
                    isClientScriptPath = true;
                }
                return true;
            }

            path = CultureInfo.InvariantCulture.TextInfo.ToLower(path);

            if (path.StartsWith(lowerCasedVirtualPathWithTrailingSlash, StringComparison.Ordinal))
            {
                return true;
            }

            if (path == lowerCasedVirtualPath)
            {
                return true;
            }

            if (path.StartsWith(lowerCasedClientScriptPathWithTrailingSlash, StringComparison.Ordinal))
            {
                isClientScriptPath = true;
                return true;
            }

            return false;
        }

        public string MapPath(string path) => HostingEnvironment.MapPath(path);

        // webForCore: see HiddenSegments. Any segment of the path, compared ignoring case, as IIS does.
        bool IsHiddenSegment(string path)
        {
            string output = Path.GetFileName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (HiddenSegments.Any(hidden => string.Equals(hidden, segment, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(output, segment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        public bool IsLegacyRequest(Core.HttpContext context)
        {
            var path = context.Request.Path.Value;
            var pathWithoutSlash = path;
            if (path.EndsWith('/')) pathWithoutSlash = path[..^1];
            if (path == "") path = "/";
            if (path != "/") path = pathWithoutSlash;

			// webForCore: path starts with "/"; this put a second one after the path base ("/app//x").
			var fullpath = context.Request.PathBase.Value.TrimEnd('/') + path;

            if (IsHiddenSegment(path)) return false;

			if (HandleExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) )) 
                return true;
            if (HandleExtensions.Any(ext => path.LastIndexOf('/') is var index and > 0 && path[..index].EndsWith(ext, StringComparison.OrdinalIgnoreCase))) 
                return true;
            if (ProhibitedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) return false;
            // webForCore: ASP.NET Core's environment settings files (appsettings.Production.json, ...), not only appsettings.json.
            string fileName = path[(path.LastIndexOf('/') + 1)..];
            if (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

            var mappedPath = MapPath(fullpath);
            if (Directory.Exists(mappedPath))
            {
				var defaultDoc = DefaultDocuments
                    .Select(doc => new { Mapped = Path.Combine(mappedPath, doc), Virtual = $"{pathWithoutSlash}/{doc}" })
                    .FirstOrDefault(doc => File.Exists(doc.Mapped));
                if (defaultDoc != null)
                {
                    context.Request.Path = defaultDoc.Virtual;
                    return true;
                }
            }

			return HandleAllRequestsWithWebForms || File.Exists(mappedPath) &&
                Path.GetDirectoryName(mappedPath) != AppDomain.CurrentDomain.BaseDirectory; // Do not serve bin_dotnet directory
		}

		public bool IsVirtualPathInApp(String path)
        {
            bool isClientScriptPath;
            return IsVirtualPathInApp(path, out isClientScriptPath);
        }

        public async Task ProcessRequest(Core.HttpContext context)
        {
            requests.TryAdd(context, 0);
            try
            {
                if (UseClassicMode) HttpRuntime.SetClassicPipeline();

                await new AspNetCoreWorkerRequest(this, context).Process();
            }
            finally
            {
                // webForCore: also when the request throws.
                requests.TryRemove(context, out _);
            }
        }

        [SecurityPermission(SecurityAction.Assert, Unrestricted = true)]
        public void Shutdown()
        {
            HostingEnvironment.InitiateShutdown();
        }
    }
}

#endif