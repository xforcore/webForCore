# Tests

webForCore is tested mainly with **other people's tests**: the suites Microsoft and Mono wrote for the libraries
webForCore runs. They come with the code from the `external/` submodules ([MODIFICATIONS.md](MODIFICATIONS.md))
and run unchanged, except where .NET's base class library behaves differently from .NET Framework's; those corrections
are commits on the forks, like every other change. On top of them, webForCore's own **gate suites** run real sample applications
on Kestrel and check them over HTTP against IIS's documented behaviour.

**Latest run (0.7.0, 3 Nov 2026, Linux x64, .NET 10.0.14, Release): 23,447 tests in 24 suites. 23,423 pass, 24 are
skipped with a reason, and none fail.** System.Drawing.Common's own tests run in
[drawingForCore](https://github.com/xforcore/drawingForCore)'s CI. System.Web.MonoTests skips 11 (6 before 0.7.0): Mono's 5
`SimpleWorkerRequestTest` tests that create application hosts need an AppDomain per host, and are skipped again now that
webForCore has no in-process AppDomains (#56).

## Running the tests

```bash
dotnet build src/WebFormsForCore.Build/WebFormsForCore.Build.csproj -c Release
dotnet build WebFormsForCore.slnx -c Release
tools/ci/sqlserver.sh          # SQL Server in a container for Identity.Test and System.Data.Linq.Tests (podman or docker)
tools/ci/redis.sh              # Redis in a container for SessionCache.Gate.Tests (and SignalR scale-out)
tools/ci/run-tests.sh            # every suite; .trx files and summary.md in ./TestResults
```

One suite: `dotnet test test/webForCore.<Suite>/webForCore.<Suite>.csproj -c Release`.

CI (`.github/workflows/ci.yml`) runs the same script on Ubuntu 24.04 for every push and pull request, with SQL Server
and Redis as service containers. A suite fails CI on any failing test.

System.Drawing and System.Drawing.Common come from [drawingForCore](https://github.com/xforcore/drawingForCore)'s
packages, and Microsoft's System.Drawing.Common tests run in that repository's CI, with its known failures. Here,
WebImage and Chart (System.Web.Helpers.Test), and the gate samples, test webForCore's use of it.

Windows is supported as a development environment (#46). CI's `build-windows` job runs the same script on Windows Server
2025 under Git Bash, with SQL Server Express on the same address and login in place of the container
(`tools/ci/sqlserver-windows.ps1`) and Memurai, a Redis-compatible server, in place of the Redis container
(`tools/ci/redis-windows.ps1`), and then packs and runs the package smoke test. The results match Linux's: every
suite passes. Two IIS gate tests run only off Windows, because on Windows protected configuration uses Windows'
own key containers and DPAPI, as on .NET Framework: `AppReadsProtectedSections` (the sample's sections are encrypted with
key files and the DPAPI emulation) and `RegIISProtectExportImportUnprotect` (it would create key containers in the
machine's store). To run the suites on Windows: Git for Windows, the SDK in `global.json`, and
SQL Server (Express is enough) at `127.0.0.1,14333` with the `sa` login of `tools/ci/sqlserver.sh`, and Redis (Memurai) at
`127.0.0.1:16379`; then the commands
above in Git Bash.

The default branch, `main`, is protected by the repository ruleset `default-branch` (#76), the same in every
xforcore repository: changes land only through a pull request (no approvals required), and it merges only once CI's
`build-and-test` and `build-windows` jobs have passed on it (packing and the package smoke test included, on both) with
the branch up to date with `main`. If it is behind, `gh pr update-branch <n>` merges the base in and CI runs
again. A release likewise needs both jobs. Force pushes and deleting the branch are blocked, and nobody can bypass the
rules, administrators included. Version bumps therefore go through a pull request too, before the `v<Version>` tag is
pushed on the merged commit; tags aren't covered by the ruleset. GitHub runs CI on a first-time contributor's pull
request only after a maintainer approves the run.

After the tests, CI packs the NuGet packages (version `<Version>-ci.<run number>`; a `v<Version>` tag builds the
release version and attaches the packages to a GitHub release, with `docs/release-notes/<version>.md` as its notes) and runs `tools/ci/package-smoke.sh`: a Web Forms app
that references only the `webForCore` package from that folder is built, started, and must serve a page and code
compiled from App_Code, and with `relaxedUrlToFileSystemMapping` on, map a path (`MapPath.aspx`). Then `tools/ci/package-smoke-mvc`, an MVC 5 and OWIN app with the third-party packages real
applications have (Unity.Mvc, WebActivatorEx, Katana's Google provider) as they have them, must render Razor views with webForCore's MVC, Razor, Web Pages and Helpers in its `deps.json`,
construct a controller through Unity (started from `Application_Start`, since WebActivatorEx's start methods don't
run on webForCore), and redirect a Google challenge. With webForCore.Microsoft.Owin.Security.OpenIdConnect and .Jwt and IdentityServer3.Contrib.AccessTokenValidation 4.1.16 (built on Microsoft's Jwt 4.0.1 and IdentityModel 5.6), its `deps.json` must have one IdentityModel version and webForCore's Katana assemblies, and `/oidc/challenge` must redirect to Entra ID's authorize endpoint. It also has Entity Framework 6.5.1, which brings Microsoft's
`System.Data.SqlClient` 4.8.6: none of that package may be in the output or `deps.json`, and `/Scan` must find
webForCore's System.Data.SqlClient, Entity Framework bound to it, and `Assembly.GetTypes()` working on every loaded
assembly (#41). Web API OData's `ODataConventionModelBuilder.GetEdmModel()` must map LINQ to SQL's `Binary`, with the
System.Data.Linq the `webForCore` package brings, and the Web Forms app's Web.config lists System.Data.Linq 3.5.0.0 in
`<compilation><assemblies>`, which `Linq.aspx` compiles against (#142). The MVC app is built a second time with Microsoft's
`System.Data.SqlClient` 4.9.1 package referenced: that must be the only System.Data.SqlClient in its `deps.json`, and MVC,
Web API (`/api/ping`) and SignalR (`/signalr/hubs`) must find their controllers and hubs although `GetTypes()` throws on
it (#195). Last, `tools/ci/package-smoke-sql.sh` builds `tools/ci/package-smoke-sql`, a Web Forms app, with
webForCore's System.Data.SqlClient and with Microsoft's 4.9.1 (through a library it references): each build must have
that one alone in `deps.json`, the same `deps.json` when built again from scratch, and `Sql.aspx` must work against
SQL Server with it (SqlDataSource, membership, roles, profile, SQL Server session state, SqlCacheDependency,
DbProviderFactories, LINQ to SQL, SqlServices) and with Microsoft.Data.SqlClient 5.2 beside it (#195). The first Web Forms app (`tools/ci/package-smoke`) is built with
`<LangVersion>7.3</LangVersion>`, as MIGRATING.md says for a project that set none, and has a WCF `[ServiceContract]`,
which CoreWCF's source generator turned into C# 11 code (#134). The MVC app has BundleTransformer.Core 1.10, built
against Microsoft's System.Web.Optimization 1.1.0.0, and `/Bundling` must report Microsoft's identities for
System.Web.Optimization and WebGrease (#141). Before the smoke test, `tools/ci/check_assembly_identities.cs` checks
every assembly in the packages against the Microsoft assembly it replaces, read from Microsoft's NuGet package (name,
version, culture and public key token; `tools/ci/assembly-identities.json` says where each one is, and why System.Web
and System.Configuration.ConfigurationManager have higher versions), and fails on any difference (#141).

CI's `linq-framework-data` job (#142) reads .NET Framework 4.8's `System.Data.Linq.dll` on a Windows runner with
`tools/linq/framework_data.cs` and fails if the names, messages and exception types differ from the committed
`src/webForCore.System.Data.Linq/FrameworkData/System.Data.Linq.json`, which LINQ to SQL's generated classes come from;
`build-and-test` checks that the generated classes are what `tools/linq/generate.cs` writes. It is not a required
check.

CI's `serialization-framework-streams` job (#156) runs `tools/serialization/FrameworkStreams` on a Windows runner: it
writes the graphs of `test/webForCore.Serialization.Tests/FrameworkStreams/FrameworkStreamCases.cs` with .NET Framework
4.8's BinaryFormatter, reads them back on .NET Framework, and fails if a committed stream (`FrameworkStreams/*.bin`)
differs or doesn't read there. Serialization.Tests checks that the port reads those streams and writes the same bytes,
so together they check both directions. A new case's stream comes from the job's `serialization-framework-streams`
artifact. It is not a required check.

CI's `regsql-framework-data` job (#123) copies the `.sql` files of .NET Framework 4.8's folder
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`) on a Windows runner and runs its `aspnet_regsql -sqlexportonly`, and
fails if they differ from System.Web's scripts (`src/WebFormsForCore.Web/FrameworkData/Sql`) or from the exports
`SqlServicesTests` compares webforcore-regsql's with (`test/webForCore.IIS.Gate.Tests/FrameworkData`). It is not a
required check either.

CI's `machinekey-framework-interop` job (#191) checks that what webForCore protects with a `<machineKey>` reads on .NET
Framework 4.8 and the reverse. `test/webForCore.MachineKey.Sample/Interop` lists the configurations (validation SHA1,
HMACSHA256 and HMACSHA512; decryption AES and 3DES; `compatibilityMode` Framework20SP1, Framework20SP2 and Framework45,
none with `<httpRuntime targetFramework="4.5">` and none at all; `ticketCompatibilityMode="Framework40"`; `IsolateApps`
and `IsolateByAppId`) and what is protected with each: `FormsAuthentication.Encrypt`, `MachineKey.Protect` (with and
without purposes), `MachineKey.Encode`, and Katana cookies, a plain one and ASP.NET Identity's. IIS.Gate.Tests'
`MachineKeyInteropTests` runs the sample once for each configuration, has it read .NET Framework's outputs
(`test/webForCore.IIS.Gate.Tests/FrameworkData/machinekey-framework.json`; the cookies through Katana's cookie
middleware on `Microsoft.Owin.Host.SystemWeb`), and writes its own (the `machinekey-webforcore` artifact). The job, after
`build-and-test`, runs the same `Interop` code on .NET Framework 4.8 (`tools/machinekey/FrameworkMachineKey`, an
application domain per configuration): it reads webForCore's outputs and the committed ones, and fails if a committed
output that .NET Framework writes the same every time differs. Outputs with a random IV differ on every call, so they
are compared by reading each other's; deterministic ones are compared byte for byte too. Its output is the
`machinekey-framework` artifact, with a report in the job's summary. It is not a required check.

`build-and-test` and `build-windows` then run `tools/ci/testapp-smoke.sh` (#80): `src/WebFormsForCore.TestApp` is rebuilt with its pages newer than
their designer files, so that this repository's designer-file task runs (upstream's WebFormsForCore.Build package,
which the TestApp used before, started `dotnet.exe` there and failed on Linux), its `deps.json` must be the SDK's with
System.Web's real `assemblyVersion` (no checked-in `deps.json`), and the app must start and serve its pages.

### Compared with IIS 10

CI's `iis-compare` job (#78) sends the Web API gate's requests to the same sample on IIS 10. WebApi.Gate.Tests writes each
request it sent on Kestrel, with the response, to `gate-responses.json` (uploaded by `build-and-test` as the
`gate-responses` artifact). On `windows-2025` (Windows Server 2025, IIS 10.0, .NET Framework 4.8.1), the job builds
`test/webForCore.WebApi.Sample.Iis`: the sample's own source files, `Global.asax` and `Web.config`, for .NET Framework 4.8
against Microsoft's Web API 5.3.0 and Katana 4.2.3 packages, with the binding redirects NuGet would add. It serves it
from IIS twice (`tools/ci/iis-site.ps1`), in an integrated-pipeline and a classic-pipeline application pool, replays
every recorded request on both (`dotnet run tools/ci/iis_compare.cs -- replay`) and compares status, headers and body
with the integrated pipeline's (`... -- diff`), after replacing each side's base address and removing stack traces. The
sample is written for the integrated pipeline and doesn't run in the classic one on IIS; that run is in the report for
the record. Every difference must be one that `tools/ci/iis-known-differences.json`
lists and [IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md#compared-with-iis-10) records, and every listed one must still
occur; the report is in the job's summary and both JSON files in its `iis-responses` artifact. The job needs nothing of
webForCore's build, so it takes a few minutes after `build-and-test`. It is not a required check.

The job does the same for request handling (#126, #127, #129, #140). `test/webForCore.Requests.Sample` is a Web Forms
site of pages, `App_Code` and static files only, so IIS serves the folder as it is, from a classic-pipeline and an
integrated-pipeline application pool. IIS.Gate.Tests' `RequestHandlingTests` writes each request it sent to the sample
on Kestrel, with the response, to `requests-responses.json` (the `requests-responses` artifact); the job replays them
on both and compares with the classic pipeline's, webForCore's, against `tools/ci/iis-known-differences-requests.json`
(`--gate=Request handling`). The integrated pipeline's report is for the record. A request recorded with headers of its own is
sent with them (#170).

Last, `<httpRuntime fcnMode>` (#167): `tools/ci/iis_fcn.cs` writes a site with `fcnMode="Single"` and one with
`"Default"` (with files in `bin\cs`, `bin\de` and `bin\runtimes\win-x64\native`), IIS serves them, and it checks that the
first request doesn't restart the application and which of these do: a new file in `bin\cs`, `bin\cs`'s LastWriteTime
set, a new file in a folder named `binaries`. The job fails if IIS decides otherwise than Host.Tests'
`AChangeInBinDotnetRestartsTheApplicationAndStartingDoesNot` asserts of webForCore (with `bin_dotnet`), on Linux and
Windows. A third site, in Single mode, is written into a folder IIS already serves, as a deployment copies a site, and
whether its first request restarts it is recorded only: Windows reports the copied folders as modified when the
application first opens them, and IIS 10 restarted for that in some runs and not in others. webForCore doesn't.

Then a request running when the application restarts (#181): `tools/ci/iis_drain.cs` writes a site with
`<httpRuntime shutdownTimeout="10">` and `<hostingEnvironment shutdownTimeout="5">`, IIS serves it from a classic and an
integrated application pool, and for an 8 s and a 30 s request to a page that flushes a line every second it writes a
file in `bin` 2 s in, asks for another page every half second while the request runs, and records how many lines
arrived, how the request ended and whether a new domain served meanwhile; in the integrated pipeline, how long a
WebSocket open across the change is served. The job fails if either request doesn't run to its end while a new domain
serves, as IIS.Gate.Tests' `ARequestRunningAcrossARestartRunsToItsEnd` asserts of webForCore (a 12 s request with
`shutdownTimeout="5"`); the WebSocket is recorded (IIS aborts it at once, as `RestartAbortsAWebSocket` asserts).

And LinqDataSource (#142): the same `test/webForCore.Requests.Sample` folder's `linq/` pages, with SQL Server Express
(`tools/ci/sqlserver-windows.ps1`) on the runner. IIS.Gate.Tests' `LinqDataSourceTests` ran `test/webForCore.Linq.Scenarios`
on Kestrel and wrote `linq-responses.json` (the `linq-responses` artifact); the job runs the same scenarios against IIS's
classic pipeline, postbacks with IIS's own view state included, and `iis_compare.cs diff` compares the forms against
`tools/ci/iis-known-differences-linq.json` ([IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md#linqdatasource)).

And OpenID Connect: `test/webForCore.Oidc.Sample.Iis` builds `test/webForCore.Oidc.Sample` for .NET Framework 4.8 with
Microsoft's Katana 4.2.3 packages (OpenIdConnect, Jwt, Cookies, the System.Web host), Web API 5.3.0 and
IdentityServer3.Contrib.AccessTokenValidation, as a customer application has them, and IIS serves it from an
integrated-pipeline application pool (Katana's System.Web host needs it on IIS). The job starts
`test/webForCore.Oidc.Provider` (`serve`) and runs the scenarios Oidc.Gate.Tests ran on Kestrel against IIS
(`scenarios`): sign-in, failures, sign-out and bearer tokens, each response normalised the same way (placeholders for
ports, nonces, codes, tokens and protected values). `iis_compare.cs diff` compares them with `oidc-responses.json`
(the `oidc-responses` artifact) against `tools/ci/iis-known-differences-oidc.json`
([IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md#openid-connect)).

And an application under a virtual path (#190, #194): `test/webForCore.VirtualPath.Sample.Iis` builds
`test/webForCore.VirtualPath.Sample` (Web Forms pages, MVC and Web API routes, bundles, forms authentication, session
state, Swashbuckle, and a module that sets `REMOTE_ADDR` and `HTTPS`) for .NET Framework 4.8 with Microsoft's packages,
and IIS serves it as the application `/app` of a site whose root is empty (`tools/ci/iis-site.ps1 -Application app`,
integrated pipeline). The job replays IIS.Gate.Tests' `VirtualPathTests` (`vpath-responses.json`, the
`vpath-responses` artifact) and compares the responses against `tools/ci/iis-known-differences-vpath.json`
([IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md#virtual-path)).

And third-party packages: `test/webForCore.Packages.Sample.Iis` builds `test/webForCore.Packages.Sample` for .NET Framework
4.8 with Microsoft's Web API 5.3.0 and Katana 4.2.3 packages and the same Ninject, WebActivatorEx, Swashbuckle.Core and
CacheCow.Server.WebApi packages, and IIS serves it from an integrated-pipeline application pool. The job sends it the
requests Packages.Gate.Tests sent on Kestrel, in the same order (`packages-responses.json`, the `packages-responses`
artifact: some depend on earlier ones, such as an `If-None-Match` with the ETag a `GET` returned), and compares the
responses against `tools/ci/iis-known-differences-packages.json`
([IIS-COMPATIBILITY.md](IIS-COMPATIBILITY.md#third-party-packages)).

### In a Windows container

CI's `windows-container` job runs webforcore-host and `test/webForCore.Mvc.Sample` in a Windows container: Microsoft's
ASP.NET Core runtime image for Nano Server, `mcr.microsoft.com/dotnet/aspnet:10.0-nanoserver-ltsc2025`, which has no
SDK, so the sample's views compile with webForCore's own compilers. On `windows-2025` the Docker engine runs Windows
containers with process isolation, so the image's Windows build must be the runner's (ltsc2025); the engine is an
automatic service that can still be starting when the job begins, and the job waits for it. Nano Server is enough:
Server Core isn't needed. The job builds the host and the sample, copies them into an image
(`tools/ci/windows-container/Dockerfile`), runs it with the container's port published, and
`tools/ci/windows_container.cs` probes it from the runner: Razor views (`/Home/About`, `/Products/Details/2`), JSON
(`/Products`) and a POST's redirect (`/Products/Create`, 302). Then it changes `Web.config` inside the container, and the
host must recycle the worker (`Recycling worker 1`, which then exits with 0), and a new worker, another process, must
serve the same responses. Last, `docker stop` must end the host with exit code 0 and its worker with 0. The checks are
in the job's summary. It is not a required check.

## The suites

### Microsoft's and Mono's suites

| Suite | Upstream | Tests | Passed | Skipped | What it exercises |
| --- | --- | ---: | ---: | ---: | --- |
| System.Web.MonoTests | Mono `mcs/class/System.Web/Test` | 2,634 | 2,623 | 11 | System.Web itself: HttpRequest/Response, caching, configuration, controls, forms authentication, hosting, HttpUtility. Corrected to Microsoft's 4.5+ behaviour where Mono's implementation differed |
| System.Web.Mvc.Test | AspNetWebStack | 5,505 | 5,505 | 0 | MVC 5, with LINQ to SQL's `Binary` (`LinqBinaryModelBinder`, the hidden-input helpers and editor template) |
| Microsoft.Web.Mvc.Test | AspNetWebStack | 457 | 457 | 0 | MVC Futures |
| System.Web.Razor.Test | AspNetWebStack | 1,460 | 1,460 | 0 | Razor 3 parser and code generator |
| System.Web.WebPages.Test | AspNetWebStack | 974 | 972 | 2 | Web Pages |
| System.Web.WebPages.Razor.Test | AspNetWebStack | 46 | 45 | 1 | Web Pages' Razor host |
| System.Web.WebPages.Deployment.Test | AspNetWebStack | 52 | 48 | 4 | Web Pages deployment and version checks |
| System.Web.Helpers.Test | AspNetWebStack | 418 | 418 | 0 | WebGrid, Chart, WebImage, Crypto, JSON (on drawingForCore's System.Drawing) |
| System.Web.Http.Test | AspNetWebStack | 3,157 | 3,157 | 0 | Web API 2 |
| System.Web.Http.WebHost.Test | AspNetWebStack | 343 | 343 | 0 | Web API on System.Web |
| System.Web.Http.Owin.Test | AspNetWebStack | 243 | 243 | 0 | Web API on OWIN |
| System.Web.Http.Cors.Test, System.Web.Cors.Test | AspNetWebStack | 196 | 196 | 0 | CORS |
| System.Web.Http.Tracing.Test | AspNetWebStack | 69 | 69 | 0 | Web API's `SystemDiagnosticsTraceWriter` and `EnableSystemDiagnosticsTracing()` |
| System.Net.Http.Formatting.Test | AspNetWebStack | 6,445 | 6,445 | 0 | Web API's formatters, multipart, HttpClient extensions |
| Microsoft.Owin.Tests | Katana | 321 | 321 | 0 | OWIN core |
| Microsoft.Owin.Host.SystemWeb.Tests | Katana | 36 | 36 | 0 | OWIN on System.Web |
| Microsoft.Owin.Security.Tests | Katana | 160 | 156 | 4 | Cookie, OAuth, OpenID Connect, JWT (and the JwtFormat functional test that needs no host), Google, Facebook, Microsoft Account and Twitter middleware; webForCore's own WS-Federation (a SAML 2.0 sign-in) and Active Directory bearer tests, which upstream has only as functional tests against IIS Express and Azure |
| Microsoft.AspNet.SignalR.Tests | SignalR 2.4.3 | 489 | 487 | 2 | SignalR server and client |
| Microsoft.AspNet.SignalR.StackExchangeRedis.Tests, .Redis.Tests | SignalR 2.4.3 | 10 | 10 | 0 | The Redis backplanes' message bus against a mock connection: connecting with retries, reopening its stream and restoring its cursor when the connection comes back |
| Microsoft.AspNet.SignalR.SqlServer.Tests | SignalR 2.4.3 | 16 | 16 | 0 | The SQL Server backplane without a server: its configuration, and its receive loop's use of query notifications or polling against a mock provider |
| Identity.Test | ASP.NET Identity 2 | 311 | 311 | 0 | Identity on Entity Framework 6 against SQL Server |
| System.Data.Linq.MonoTests | Mono `mcs/class/System.Data.Linq/Test` | 72 | 72 | 0 | LINQ to SQL without a server: `DataContext`'s constructors and arguments, `EntitySet`, `Table<T>`, `Binary`, and the SQL text of a few queries. Built with `MONO_STRICT`, as Mono built them for its own build of Microsoft's System.Data.Linq; one test corrected to .NET Framework's `ArgumentException` (#142) |
| System.Data.SqlClient.Tests | dotnet/maintenance-packages (`FunctionalTests`) | 143 | 143 | 0 | System.Data.SqlClient without a server: parameters, `SqlDataRecord`, metadata, bulk copy mappings, errors, statistics, credentials, async provider base classes. Only upstream's tests that need nothing but xunit: the rest need its TDS test server, RemoteExecutor or Arcade's xunit extensions, which aren't on nuget.org. With webForCore's `AssemblyTests` (4): `GetTypes()` works, and Microsoft's identity is kept (#41) |

Every skip is the upstream project's own (`Skip=` in its source, or a category Microsoft excludes on Linux). None was
added to hide a failure.

Settings some suites need:
- **SignalR** runs with `TieredCompilation=false`: two weak-reference tests rely on a local being dead after its last
  use, which .NET Framework's JIT gave and .NET's tier-0 code doesn't.
- **Identity** runs one test at a time (`parallelizeTestCollections: false`, as xunit 1.9 ran them; they share one
  database) and in Pacific time (one test assumes UTC−8).
- The suites run in **Release**, as Microsoft ships: in Debug, `Debug.Fail` calls in Microsoft's code fire in Mono's
  tests.

#### System.Web.MonoTests

Mono's System.Web tests (mono/mono `0f53e9e`, MIT) are the only large suite for System.Web itself. Of the 563 files in
Mono's `System.Web_test.dll.sources`, 188 are built (186 test files and two helpers). Left out: 103 that use Mono's
in-process web harness (`MonoTests.SystemWeb.Framework`), 11 that use its HTML-comparison harness and 45 of the
harness itself (`mainsoft/`), 209 Code Access Security tests (.NET doesn't enforce CAS), 4 that test Mono internals or
inspect an AppDomain's setup, and `RequestValidatorTest`, which needs the harness's fake worker request.

- They run on NUnit 3.14, the last version with NUnit 2's classic `Assert`, with `NUnit2Compat.cs` for
  `[ExpectedException]` (NUnit 2's exact-type semantics) and Mono's `[MonoTODO]`. `TARGET_DOTNET` and `DOT_NET` are
  defined, so where Mono wrote a .NET variant of a test, that variant runs.
- The assembly is named `DotNet4_NUnit_Tests`: the output-cache tests compare bytes against baselines recorded on .NET
  Framework from an assembly of that name, which the serialized bytes contain.
- Where a Mono test disagrees with System.Web, Microsoft's reference source (microsoft/referencesource `ec9fa9a`)
  decides: where the port's code is the reference source's, the test is corrected to .NET Framework 4.5+'s behaviour
  (Mono's tests follow Mono); where the port differs, the port is fixed.

#### Accepted platform differences

Tests corrected to .NET's behaviour, because the difference comes from .NET's base class library or the platform and
not from Microsoft's ASP.NET code. Those an application can see are marked \*; the others only change what a test
asserts.

| Difference | Suites |
| --- | --- |
| \* `ArgumentException.Message` is `msg (Parameter 'name')` (.NET Framework: `msg`, a newline, `Parameter name: name`); it shows in model-state errors built from exceptions | Web API, MVC, Web Pages, Identity |
| \* `ContentDispositionHeaderValue.FileName` is unquoted (.NET Framework kept the quotes). Web API's own multipart providers unquote it themselves, so they behave the same | Formatting |
| \* `MediaTypeHeaderValue(null)` throws `ArgumentNullException` (.NET Framework: `ArgumentException`, its base class) | Web API |
| \* Invalid UTF-8 is replaced per Unicode's "maximal subpart" rule (more U+FFFD than .NET Framework) | Formatting |
| \* Indented JSON and XML, Razor's generated code and select lists use the platform newline | Formatting, Razor, MVC |
| \* ICU cultures: any well-formed culture name is created (`<PredefinedCulturesOnly>true</PredefinedCulturesOnly>` restores .NET Framework's `CultureNotFoundException`), and culture data differs from Windows NLS (es-MX's time format, ar-DZ's decimal separator, U+202F before AM in en-US) | Web Pages, MVC |
| \* `HostString` decodes a host that mixes Unicode and punycode labels (.NET Framework threw) | Katana |
| \* A whitespace-only directory name is valid on Linux | Formatting (the multipart root path is rejected anyway, as on .NET Framework) |
| The core library is `System.Private.CoreLib`, not `mscorlib`, in assembly-qualified names | Web API, MVC |
| Reworded BCL messages and stack frames (`List<T>.InsertRange`, `KeyNotFoundException`, `Boolean.Parse`, Activator, async state machines) | Web API, MVC, WebHost |
| `HttpContent.ReadAsStreamAsync` returns a different stream over its buffer | Formatting |
| `Thread.Abort` isn't supported: tests throw a `ThreadAbortException` instance, as `Response.End` does | MVC |
| No AppDomains: tests that need one run in a collectible `AssemblyLoadContext`, or are skipped where that can't stand in | MVC, Web Pages, Mono |
| No DPAPI or LocalDB off Windows: Katana's security tests use a test data protector, and Identity uses SQL Server in a container | Katana, Identity |
| Things .NET doesn't have (EF 1's `EntityObject`, legacy CAS, a JScript CodeDom provider, side-by-side Web Pages versions from the GAC, `WindowsIdentity`): compiled out or skipped | MVC, Web Pages, MVC Futures |

### webForCore's own

| Suite | Tests | What it checks |
| --- | ---: | --- |
| IIS.Gate.Tests | 387 | `test/webForCore.IIS.Sample` under `webforcore-host`, compiled from an empty Temporary ASP.NET Files each run: `RegressionTests` (48) for problems real applications hit (`regressions/`, one test per issue, each naming the application); each `<system.webServer>` feature against IIS 10's documented behaviour (request filtering, rewrite rules, redirects, default documents, errors, IP restrictions, directory browsing, compression, logging), the connection details the host forwards (and that clients can't forge them), WebSockets, HTTPS and client certificates, timeouts, SignalR (and the hub proxy's version header, #118), bundling, protected configuration, integrated-pipeline APIs (and the headers ASP.NET adds only around a handler), the compiler errors CodeDom reads from csc and vbc, the CodeDom options (`GenerateInMemory`, `IncludeDebugInformation`, `CompilerOptions`, `OutputAssembly`, `KeepFiles`) against what .NET Framework 4.8's `CSharpCodeProvider` gave (#135), precompiled sites (`aspnetcore_compiler`: fixed, updatable, compile errors, usage), ASMX (SOAP 1.1/1.2, faults, HTTP POST, WSDL, help page, JSON), ASP.NET AJAX (ScriptResource.axd, UpdatePanel partial postback, page methods), `System.Web.Mail`, `<system.net>` (mail settings through a PasswordRecovery postback to a fake SMTP server, the default proxy through a fake proxy), `<DbProviderFactories>`, data binding over app types, a Visual Studio typed DataSet with TableAdapters against SQL Server, the SQL providers' database (`SqlServicesTests`, #123: `webforcore-regsql` creates membership, roles and session state, machine.config's `SqlMembershipProvider` and `SqlRoleProvider` use it, `SqlServices` adds profile, and `-sqlexportonly` writes aspnet_regsql's output byte for byte), and restarts (`RecyclingTests`): `UnloadAppDomain` starts the application in a new worker process, every request during a restart under load is answered, also with 16 clients across 5 restarts (#209), and with form POSTs and PUTs whose bodies reach the next worker (#214), Web.config changing while the next worker starts ends in a serving worker (#49), a worker that exits is replaced, `bin` and `App_Code` changes restart the application (#51), a request running when the application restarts runs to its end in the old worker while the new one serves (#181, checked against IIS 10 by `iis-compare`), and a WebSocket open across a restart is aborted at once, as on IIS. `RequestHandlingTests` (92) run `test/webForCore.Requests.Sample`, a site IIS serves as it is, and assert IIS 10's answers for escaped and invalid characters in paths, path info, default documents and `RawUrl`, files asked for in another case, `Response.End`, `Redirect`, `Server.Transfer` and `CompleteRequest` before `EndRequest`, request headers on paths with a `(`, unbuffered `Response.Write`, and the `Content-Length` a page appends to a 304 (#126, #127, #129, #140, #170, #184); `ErrorsAndLimitsTests` (86, #192) the same sample's `<customErrors>` (On, RemoteOnly, Off, `defaultRedirect`, `<error statusCode>`, `ResponseRewrite`), `<httpErrors existingResponse>` with and without `TrySkipIisCustomErrors`, `<httpErrors>` for request filtering's 404.8 (a hidden segment, as Subtext has it), each request filtering rejection with IIS's error page and with an httpErrors page and the client's connection closed after it, as IIS 10 closes it (#219), and `<httpRuntime maxRequestLength>` and `requestLengthDiskThreshold` against `maxAllowedContentLength` (forms, multipart uploads and raw bodies at the limits and one byte over, and a 31,000,000-byte upload; 300 requests over `maxRequestLength` and `maxAllowedContentLength` each get their answer); each response is written to `requests-responses.json` for CI's `iis-compare` job. `ScaleoutTests` (13) run `test/webForCore.Scaleout.Sample` twice, each instance under its own `webforcore-host`, as a web farm on a SignalR backplane: StackExchange.Redis 2, StackExchange.Redis.StrongName 1.x (both on Redis at `127.0.0.1:16379`) and SQL Server (a database of their own). A broadcast from a hub on one instance reaches the clients of both, groups have members on both, a client whose instance restarts (its process killed and started again) reconnects with its connection and groups, and a long-polling client whose requests alternate between the instances (no sticky sessions) gets every message; without a backplane a broadcast stays on its instance. `LinqDataSourceTests` (#142) drive the same site's `linq/` pages through `test/webForCore.Linq.Scenarios` against SQL Server: a GridView and DetailsView on a `LinqDataSource` (Where, OrderBy, paging and sorting by the data source, `AutoGenerateWhereClause`, a `Select` projection, the `Selecting`, `Updating`, `Updated`, `Inserted` and `Deleted` events), postbacks that page, sort, edit, update, delete and insert, an update of a row whose `rowversion` changed in between (`ChangeConflictException`), and Dynamic Data's LINQ to SQL model provider; the responses go to `linq-responses.json`, which `iis-compare` compares with IIS 10. `MachineKeyInteropTests` (31, #191) run `test/webForCore.MachineKey.Sample` once for each `<machineKey>` configuration and read .NET Framework 4.8's forms tickets, `MachineKey.Protect` and `Encode` output and Katana and ASP.NET Identity cookies (`FrameworkData/machinekey-framework.json`); their own outputs go to `machinekey-webforcore/`, which `machinekey-framework-interop` has .NET Framework read. `VirtualPathTests` (14) run `test/webForCore.VirtualPath.Sample` as the application `/app` (#190): the paths and URLs System.Web, MVC, Web API, bundling, forms authentication and Swashbuckle make, the application in another case and without its trailing slash, URLs outside it, and a module that sets `REMOTE_ADDR` and `HTTPS` (#194); each response is written to `vpath-responses.json` for `iis-compare`. `ForwardedHeadersTests` (6) run it behind a proxy (#194): the host's `WebForCore:Host:ForwardedHeaders` from a trusted proxy and not from another peer, ASP.NET Core's `ASPNETCORE_FORWARDEDHEADERS_ENABLED` applied once, by the host, and the application's own `UseForwardedHeaders`; and Application Initialization's pages under the virtual path (#193) |
| WebApi.Gate.Tests | 70 | `test/webForCore.WebApi.Sample` (Visual Studio's Web API template and the "Getting Started" tutorial's controllers). Each request runs through System.Web and through OWIN, and is compared with an oracle: the same controllers in Web API's in-memory server, without System.Web. Status, media type, body, Location and Allow must match. Every request and response is written to `gate-responses.json` beside the test assembly, and CI sends the same requests to the sample on IIS 10 ([Compared with IIS 10](#compared-with-iis-10), #78). Also: a restart, which runs the application in a new worker process, and Web API's tracing (`TracingTests`): `config.EnableSystemDiagnosticsTracing()` writes a request's trace records, from its begin record to its end record, through `System.Diagnostics.Trace` to a listener added in `Application_Start`, through System.Web and through OWIN; Web.config's `<system.diagnostics>` listener isn't created (on IIS 10 it is, which `iis-compare` records as a known difference) |
| Mvc.Gate.Tests | 71 | `test/webForCore.Mvc.Sample` and `test/webForCore.WebPages.Sample`: model binding, validation, post-redirect-get with TempData, in-process session state keeping a TempData object that isn't serializable (#180), one session cookie (`<sessionState cookieName>`), session state through `UseAspNetCoreSessionProvider()` in place of `StateServer` (Web Pages sample) and its need for serializable values, `Html.Serialize`/`[Deserialize]` state (and tampering), anti-forgery tokens, filters, error pages (`HandleErrorAttribute` with customErrors on and off, #192), layouts, display and editor templates, Razor features, MVC Futures, Web Pages, that source files aren't served, and that a restart runs the application in a new worker process, and a LINQ to SQL `rowversion` through an edit form against SQL Server (`LinqBinaryTests`: written as base64 by the hidden-input editor template and `Html.Hidden`, bound back to a `Binary`, saved, and a stale one rejected with `ChangeConflictException`) |
| Oidc.Gate.Tests | 39 | `test/webForCore.Oidc.Sample`, the Visual Studio "Work or School Accounts" (Entra ID) template's OWIN startup as a customer application configures it: cookies and `UseOpenIdConnectAuthentication` with `code id_token` and form_post, the code redeemed by Katana or by `AuthorizationCodeReceived`, `SaveTokens`, the notifications changing the outcome (a domain hint, an `id_token_hint` read from the saved tokens, claims added, `HandleResponse` and `SkipToNextMiddleware` on failure), its own `TicketDataFormat` and `StateDataFormat`, a cookie in Katana's default ticket format (the machine key), and a Web API behind `Microsoft.Owin.Security.Jwt` and IdentityServer3.Contrib.AccessTokenValidation 4.1.16 (the net45 package, with introspection). `test/webForCore.Oidc.Provider`, a minimal OpenID Connect provider (discovery, JWKS, authorize, token, introspection, end session) in the test process, signs in, out and wrongly: the challenge's parameters and nonce cookie, the claims in `User`, the saved tokens, end_session, a tampered, expired, wrongly issued or addressed id_token, a wrong or missing nonce, an unknown key and a provider error, each with the exception type `AuthenticationFailed` sees, and bearer tokens' 200, 401 and 403. Behind a proxy that ends TLS (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`), the nonce cookie is `Secure`; one IdentityModel version (8.x) in the application; the cookie middleware's `Expires: -1` kept, as on IIS. Every response is written to `oidc-responses.json` for CI's `iis-compare` job |
| Packages.Gate.Tests | 6 | `test/webForCore.Packages.Sample`, with third-party net4x packages a customer application keeps as they are (the original packages, unchanged): Ninject.Web.Common.WebHost started by WebActivatorEx 2.2.0 from the package's `App_Start/NinjectWebCommon.cs` (its `OnePerRequestHttpModule` and `NinjectHttpModule` registered, `InRequestScope` keyed on `HttpContext.Current`: one object per request, disposed at its end), Ninject.Web.Common.OwinHost's `UseNinjectMiddleware` (an OWIN middleware resolving in its request scope), Ninject.Web.WebApi.OwinHost's `UseNinjectWebApi` (controllers constructed by Ninject), Swashbuckle.Core 5.6.0 (the Swagger document, and the UI's page and every file it loads, embedded in the assembly) and CacheCow.Server.WebApi 2.5.0 (an ETag on `GET`, 304 for `If-None-Match`, a new ETag after `PUT` and `POST`); and a restart, in which WebActivatorEx runs `NinjectWebCommon.Stop` in the old worker and `Start` in the new one. The requests are sent in a fixed order and each response is written to `packages-responses.json` for CI's `iis-compare` job |
| Host.Tests | 31 | `webforcore-host` itself (`test/webForCore.Host.Sample`): rapid-fail protection stops starting workers that keep crashing, workers stop when the host ends, an application run without the host stops when it restarts, and an application warns at start when the process has no locale (`LC_ALL=C`) and not when it has one (#97). File change notifications (`FileChangeNotificationTests`, on copies of the sample with each `<httpRuntime fcnMode>`): cache dependencies on files in 200 folders hold no more than 16 inotify instances on Linux and see a change, a Web.config change and renaming a monitored folder restart the application while deleting a folder with nothing monitored doesn't, and `Disabled` monitors nothing (#145, #146, #152); a freshly copied site doesn't restart when it starts, and a change in `bin_dotnet` restarts it as the same change to `bin` does on IIS 10 in each mode, on Linux and Windows (#167). `AssemblyBindingTests`: a `<codeBase>` `file:` URL loads a strong-named assembly from outside the site, and not one without a strong name (#158) |
| SessionCache.Gate.Tests | 28 | Two instances of `test/webForCore.SessionCache.Sample` under `webforcore-host`, each from its own copy of the site (two servers), configured in `web.config` as on IIS (`configs/*.config`), sharing session state through `Microsoft.Web.RedisSessionStateProvider` 5.0.4 with the async module, `<sessionState mode="SQLServer">` in a database `webforcore-regsql -ssadd -sstype c` makes, and `SqlSessionStateProviderAsync` 2.1.0-preview1; for each: the application's ID is the same on both, a value set on one is read on the other (a string and a `[Serializable]` object changed on the other instance), a request waits for the session's lock held on the other instance (read-only ones too) while read-only requests don't wait for each other, `Session.Abandon` ends the session on both, a session ends a minute after its last request with `Session.Timeout = 1`, and a value that isn't serializable fails the request with "Unable to serialize the session state" (#180). `Microsoft.Web.RedisOutputCacheProvider` 4.0.1: a page (`VaryByParam="none"`) rendered on one instance is served from Redis by the other until its `Duration` ends, a page with `VaryByParam` is never found again (4.0.1's `Add` fails on a cast, as on IIS), and nothing is cached with the `OutputCacheModuleAsync` swap its dependency adds (as on IIS). The providers' assemblies load (Cosmos DB's too). Redis at 127.0.0.1:16379 (`tools/ci/redis.sh`, or `WEBFORCORE_REDIS`), SQL Server at 127.0.0.1,14333 |
| System.Data.Linq.Tests | 590 | LINQ to SQL (#142). `ErrorTests` (560): each of the 553 members of the generated `Strings` and `Error` classes against `FrameworkData/System.Data.Linq.json`, the data read from .NET Framework 4.8's `System.Data.Linq.dll` (the exception's type, constructor and message, and the string), without the generator; the resources; long arguments cut as .NET Framework cuts them; errors through the public API. `SqlServerTests` (24) against SQL Server (127.0.0.1,14333, or `WEBFORCORE_LINQ_SQLSERVER`), in a database `DataContext.CreateDatabase` creates: inserts with identity and `rowversion` values, LIKE, joins, grouping, paging, deferred and eager loading, updates, a concurrency conflict, deletes, `CompiledQuery`, aggregates, `SqlMethods`, `Binary`, `ExecuteQuery`/`ExecuteCommand`, a stored procedure and a composable function, a `TransactionScope` rollback, `Attach`, `XmlMappingSource`, inheritance, an untranslatable method's `NotSupportedException`, `DBConvert`. `AssemblyTests` (6): .NET Framework's identity, references to 3.5.0.0 and 4.0.0.0 bind to it. The test assembly is `DLinq.Unittests` with Microsoft's public key, to which System.Data.Linq's own `InternalsVisibleTo` gives its internals |
| Serialization.Tests | 171 | `NameValueCollection` and its subclasses through the BinaryFormatter port, in .NET Framework's format: round trips and the wire layout (member names and order, `Version=4.0.0.0` assembly names). `ReflectionSerializationTest` (160, #156): `Type`, `MethodInfo`, `ConstructorInfo`, `FieldInfo`, `PropertyInfo`, `EventInfo`, `DBNull`, `Missing`, `Assembly` and `Module` (`UnitySerializationHolder`, `MemberInfoSerializationHolder`): the issue's repro, round trips of 50 graphs, and 51 streams .NET Framework 4.8 wrote (`FrameworkStreams/*.bin`, from `FrameworkStreamCases.cs`), each of which the port reads as that graph and, but for `System.Empty`, writes byte for byte. The port also read and re-wrote 458 of the 589 .NET Framework streams in dotnet/runtime's `BinaryFormatterTestData.cs` (release/8.0), as many as before the surrogates; no `NameValueCollection` stream written by .NET Framework itself has been checked (the layout follows the reference source member for member) |

## Code coverage

Measured over all 24 suites with `dotnet-coverage` (`tools/ci/coverage.sh`, round 13), including the sample apps the
gate suites start in their own processes. System.Drawing.Common, System.Drawing and their suite have moved to
drawingForCore since, so they are left out of the figures below. Line coverage of the production assemblies (tests,
samples and NuGet dependencies excluded):

| | Coverable lines | Covered | Line coverage |
| --- | ---: | ---: | ---: |
| Microsoft's code from the submodules (Web Stack, Katana, SignalR, Identity) | 64,168 | 50,659 | **78.9%** |
| WebFormsForCore's port (System.Web and companions) and webForCore's tools | 301,396 | 59,361 | **19.7%** |
| **All** | **365,564** | **110,020** | **30.1%** |

The upstream libraries come with Microsoft's own suites, so their coverage is close to what Microsoft had: MVC 91%,
Web API 91%, Razor 90%, Formatting 90%. The port is the weak spot:
- **System.Web (27.8% of 146,216 lines)** is covered by Mono's tests and the gates. The hosting layer webForCore
  added is well covered (the IIS layer 84%, the worker request 69%), but large parts of the Web Forms control set
  and the configuration sections are only exercised indirectly.
- **System.Web.Services (ASMX) had no coverage at all**, and **System.Web.Extensions** (AJAX, ScriptManager,
  UpdatePanel) had 3.1%. Since these figures, the IIS gate has suites for both (`WebServiceTests`, `AjaxTests`), but
  they cover the main paths only; both remain thin.
- System.Web.DataVisualization (8.9%) and WebGrease (7.5%) are large and only touched through the Chart helper and
  bundling.

Per assembly:

| Assembly | Source | Coverable lines | Covered | Line coverage |
| --- | --- | ---: | ---: | ---: |
| Microsoft.AspNet.Identity.Core | upstream | 1,302 | 1,258 | 96.6% |
| Microsoft.AspNet.Identity.EntityFramework | upstream | 517 | 442 | 85.5% |
| Microsoft.AspNet.Identity.Owin | upstream | 424 | 225 | 53.1% |
| Microsoft.AspNet.SignalR.Client | upstream | 3,105 | 1,773 | 57.1% |
| Microsoft.AspNet.SignalR.Core | upstream | 6,172 | 4,624 | 74.9% |
| Microsoft.AspNet.SignalR.SystemWeb | upstream | 13 | 0 | 0.0% |
| Microsoft.Owin | upstream | 1,759 | 1,219 | 69.3% |
| Microsoft.Owin.Cors | upstream | 74 | 0 | 0.0% |
| Microsoft.Owin.Host.HttpListener | upstream | 1,812 | 492 | 27.2% |
| Microsoft.Owin.Host.SystemWeb | upstream | 2,847 | 1,578 | 55.4% |
| Microsoft.Owin.Hosting | upstream | 853 | 466 | 54.6% |
| Microsoft.Owin.Security | upstream | 689 | 436 | 63.3% |
| Microsoft.Owin.Security.Cookies | upstream | 328 | 253 | 77.1% |
| Microsoft.Owin.Security.OAuth | upstream | 871 | 790 | 90.7% |
| Microsoft.Web.Mvc | upstream | 2,964 | 1,654 | 55.8% |
| System.Net.Http.Formatting | upstream | 4,141 | 3,724 | 89.9% |
| System.Web.Cors | upstream | 216 | 216 | 100.0% |
| System.Web.Helpers | upstream | 2,146 | 1,696 | 79.0% |
| System.Web.Http | upstream | 9,552 | 8,657 | 90.6% |
| System.Web.Http.Cors | upstream | 372 | 362 | 97.3% |
| System.Web.Http.Owin | upstream | 755 | 548 | 72.6% |
| System.Web.Http.WebHost | upstream | 1,172 | 877 | 74.8% |
| System.Web.Mvc | upstream | 11,118 | 10,133 | 91.1% |
| System.Web.Razor | upstream | 6,368 | 5,704 | 89.6% |
| System.Web.WebPages | upstream | 3,921 | 3,020 | 77.0% |
| System.Web.WebPages.Deployment | upstream | 320 | 200 | 62.5% |
| System.Web.WebPages.Razor | upstream | 357 | 312 | 87.4% |
| aspnetcore_compiler | port | 476 | 168 | 35.3% |
| WebFormsForCore.Compilers | port | 51 | 40 | 78.4% |
| System.Configuration.ConfigurationManager | port | 14,125 | 5,530 | 39.2% |
| webforcore-regiis | port | 54 | 44 | 81.5% |
| WebFormsForCore.Serialization.Formatters | port | 6,111 | 2,944 | 48.2% |
| System.Web | port | 146,216 | 40,667 | 27.8% |
| System.Web.ApplicationServices | port | 389 | 90 | 23.1% |
| System.Web.DataVisualization | port | 51,639 | 4,603 | 8.9% |
| System.Web.Extensions | port | 16,527 | 511 | 3.1% |
| Microsoft.Web.Infrastructure | port | 551 | 63 | 11.4% |
| System.Web.Optimization | port | 1,255 | 634 | 50.5% |
| System.Web.RegularExpressions | port | 3,407 | 946 | 27.8% |
| System.Web.Services | port | 18,737 | 0 | 0.0% |
| WebGrease | port | 41,858 | 3,121 | 7.5% |
| **Total** | | **365,564** | **110,020** | **30.1%** |

Branch coverage isn't reported: dotnet-coverage's Cobertura output has no branch data. To measure again:
`dotnet tool install -g dotnet-coverage`, build in Release, then `tools/ci/coverage.sh` (about 10 minutes).

## How results are recorded

- The fork commits in `external/` (`git log` in a submodule): why each test was corrected or skipped, one change per
  commit. A difference kept on purpose is in the tables above.
- `tools/ci/trx_summary.cs` and `trx_failures.cs` summarise `.trx` files (C# file-based apps:
  `dotnet run tools/ci/trx_summary.cs -- <files>`).
- `tools/ci/check_code_rules.cs` checks webForCore's own code against CONTRIBUTING.md rules 6 and 7 (non-public
  reflection, unsafe code, P/Invoke, GC steering) and fails on any line that isn't one of #57's known exceptions, or
  on an exception that has been fixed but is still listed. CI runs it on Linux before the build:
  `dotnet run tools/ci/check_code_rules.cs`.
