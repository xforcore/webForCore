using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WebFormsForCore.Host;

// Starting until it says it serves; Warming while it is sent the initialization pages (#193); Ready once it takes traffic.
enum WorkerState { Starting, Warming, Ready, Retiring }

// One worker process: "dotnet <application>" serving on its own Unix socket, with its stdin a pipe from the host (when
// that closes, the worker stops: it never outlives the host).
sealed class Worker : IDisposable
{
	public int Id { get; }
	public string SocketPath { get; }
	public WorkerState State { get; set; } = WorkerState.Starting;
	// The application's virtual path without its trailing slash ("" at the root), from its ready message.
	public string ApplicationPath { get; set; } = "";
	public DateTime Started { get; } = DateTime.UtcNow;
	public Process? Process { get; private set; }
	public HttpMessageInvoker Invoker { get; }
	int requests;
	long lastActivity = DateTime.UtcNow.Ticks;

	// The initialization requests it is sent before it takes traffic (#193).
	public IReadOnlyList<(string Page, string? HostName)> WarmUpPages { get; init; } = [];

	public Worker(int id, string socketPath)
	{
		Id = id;
		SocketPath = socketPath;
		Invoker = new HttpMessageInvoker(new SocketsHttpHandler
		{
			// The request as the client sent it: no proxy, decompression, cookies, redirects or trace headers added.
			UseProxy = false,
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.None,
			UseCookies = false,
			ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
			ConnectTimeout = TimeSpan.FromSeconds(15),
			// #214: a large body goes to the worker only once its application reads it (RequestBody sends
			// "Expect: 100-continue"), however long that takes, so that a request the worker turns away while it shuts
			// down can go to the next worker with its body. A final response that comes first (the application didn't
			// read the body) ends the wait; from 300 on, a large body isn't sent and the connection isn't reused.
			Expect100ContinueTimeout = Timeout.InfiniteTimeSpan,
			ConnectCallback = async (_, cancellationToken) =>
			{
				var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				try
				{
					await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
					return new NetworkStream(socket, ownsSocket: true);
				}
				catch
				{
					socket.Dispose();
					throw;
				}
			},
		}, disposeHandler: true);
	}

	public int ProcessId { get; private set; }
	// Completes when the process exits (taken when it starts: the Process object is disposed with the worker).
	public Task Exited { get; private set; } = Task.CompletedTask;
	public int ExitCode { get; private set; }

	public void Start(WorkerCommand command, string controlSocket, string token)
	{
		var start = new ProcessStartInfo(DotnetPath())
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
		};
		start.ArgumentList.Add(command.Application);
		foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
		start.Environment[WorkerProtocol.SocketVariable] = SocketPath;
		start.Environment[WorkerProtocol.IdVariable] = Id.ToString(CultureInfo.InvariantCulture);
		start.Environment[WorkerProtocol.ControlVariable] = controlSocket;
		start.Environment[WorkerProtocol.TokenVariable] = token;
		// System.Web's hosting startup makes the application a worker (serving on the socket, with the host's connection
		// details) before its Program.cs runs, whatever that contains.
		var startups = Environment.GetEnvironmentVariable(WorkerProtocol.HostingStartupVariable);
		start.Environment[WorkerProtocol.HostingStartupVariable] = string.IsNullOrEmpty(startups) ? "System.Web" : startups + ";System.Web";
		// ASP.NET Core's ASPNETCORE_FORWARDEDHEADERS_ENABLED (or ForwardedHeaders_Enabled in appsettings.json) is the host's:
		// it has applied X-Forwarded-For and -Proto, trusting any peer, and the worker gets the result. Applied again in
		// the worker, whose peer is always the host, it took the entry a client had put before the proxy's own, so a client
		// could set its address past the proxy (#194). The application's own UseForwardedHeaders(...) still applies.
		start.Environment["FORWARDEDHEADERS_ENABLED"] = "false";
		var process = Process.Start(start) ?? throw new InvalidOperationException("The worker process didn't start.");
		Process = process;
		ProcessId = process.Id;
		Exited = WaitAsync(process);
	}

	async Task WaitAsync(Process process)
	{
		await process.WaitForExitAsync();
		ExitCode = process.ExitCode;
	}

	// The dotnet that runs the host runs the workers.
	static string DotnetPath()
	{
		var path = Environment.ProcessPath;
		if (path != null && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return path;
		var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		var dotnet = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		return !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, dotnet)) ? Path.Combine(root, dotnet) : dotnet;
	}

	public void BeginRequest() => Interlocked.Increment(ref requests);
	public void EndRequest()
	{
		Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
		Interlocked.Decrement(ref requests);
	}
	// When its last request ended, or it started serving: for the idle timeout (#193).
	public DateTime LastActivity => new DateTime(Interlocked.Read(ref lastActivity), DateTimeKind.Utc);
	public void Touch() => Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
	public int Requests => Volatile.Read(ref requests);

	// A command to the worker, one line on its stdin (WorkerProtocol); false if it is gone.
	public bool SendCommand(string command)
	{
		try
		{
			if (Process == null) return false;
			Process.StandardInput.Write(command + "\n");
			Process.StandardInput.Flush();
			return true;
		}
		catch (Exception e) when (e is IOException || e is InvalidOperationException || e is ObjectDisposedException)
		{
			return false;
		}
	}

	// Asks the worker to stop: its stdin closes, and it shuts down as on Ctrl+C.
	public void RequestStop()
	{
		try { Process?.StandardInput.Close(); }
		catch (Exception e) when (e is IOException || e is InvalidOperationException) { }
	}

	// The host killed it (a worker that kept running past its limit).
	public bool Killed { get; private set; }

	public void Kill()
	{
		Killed = true;
		try { Process?.Kill(entireProcessTree: true); }
		catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { }
	}

	public void Dispose()
	{
		Invoker.Dispose();
		Process?.Dispose();
		try { File.Delete(SocketPath); }
		catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
	}
}
