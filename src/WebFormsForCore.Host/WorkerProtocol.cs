namespace WebFormsForCore.Host;

// What the host and its workers agree on. The worker's side is System.Web's WebFormsForCore.Hosting.WorkerProcess
// (src/WebFormsForCore.Web/WebFormsForCore/AspNetCoreHosting/WorkerProcess.cs); keep the two in step.
static class WorkerProtocol
{
	// Environment variables the host starts a worker with.
	public const string SocketVariable = "WEBFORCORE_WORKER_SOCKET";   // the Unix socket the worker serves on
	public const string IdVariable = "WEBFORCORE_WORKER_ID";           // the worker's number
	public const string ControlVariable = "WEBFORCORE_HOST_CONTROL";   // the host's control socket
	public const string TokenVariable = "WEBFORCORE_HOST_TOKEN";       // proves a control message comes from a worker
	// ASP.NET Core's: System.Web's WorkerHostingStartup sets the worker up before the application's Program.cs runs.
	public const string HostingStartupVariable = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";

	// Request headers with what the worker can't see itself; the host removes any a client sent.
	public const string RemoteHeader = "X-WebForCore-Remote";          // client address and port: "[address]:port"
	public const string LocalHeader = "X-WebForCore-Local";            // the binding's local address and port
	public const string SchemeHeader = "X-WebForCore-Scheme";          // http or https
	public const string ClientCertificateHeader = "X-WebForCore-Client-Certificate"; // DER, base64
	public const string TlsHeader = "X-WebForCore-Tls";                // "<SslProtocols>;<cipher strength>"
	// #214: the host added "Expect: 100-continue" (RequestBody); the worker hides it from the application, which the
	// client didn't send it to.
	public const string ExpectHeader = "X-WebForCore-Expect";
	public static readonly string[] RequestHeaders = [RemoteHeader, LocalHeader, SchemeHeader, ClientCertificateHeader, TlsHeader, ExpectHeader];

	// Response header of a worker that is shutting down and didn't process the request (with 503): the host can send
	// the request to the next worker (RequestBody).
	public const string RestartingHeader = "X-WebForCore-Restarting";
	// #219: response header of a worker that asks for the client's connection to be closed after the response, as IIS
	// closes it; the host sends Connection: close in its place.
	public const string CloseHeader = "X-WebForCore-Close";

	// Control messages, one line each, worker to host: "<token> ready <id> <virtual path>" once the worker serves (the
	// initialization pages are under the application's path, #190), and
	// "<token> recycle <id> <drain seconds> <reason>" when the application restarts: the worker finishes its requests,
	// for up to the drain time (#181), and exits. The host answers "ok" or "error".
	public const string Ready = "ready";
	public const string Recycle = "recycle";
	// "<token> overlap <id> <reason>": an application pool recycle (Recycling(...)), overlapped as IIS overlaps it (#193):
	// the worker goes on serving while the host starts and warms the next one; then the host sends it RecycleCommand on
	// its stdin, and it recycles (its "recycle" message then finds it already retired).
	public const string Overlap = "overlap";
	// Host to worker, one line on the worker's stdin: recycle now. Closing stdin stops the worker.
	public const string RecycleCommand = "recycle";
}
