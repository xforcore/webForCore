# What webForCore changed in Microsoft's code and tests

webForCore runs Microsoft's own ASP.NET 4.x libraries rather than reimplementations of them. Where Microsoft published
the source, webForCore compiles it from a **submodule** in `external/`: a fork of the upstream repository, cleared down
to the files webForCore uses, at a pinned commit, built for .NET 10 and tested with the upstream project's own tests.
(Until #61 the files were copied in by lift scripts; git history has that record.) This page explains the rules for
that code and what had to change.

- **Every change is a commit** on the fork's `webforcore` branch, with its reason in the message:

  | Submodule | Fork branch | Upstream commit | Changes |
  | --- | --- | --- | --- |
  | `external/AspNetWebStack` | [xforcore/AspNetWebStack `webforcore`](https://github.com/xforcore/AspNetWebStack/commits/webforcore) | `2106565` | 163, a casing fix, upstream's `.gitattributes` kept (Windows line endings for the tests), and System.Web.Http.Tracing brought back, unchanged; LINQ to SQL's `Binary` support restored (#142): `LinqBinaryModelBinder` and its test brought back, unchanged, and the changes that compiled `Binary` out reverted |
  | `external/AspNetKatana` | [xforcore/AspNetKatana `webforcore`](https://github.com/xforcore/AspNetKatana/commits/webforcore) | `096e225` | 10, and OpenIdConnect, Jwt, WsFederation, ActiveDirectory and the social providers brought back, unchanged |
  | `external/SignalR` | [xforcore/SignalR `webforcore`](https://github.com/xforcore/SignalR/commits/webforcore) | `7f53f26` | 3, and the Redis, StackExchange.Redis and SQL Server scale-out backplanes brought back, unchanged, with their tests |
  | `external/AspNetIdentity` | [xforcore/AspNetIdentity `webforcore`](https://github.com/xforcore/AspNetIdentity/commits/webforcore) | `b7fc094` | 5 |
  | `external/referencesource` | [xforcore/referencesource `webforcore`](https://github.com/xforcore/referencesource/commits/webforcore) | `ec9fa9a` | 44, and System.Data.Linq brought back (#142) |
  | `external/winforms` | [xforcore/winforms `webforcore`](https://github.com/xforcore/winforms/commits/webforcore) | `8266870` | 13 |
  | `external/mono` | [xforcore/mono `webforcore`](https://github.com/xforcore/mono/commits/webforcore) | `0f53e9e` | 32, and System.Data.Linq's tests brought back (#142) |
  | `external/maintenance-packages` | [xforcore/maintenance-packages `webforcore`](https://github.com/xforcore/maintenance-packages/commits/webforcore) | `e699209` | 1 |

  Each branch starts at the upstream commit, then one commit keeps only the files webForCore uses (and the licences),
  then the changes, one file per commit. `git log` on a submodule lists them; `git revert` undoes one.
- Licences and attributions are in [`NOTICE`](../NOTICE) and [`LICENSE`](../LICENSE).
- System.Drawing.Common 6.0 and its tests were part of webForCore until they moved to
  [drawingForCore](https://github.com/xforcore/drawingForCore) (#53), whose `docs/MODIFICATIONS.md` lists their changes.

## Rules for Microsoft's code

1. **Don't rewrite.** Each file is upstream's, with its copyright header, at the pinned commit.
2. **Every change is a commit on the fork,** one problem per commit, most with a `// webForCore:` comment that gives the
   reason (CONTRIBUTING.md rule 5). webForCore then moves the submodule to it, with the test.
3. **webForCore's own files stay in the projects,** for example `ResponseContent` for Web API, next to the `Link` items
   that compile the submodule's files at their old paths.
4. **Every file is traceable:** the submodule pins the fork's commit, and the fork's history goes back to upstream's.
5. **Identity is kept.** Assemblies keep Microsoft's names and versions. Those Microsoft strong-named are delay signed
   with Microsoft's public key (`src/SigningKey/35MSSharedLib1024.snk`), so existing references and binding
   information resolve to them. They aren't signed by Microsoft. System.Data.SqlClient carries Microsoft's other public
   key (`b03f5f7f11d50a3a`), as System.Web does, and System.Data.Linq the ECMA key (`b77a5c561934e089`), as on .NET
   Framework, both set by `FakeStrongName`. The port's assemblies (System.Web.Extensions, System.Web.Optimization,
   WebGrease, ...) have .NET Framework's or Microsoft's package's identity too, their keys set by `FakeStrongName` and
   their versions by `MicrosoftAssemblyVersion` (`Directory.Build.targets`); System.Web alone keeps 11.0.0.0, above the
   shared framework's facade. The Web Stack's source has development versions (5.3.1.0, 6.0.1.0), which Microsoft's
   builds replaced (`BUILD_GENERATED_VERSION`): webForCore defines that switch and stamps the released versions (MVC and
   Web API 5.3.0.0, System.Net.Http.Formatting 6.0.0.0, MVC Futures 5.0.0.0), without changing the source. CI compares
   every packaged assembly with Microsoft's (`tools/ci/check_assembly_identities.cs`, #141).
6. **Project files are webForCore's,** edited by hand: they name the submodule files they compile. Build steps
   Microsoft's build had are in them: SignalR's wrote the version into the hub proxy template (`Scripts/hubs.js`,
   `[!VERSION!]`) with `updateHubsJsVersion.ps1` before embedding it, on Windows only, and
   `webForCore.Microsoft.AspNet.SignalR.Core.csproj` does the same, so the resource is byte for byte Microsoft's 2.4.3
   (#118).

## Components from submodules

| Component | Upstream (pinned commit) | Licence | Files used | Files changed |
| --- | --- | --- | ---: | ---: |
| ASP.NET Web API 2, Razor 3, Web Pages 3, MVC 5, MVC Futures, Helpers, CORS, Web API tracing | [aspnet/AspNetWebStack@2106565](https://github.com/aspnet/AspNetWebStack/tree/2106565) | Apache 2.0 | 1,559 libraries · 1,269 tests | 22 · 129 |
| Katana (Microsoft.Owin 4.2.3) | [aspnet/AspNetKatana@096e225](https://github.com/aspnet/AspNetKatana/tree/096e225) | Apache 2.0 | 421 · 63 | 8 · 3 |
| ASP.NET SignalR 2.4.3 | [SignalR/SignalR@7f53f26](https://github.com/SignalR/SignalR/tree/7f53f26) | Apache 2.0 | 362 · 158 | 2 · 1 |
| ASP.NET Identity 2 | [aspnet/AspNetIdentity@b7fc094](https://github.com/aspnet/AspNetIdentity/tree/b7fc094) | MIT | 75 · 26 | 0 · 4 |
| System.Data.SqlClient 4.9.1 (replacing Microsoft's package, #41) | [dotnet/maintenance-packages@e699209](https://github.com/dotnet/maintenance-packages/tree/e699209) (`src/System.Data.SqlClient`, `src/Common`) | MIT | 270 · 18 | 1 · 0 |
| LINQ to SQL, System.Data.Linq (#142) | [microsoft/referencesource@ec9fa9a](https://github.com/microsoft/referencesource/tree/ec9fa9ae770d522a5b5f0607898044b7478574a3) (`System.Data.Linq`) | MIT | 98 | 3 |
| System.Web tests | [mono/mono@0f53e9e](https://github.com/mono/mono/tree/0f53e9e) (`mcs/class/System.Web/Test`) | MIT | 188 | 30 |
| System.Data.Linq tests | mono/mono@0f53e9e (`mcs/class/System.Data.Linq/Test`) | MIT | 9 | 1 |
| System.Configuration, System.Web.ApplicationServices, aspnet_compiler, `BindableTypeAttribute` (replacing WebFormsForCore's decompiled copies); System.Data's typed DataSet generator | [microsoft/referencesource@ec9fa9a](https://github.com/microsoft/referencesource/tree/ec9fa9ae770d522a5b5f0607898044b7478574a3) | MIT | 114 | 41 |
| ResX classes (replacing decompiled copies) | [dotnet/winforms@8266870](https://github.com/dotnet/winforms/tree/8266870) (release/8.0) | MIT | 13 | 13 |
| StronglyTypedResourceBuilder (replacing an API reconstruction) | mono/mono@0f53e9e (`mcs/class/System.Design`) | MIT | 1 | 1 |
| **Total** | | | **4,594** | **259** |

The fork itself starts from [webformsforcore/WebFormsForCore@52f4172](https://github.com/webformsforcore/WebFormsForCore/tree/52f4172dbc09b3ad0b849c97642720fa7a49aa22),
whose System.Web, System.Configuration and related assemblies were ported from Microsoft's .NET Framework reference
source. Some of that port had been decompiled from .NET Framework assemblies instead; webForCore replaced those parts
with the MIT originals (the last four rows above) or its own code ([LICENSING.md](LICENSING.md#what-was-replaced)).
webForCore's changes to the port are its git history since the fork: `git diff 52f4172 -- 'src/WebFormsForCore.*'`.

## Library changes, by kind

| Kind | Examples | Where |
| --- | --- | --- |
| **APIs .NET doesn't have** | `AppDomain.DefineDynamicAssembly` → `AssemblyBuilder.DefineDynamicAssembly` (SignalR); `AppDomainSetup.PrivateBinPath` → scan the app base (Katana's loader); `NetDataContractSerializer`, which MVC Futures' `MvcSerializer` uses, is replaced by a `DataContractSerializer` with a type-preserving resolver (`src/webForCore.Microsoft.Web.Mvc`), so `MvcSerializer.cs` itself is unchanged; Code Access Security policy removed, and LINQ to SQL's reflection-permission demands are empty; LINQ to SQL's `DBConvert` uses webForCore's BinaryFormatter port, and a debug build of it doesn't capture compiled readers to an assembly file (`AssemblyBuilderAccess.Save`) | Web Stack, Katana, SignalR, System.Data.Linq |
| **Windows-only APIs** | The CryptoAPI certificate context → `PublicKey.ExportSubjectPublicKeyInfo()` (Katana); `DpapiDataProtector` → webForCore's DPAPI emulation (`LocalDataProtection`); the HttpListener host's private-field access → Katana's Mono path | Katana |
| **.NET Framework behaviour restored** | Web Pages' `WebMail` takes its defaults from `<system.net><mailSettings>`, which .NET's `SmtpClient` ignores; `float`/`double` route constraints reject overflow; a whitespace multipart root path is rejected; `HttpResponseMessage.Content` is never null on .NET, so `ResponseContent.Get` treats `EmptyContent` as no content (11 call sites in the Web API hosts); a request body copied through the seekable stream; `HostString` keeps the host's case | Web Stack, Katana |
| **Compiler and language** | A `(string)null` where .NET's `ArraySegment<T>` overloads made `null` ambiguous; `LangVersion` pinned to what Microsoft built with (C# 7.3 for SignalR) | SignalR, Web Stack |
| **Linux and ICU** | Case-correct paths for files referenced with a different case; newline handling; IDN mapping | Web Stack tests, Katana |
| **Generated code that isn't published** | LINQ to SQL's `SR`, `Strings` and `Error` classes and string resources (in System.Data.Linq, .Mapping and .SqlClient), which Microsoft's build generated and referencesource doesn't include: `tools/linq/generate.cs` writes them (`src/webForCore.System.Data.Linq/Generated`) from referencesource's `System.Data.Linq.txt` and from data read from .NET Framework 4.8's `System.Data.Linq.dll` by `tools/linq/framework_data.cs` (`FrameworkData/System.Data.Linq.json`): resource names and messages, and each error's name, parameters, exception type and constructor arguments. No IL or code is copied; the two sources must agree where both have a string, and CI's `linq-framework-data` job reads the assembly again on Windows and fails if the data differs (#142) | System.Data.Linq |
| **Types .NET can't load** | `SqlGuidCaster` overlaid a `byte[]` on `SqlGuid`, whose field is a `Guid?` on .NET, so `Assembly.GetTypes()` threw; its unused `SqlGuidCtor` calls `new SqlGuid(byte[])`, as Microsoft.Data.SqlClient 5.2 does (#41) | System.Data.SqlClient |

## Test changes, by kind

Tests change only to fit the platform. A failing test is never edited just to make it pass: either the library is
fixed, or the BCL difference is documented.

| Kind | Examples |
| --- | --- |
| **BCL message and type differences** | `ArgumentException.Message` ends with `(Parameter 'name')`; `MediaTypeHeaderValue(null)` throws `ArgumentNullException`; `System.Private.CoreLib` instead of `mscorlib` in assembly-qualified names; the UTF-8 decoder's replacement characters; stack frames: a test asserts on MVC's own frame, not on .NET's Activator frames, which the JIT may inline (#74) |
| **Platform newline and paths** | Razor's generated code and error messages use `\n` on Linux; tests that built Windows paths build them with `Path.Combine`, which gives each platform's separator (#46) |
| **Test infrastructure** | `RunInSeparateAppDomain` and `AppDomainUtils` on collectible `AssemblyLoadContext`s (`test/webForCore.Microsoft.TestCommon`); xunit 1.9 → 2 (`Assert.ThrowsDelegate`, `Xunit.Extensions`); `PersistedAssemblyBuilder` for `AssemblyBuilderAccess.Save` |
| **Shared static state** | xUnit runs test classes in parallel. Classes that use the static `ScopeStorage` (`HtmlHelper.ValidationInputCssClassName`, `Html5DateRenderingMode`, client validation settings, or a transient scope opened by `ControllerBase.Execute` or `WebPage.ExecutePageHierarchy`) join upstream's `Uses ScopeStorage or ViewEngines.Engines` collection, which runs them one at a time, and so do the classes that assert the defaults of `ModelValidatorProviders.Providers` and `ValueProviderFactories.Factories`, which classes in that collection clear (#113, #114) |
| **Windows-only test dependencies** | DPAPI in Katana's security tests → `TestDataProtectionProvider` (AES-CBC and HMAC-SHA256); LocalDB → SQL Server in a container (Identity); a test certificate .NET's DER reader rejects outside Windows |
| **Time zone** | Identity's `LockoutEndToNowPlus5ShouldNotBeLockedOut` assumes Pacific time: the suite runs with `TZ`, and the test takes Pacific time itself, since .NET on Windows ignores `TZ` (#46) |
| **Mono's System.Web tests** | Corrected to Microsoft's .NET Framework 4.5+ behaviour wherever Mono's own implementation differed (Mono's tests follow Mono); hosting in the test process (.NET has one AppDomain) |
| **Mono's System.Data.Linq tests** | One test expected the `System.Exception` that Mono's own `Error` class throws where .NET Framework's throws `ArgumentException` (#142) |
| **webForCore's identities** | MVC Futures' `VersionTest` expects System.Web.Mvc 5.3.0.0, the released version webForCore's has, not the source's development version 5.3.1.0 (#141) |
| **Selection** | Microsoft's own categories are used (`category!=failing`). SignalR's IIS Express tests aren't built, nor the Service Bus backplanes (`WindowsAzure.ServiceBus` is .NET Framework only), which have none |

## webForCore's changes to System.Web and the hosting layer

These aren't in the submodules. They change WebFormsForCore's port, and each has a `webForCore:` comment; the
commit that made it, and its issue, say why.

- **Hosting:** webforcore-host and its worker processes (process recycling, #56), IIS's `<system.webServer>` modules,
  the Kestrel worker request (WebSockets, client certificates, server variables, timeouts, integrated-pipeline APIs).
  See [IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md).
- **A restart drains as on .NET Framework** (#181): the host object (`AspNetCoreHost`, Cassini's registered object)
  unregisters as soon as the hosting environment stops it, as IIS's `ISAPIRuntime` does, so the old worker's requests
  are drained by `HttpRuntime.Dispose` for `<httpRuntime shutdownTimeout>`, unchanged from the reference source.
  `HttpRuntime.ReleaseResourcesAndUnloadAppDomain`, which can't unload an AppDomain on .NET, completes
  `HttpRuntime.Unloaded` instead, and the worker process stops then. The host object used to wait for the requests
  itself and abort them after `<hostingEnvironment shutdownTimeout>`, and the worker stopped Kestrel at once, which
  aborted them after its own 30 s.
  The request `HttpRuntime.ProcessRequestInternal` turns away once `Dispose` has drained ("Server Too Busy", 503: it
  reached the old worker before the application began shutting down, and the runtime only after the drain) carries
  the worker's restarting header, so that webforcore-host sends it to the next worker (#209), with its body: the
  host reads a small body ahead, and has the worker's Kestrel ask for a larger one (`Expect: 100-continue`), which
  `AspNetCoreWorkerRequest.ParseHeaders` hides from System.Web (#214). `WebFormsMiddleware` counts a request among
  `HttpRuntime`'s active requests (`IncrementActivePipelineCount`) before it checks that the application is still
  running, until the runtime has completed its response, so that the drain can't miss a request between the two (#214).
  `AspNetCoreHost.AbortRequests` leaves a request whose response the runtime has completed to Kestrel (#214).
  `HttpResponse.WriteErrorMessage`, before it flushes the error page it closes the connection after
  (`CloseConnectionAfterError`: a body over `maxRequestLength`), asks the worker request to have the client's
  connection closed, as IIS closes it (`Connection: close`, from webforcore-host, which keeps its connection to the
  worker); request filtering's rejections (`IIS/IISModules.cs`) do the same (#219).
  `AspNetCoreWorkerRequest.CloseConnection` sends `Connection: close` when the response hasn't started. In a worker of
  webforcore-host, once the response has started, it leaves the connection to the host open (Kestrel drains the
  unread body): a close the response didn't announce reset the next request the host's pool sent on that connection.
  Request filtering's 413.1 for a body over `maxAllowedContentLength` (`IIS/IISModules.cs`) lifts Kestrel's body limit
  for that request, so that Kestrel drains the body rather than closing the connection.
  `AspNetCoreWorkerRequest.ParseHeaders` stores the request's unknown headers whatever the path: the cookieless
  session check it took from IIS's filter returned before storing them when a path had a `(` and no `/(...)/`
  (#170, OData's `/People(1)` lost `Prefer`).
- **Integrated-mode semantics** on the classic pipeline: writable `Response.Headers`, the Content-Type rule, the
  server name from the Host header, `X-AspNet-Version` and the default `Cache-Control: private` only once a managed
  handler ran, the response was flushed or an error was reported; a cache header (`Expires`, `Cache-Control`, ...) set
  through `Response.Headers` stays when the cache policy writes its own, which then replaces only the same names, as
  in IIS's header block (Katana's cookie middleware's `Expires: -1`, found by the OpenID Connect comparison with IIS 10);
  `Response.Redirect` sets `text/html` as in the integrated pipeline, so a redirect before any handler ran (customErrors'
  for a page that doesn't exist) has its Content-Type (#192).
  Server variables an application sets (`Request.ServerVariables["REMOTE_ADDR"] = ...`) are kept by the worker request,
  as IIS keeps them in its own block, and it answers `UserHostAddress`, `IsSecureConnection`, `LOCAL_ADDR`,
  `SERVER_NAME` and `SERVER_PORT` with them, as `IIS7WorkerRequest` does (`HttpServerVarsCollection` hands them to it, #194).
- **.NET's base class library, where an application sees the difference:** `HttpException` and its subclasses built
  with a null message have .NET Framework's `Exception` default ("Exception of type '...' was thrown."), not .NET's
  `ExternalException` one ("External component has thrown an exception."): Page.HandleError's `HttpUnhandledException`
  on error pages and in `Server.GetLastError()` (#192).
- **Configuration:** protected configuration on Linux (DPAPI emulation, RSA key containers as files), section type
  identity across load contexts, `machine.config` refreshed by content.
- **Compilation:** csc and vbc run in process, through Roslyn's public API (below), and `AssemblyBuilder` loads every
  compiled assembly into the app's context (never CodeDom's `Assembly.LoadFile`, which loaded App_Code a second time,
  #56); precompilation for .NET
  (`System.Private.CoreLib` as the core library); typed DataSets in App_Code (the Reference Source's
  `System.Data.TypedDataSetGenerator` and webForCore's TableAdapter generator); `BuildManager.ResolveAssembly` looks the
  fixed names (`App_GlobalResources`, `App_Code`, `__code`) up by simple name too, because .NET passes the display name,
  so `Assembly.Load("App_GlobalResources")` finds the compiled assembly, which keeps its random name as on IIS (#33).
  Off Windows, when App_Code's new assembly has the path of one already loaded, `CodeDirectoryCompiler` fails at
  once with the reference source's "already loaded" error: it used to force garbage collections for 3 seconds waiting
  for it to unload, which it can't in a worker process (#57).
  `<system.codedom>` entries naming .NET Framework's `Microsoft.CSharp.CSharpCodeProvider` or
  `Microsoft.VisualBasic.VBCodeProvider` (System.CodeDom's types on .NET) resolve to System.Web's own C# and VB
  providers, keeping the entry's options (`CompilerInfo.CodeDomProviderType`, #88).
- **Additions removed under CONTRIBUTING.md rules 6 and 7 (#57):** the unused `WebFormsTaskScheduler` (a public type
  .NET Framework's System.Web never had, which reflected into `ThreadPoolTaskScheduler` and `Task`), a debug-only
  `#if DebugWF4C` block in `HttpApplication.ExecuteStep`, and the `GCUtil.Alloc`/`Free` wrappers (`GCUtil`,
  `RootedObjects`, `WorkItem`, `AspCompat` and `FileChangesMonitor` call `GCHandle` directly again, as the reference
  source does). The BinaryFormatter port no longer looks up `SerializationInfo.StartDeserialization` by reflection;
  the lookup found nothing on .NET 10, so .NET's SerializationGuard was never on, and isn't now
  (`SerializationGuardTest`). System.Web's C# and VB CodeDom providers (`CSharpCodeProviderBase`, `VBCodeProviderBase`)
  take the generator, the converters and member generation from System.CodeDom's public `CSharpCodeProvider` and
  `VBCodeProvider` instead of creating its internal types by reflection (`CodeDomTests`). The `dotnet csc.dll` and
  `dotnet vbc.dll` command lines CodeDom gives are compiled in process through Roslyn's public API
  (`WebFormsForCore.Compilers/Compiler.cs`): Roslyn's command-line parser, `CSharpCompilation`/`VisualBasicCompilation`,
  `Emit`, and the diagnostics in csc's and vbc's text (vbc's with the source line underlined), which CodeDom parses
  into `CompilerError`s. It used to call Roslyn's internal `Csc.Run`/`Vbc.Run`, with its internal `BuildPaths` and
  `AnalyzerAssemblyLoader`, by private reflection. The output, the assembly and the PDB are byte for byte what csc and
  vbc write (the order of the warnings from methods compiled in parallel varies from run to run, in csc too). A
  command line that needs something it doesn't do (analyzers, `/doc`, `/errorlog` and the like), or that
  csc reports an error on before compiling (a source file or a reference that isn't there), runs in a csc
  or vbc process, given the application's `runtimeconfig.json`: an application's bin has csc.dll and vbc.dll without
  theirs. The logo line is in English whatever the UI culture. Off Windows the compilation folder's name ends with the
  user's name from `Environment.UserName` (`Temporary_ASP.NET_Files-www-data`), with any character other than an ASCII
  letter, a digit, `.`, `_` or `-` replaced by `_`, instead of the user's number from libc's `geteuid()` by P/Invoke
  (`Temporary_ASP.NET_Files-33`); the public `HttpRuntime.UserId` that .NET Framework never had is gone
  (`HttpRuntime.CodegenDirNameForCurrentUser`). `tools/ci/check_code_rules.cs` keeps new breaks out.
- **`<system.net>`:** `<defaultProxy>` applied at application start, and `<mailSettings>` used by System.Web's mail
  senders (`WebFormsForCore.Net.Configuration.NetConfiguration`).
- **Build targets** (`src/webForCore.WebStack.targets`, imported by every app through the `webForCore.Web` package):
  - The shared framework has facades named `System.Web.dll` and `System.Configuration.dll`, and the host keeps whichever
    copy has the higher `assemblyVersion` in `deps.json`. The SDK writes none for project references, so the
    framework's 4.0.0.0 facade won and loading System.Web failed with 0x80131053 ("The requested assembly version
    conflicts with what is already bound"). A target after `GenerateBuildDependencyFile` writes each app-local
    assembly's real `assemblyVersion` where a reference pack has one of the same name.
  - The framework's `System.Web.HttpUtility.dll` (and the `System.Web` facade) are left out of the compiler's references
    next to webForCore's System.Web (CS0433, CS7069 otherwise). They are matched by the reference pack's name, not by
    the SDK's `packs` folder: an SDK newer than the target framework takes the pack from the NuGet cache
    (`microsoft.netcore.app.ref/<version>/`). `tools/ci/package-smoke.sh` builds once that way.
  - One System.Data.SqlClient (#41, #195): Microsoft's package when the build has it at 4.9.0 or later, otherwise
    `webForCore.Data.SqlClient`; the other's compile, runtime and runtime-target assets are removed as soon as NuGet's
    assets are resolved (`WebForCoreSystemDataSqlClient` decides it explicitly).
- **Run-time compilation** adds the targets of a referenced facade's type forwards (CS0012 otherwise), leaving out
  those whose types System.Web defines itself (`HttpUtility`).
- **BinaryFormatter port:** surrogates for .NET Framework types .NET no longer serializes (`NameValueCollection`
  and others), so session state and view state round-trip. `Type`, the runtime's `MethodInfo`, `ConstructorInfo`,
  `FieldInfo`, `PropertyInfo` and `EventInfo`, `Missing`, `Assembly` and `Module` are written as .NET Framework 4.8
  writes them (`System.UnitySerializationHolder`, `System.Reflection.MemberInfoSerializationHolder`;
  `ReflectionSurrogates.cs`, #156), and the reader binds those two names (in mscorlib) to the reading half of
  referencesource's `UnitySerializationHolder` and `MemberInfoSerializationHolder`, lifted into the port with marked
  changes: types and assemblies are found as the rest of the reader finds them, mscorlib's assembly and module are
  .NET's core library's, .NET has no `System.Empty` (unity type 1 throws `SerializationException`), and a
  generic method whose type arguments aren't complete yet waits for them instead of throwing. The core library's
  types that have no `TypeForwardedFrom` are written as mscorlib's.
- **System.Configuration's `AssemblyRef.SystemConfiguration`** (webForCore's `AssemblyRef.cs`) names
  System.Configuration.ConfigurationManager 14.5.0.0, this assembly, instead of .NET's System.Configuration 4.0.0.0
  facade, which forwards only public types: the Reference Source creates `ConfigurationManagerInternal`,
  `ClientConfigurationHost` and `InternalConfigConfigurationFactory` by that name, so every `ApplicationSettingsBase`
  threw `TypeLoadException` (#154). Microsoft's files are unchanged.
- **Security:** `bin`, `App_Data`, the build output folder and the other hidden segments return 404, as on IIS.
- **Full trust only** (`Hosting/ApplicationManager.Core.cs`, `HttpRuntime.cs`): .NET has no Code Access Security, so
  a partial `<trust level>` (Medium, High, ...) is accepted and the application runs in full trust, as with
  `level="Full"`: `HttpRuntime`'s trust level is "Full" and there is no named permission set. ASP.NET read the level's
  policy file (`web_mediumtrust.config`), which webForCore doesn't ship, and every request was a Configuration Error
  (#87, Umbraco 6).
- **File change notifications off Windows** (`FileChangesMonitor.cs`, `Util/FileUtil.cs`): `FindFileData` names a
  file as `FindFirstFile` does (its name, or its path relative to the monitored root, without the Windows-only parent
  walk), and the `FileSystemWatcher` that replaces `ReadDirectoryChangesW` reports names relative to the watched
  directory, as `ReadDirectoryChangesW` does (#2). `bin` and the critical directories (`App_Code`, ...) are watched
  on every platform, as in the reference source; WebFormsForCore watched them only on Windows (`HttpRuntime.cs`, #51).
  On Linux the directories inside the application's folder share a recursive `FileSystemWatcher` of the folder per
  notify filter (`WebFormsForCore/SharedDirectoryWatcher.cs`, called from `DirMonCompletion`), which passes each change
  to the directories that would have seen it with a watcher of their own: on .NET 10 each `FileSystemWatcher` is an
  inotify instance, of which a user has 128 (#145, #152). `HttpRuntime.HostingInit` creates the `FileChangesMonitor`
  again with `HostingEnvironment.FcnMode`: `HttpRuntime`'s static constructor runs before the hosting environment has
  the application's `<httpRuntime fcnMode>`, which was ignored (#146). In Single mode (`DirectoryMonitor`,
  `GetFileMonitorForSpecialDirectory`) a change below a special directory goes to the directory it is in first, so
  `bin_dotnet`'s changes are `bin_dotnet`'s (they matched `bin` as a prefix), and then by the reference source's prefix
  comparison (`binaries` is still `bin`'s, as on IIS); and a folder below one that is reported as modified without
  having been created or written since monitoring started is ignored (`IsFolderUnchangedSinceMonitoringStarted`): on
  Windows that is a freshly copied folder's deferred LastWriteTime, written when the application first opens it, and
  a freshly copied site restarted at its first request, which IIS 10 does in some runs and not in others (#167).
- **No ASP.NET telemetry** (`TelemetryLogger.cs`): .NET Framework wrote handler, provider and target-framework events
  to Windows' telemetry (`TelemetryEventSource`, in a provider group Windows' telemetry service enables). webForCore
  writes none; on Windows, TraceLogging's per-thread type cache also kept an application's domain alive (#46).
- **Paths with `/` as the separator:** `configSource` paths written with `\` resolve on Linux
  (`InternalConfigHost.StaticGetStreamNameForConfigSource`, #26), and `/` no longer makes a mapped physical path invalid
  under `relaxedUrlToFileSystemMapping` (`HttpRuntime.s_InvalidPhysicalPathChars`, #27).
- **Assembly loading** (`AspNetCoreHosting/AssemblyLoaderNetCore.cs`): where the reference source calls
  `Type.GetType(typeName, throwOnError, ...)`, the port passes an assembly resolver for the application's load
  context, and .NET's `Type.GetType` lets a resolver's exceptions through whatever `throwOnError` says. The resolver
  (`AssemblyLoaderNetCore.TypeAssemblyResolver`) returns null for an assembly that isn't there unless `throwOnError`,
  so `BuildManager.GetType(name, false)` and the other non-throwing lookups (`HttpProfileBase`, `StateManagedCollection`,
  `HttpStaticObjectsCollection`, `HostingEnvironment`, `ApplicationManager`) return null, as on .NET Framework (#137,
  DNN and ClientDependency). `HttpRuntime.InitFusion` reads `<runtime><assemblyBinding><probing privatePath>` from the
  application's web.config, and the load context's resolver searches those folders after the build output, in fusion's
  order (#147, Orchard; [NAMESPACES.md](NAMESPACES.md)). As fusion does, it checks the version only of a strong-named
  reference: for one without a public key token, the assembly of that name it finds is the one, whatever its version
  (#171, JabbR deploys Nancy 0.20.0 and a library built against 0.21.1). It reads `<dependentAssembly>` too and applies
  `<bindingRedirect>` and `<codeBase>` as fusion does (#158; a customer application has about 60 redirects, many to a
  version lower than or equal to the one its references were built against): a redirected reference loads the new
  version through the load context, and a code base is the only place a version is looked for. The call in
  `HttpRuntime.InitFusion` is webForCore's own (`AssemblyLoaderNetCore.ReadAssemblyBinding`).
- **System.Web.DynamicData** (`src/WebFormsForCore.Web.DynamicData`, now built and in the "Always" set): the Entity
  Framework 1–4 model provider, the `EntityDataSource` hookup and EF 5 spatial column types are compiled out
  (`#if NETFRAMEWORK`); the LINQ to SQL model provider and the `LinqDataSource` hookups are built again, as the reference
  source has them, now that System.Data.Linq is (#142); `AssemblyRef.cs` and `ScaffoldTableAttribute` (which .NET dropped) are
  added (#8).
- **`Response.End()` keeps the response buffered** (`HttpResponse.End`), as after .NET Framework's thread abort: it
  throws the `ThreadAbortException` that `HttpApplication.ExecuteStep` observes, and calls `CompleteRequest`, for
  code that catches the exception without rethrowing it. WebFormsForCore flushed the response first, so `EndRequest`'s
  modules could no longer change it, and forms authentication sent the 401 itself instead of its redirect to the
  log-in page (#129). `Redirect(url, true)` and `Server.Transfer` end through it.
- **Response buffers from `ArrayPool`** (`HttpWriter.cs`, `HttpResponseBufferElement`): .NET has no unmanaged buffer
  pool, so where .NET Framework's writer took `HttpResponseUnmanagedBufferElement`s (aspnet_isapi's or IIS's buffer
  pool), webForCore's takes managed buffers rented from `ArrayPool<byte>.Shared` (`CreateNewMemoryBufferElement`).
  A rented buffer behaves as the unmanaged one did: it goes back to the pool when the writer recycles it, unless the
  output cache keeps it (`DisableRecycling`), and `GetBytes` copies its content. The arrays the element only wraps
  (an unbuffered `Response.Write`'s, a clone's, a substitution's, the output cache's) are the GC's, as on .NET
  Framework. Every array used to go back to the pool: an unbuffered `Response.Write(string)` threw "The buffer is not
  associated with this pool" and the response never ended (Bonobo Git Server's smart HTTP), a cached response's
  buffer could be handed to another response, and a provider of the application's own cached the whole pool buffer, so
  hits sent the page followed by another response's bytes (#184).
- **A response ASP.NET chunked itself ends or is aborted** (`AspNetCoreWorkerRequest.EndOfRequest`): classic mode's
  `HttpResponse` writes `Transfer-Encoding: chunked` and the chunks of an unbuffered response itself, and Kestrel
  passes them through. When the last chunk never went out (sending failed, and so did writing the error), the request
  ended and Kestrel kept the connection: the client waited for ever. The worker aborts the connection then (#184).
- **No `Content-Length` set through `Response.Headers` on a 304** (`HttpResponse.WriteHeaders`): IIS's integrated
  pipeline sends none with a 304 Not Modified; its classic pipeline sends one `AppendHeader` set. The emulated
  integrated `Response.Headers` (`AppendHeaderThroughHeaders`) marks a `Content-Length` set through it, and a 304 leaves
  that one out: Web API's OWIN adapter sets `Content-Length: 0` for an empty response (a customer application's
  CacheCow.Server.WebApi 304s, compared with IIS 10).
- **URLs find files in any case off Windows** (`Configuration/UserMapPath.cs`): `ResolveIgnoreCase`, which already
  found configuration paths, is internal so that the worker request (`Request.PhysicalPath`) and the IIS modules map a
  request's path with it too (#140).
- **No kernel32 for the machine name** (`HttpServerUtility.GetMachineNameInternal`): `Environment.MachineName` instead of
  `GetComputerName`, which threw `DllNotFoundException` off Windows (ELMAH's log pages, #128). On Windows .NET reads it
  with `GetComputerName` too. The P/Invoke declaration is left in `UnsafeNativeMethods.cs`, unused.
- **webengine4.dll's HMAC-SHA1 in managed code** (`UnsafeNativeMethods.GetHMACSHA1Hash`, `GetSHA1Hash`): .NET
  Framework's native engine isn't shipped, so `MachineKeySection`'s legacy MAC (Framework20SP1/SP2 forms tickets and
  view state, `MachineKey.Encode` in every mode) computes it with `IncrementalHash`. Like webengine4's, it fails for
  any hash size but SHA1's, so that `validation="HMACSHA256"`, `"HMACSHA384"` and `"HMACSHA512"` use the
  `KeyedHashAlgorithm` of that name, as on .NET Framework; it had returned HMAC-SHA1 padded with zeros, which .NET
  Framework rejects (#191, checked against .NET Framework 4.8 by `MachineKeyInteropTests`).
- **aspnet_regsql's scripts as resources** (`Management/SqlServices.cs`, `WebFormsForCore/SqlScripts.cs`): `SqlServices`
  reads `InstallCommon.sql` and the others from System.Web's resources, which are .NET Framework 4.8's files
  (`FrameworkData/Sql`, read as data from its folder on a Windows CI runner: referencesource doesn't have them), with
  the name matched without case, as Windows does (`InstallProfile.SQL`). It read them from
  `HttpRuntime.AspInstallDirectory`, .NET's folder on webForCore, which has none (#123). `webforcore-regsql`
  (`src/WebFormsForCore.RegSql`) is webForCore's own aspnet_regsql, on `SqlServices`.
- **CodeDom provider** (`WebFormsForCore/CodeDom`, from Microsoft's RoslynCodeDomProvider): `CompilerOptions` may be
  null (the `/nowarn:` check webForCore added didn't allow it), and an assembly compiled in memory without a PDB is
  loaded without a symbol stream, as `Assembly.Load(byte[], null)` was on .NET Framework (#135).
