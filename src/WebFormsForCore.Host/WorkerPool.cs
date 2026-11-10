using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace WebFormsForCore.Host;

// The workers, as IIS's Windows Process Activation Service kept an application pool's: one current worker serves the
// requests. When its application restarts (a Web.config, bin or App_Code change, HttpRuntime.UnloadAppDomain, a
// recycling limit), the worker tells the host and stops taking requests; the host starts the next worker, and requests
// wait for it while the old worker finishes the ones it has and exits. A worker that exits otherwise is replaced, up to
// rapid-fail protection's limit.
//
// #193: the first worker starts with the host (StartMode AlwaysRunning) or at the first request (OnDemand). A new worker
// is sent <applicationInitialization>'s pages before it takes traffic (Warming); requests wait for it meanwhile, or get
// remapManagedRequestsTo's file. An application pool recycle (Recycling(...)) is overlapped: the old worker serves until
// the new one is warm (`serving`), then recycles. A worker idle for IdleTimeout is stopped.
sealed class WorkerPool : IHostedLifecycleService, IDisposable
{
	readonly WorkerCommand command;
	readonly HostSettings settings;
	readonly IHttpForwarder forwarder;
	readonly ILogger logger;
	readonly ForwarderRequestConfig requestConfig;
	readonly ApplicationInitialization initialization;
	readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
	readonly object sync = new();
	readonly List<Worker> retiring = new();
	readonly Queue<DateTime> failures = new();
	DirectoryInfo? runDirectory;
	ControlChannel? control;
	// The newest worker: it serves once Ready.
	Worker? current;
	// During an overlapped recycle, the worker that serves until `current` is ready (#193).
	Worker? serving;
	// Completes with the current worker once it is ready, or null when the worker it was for won't serve.
	TaskCompletionSource<Worker?> currentReady = NewReady();
	int lastId;
	bool shuttingDown;
	bool stopped; // rapid-fail protection stopped the pool

	public WorkerPool(WorkerCommand command, HostSettings settings, IHttpForwarder forwarder, ILogger<WorkerPool> logger)
	{
		this.command = command;
		this.settings = settings;
		this.forwarder = forwarder;
		this.logger = logger;
		requestConfig = new ForwarderRequestConfig
		{
			ActivityTimeout = settings.RequestIdleTimeout,
			Version = System.Net.HttpVersion.Version11,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
		};
		// The site is the folder above the application's output folder (bin_dotnet), as the worker's PhysicalPath is.
		var site = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(command.Application))) ?? ".";
		initialization = ApplicationInitialization.Read(site, settings, logger);
	}

	static TaskCompletionSource<Worker?> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	// A request a shutting-down worker didn't take is sent again after this delay, up to MaxRetries times (then the
	// worker's 503 goes to the client).
	static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
	const int MaxRetries = 25;

	// A private directory for the sockets (on Unix, only this user can use it).
	public Task StartAsync(CancellationToken cancellationToken)
	{
		runDirectory = Directory.CreateTempSubdirectory("webforcore-");
		control = new ControlChannel(Path.Combine(runDirectory.FullName, "host.sock"), token, OnControlMessage, logger);
		control.Start();
		if (initialization.Pages.Count > 0)
			logger.LogInformation("Application initialization: {Pages}{AfterRestart}.", string.Join(", ", initialization.Pages.Select(p => p.HostName + p.Page)),
				initialization.DoAppInitAfterRestart ? ", also after the application restarts" : "");
		if (settings.IdleTimeout > TimeSpan.Zero && settings.IdleTimeoutAction == IdleTimeoutAction.Suspend)
			logger.LogInformation("IdleTimeoutAction Suspend: idle workers keep running (IIS kept a suspended worker's application).");
		// AlwaysRunning: the first worker starts with the host; OnDemand: with the first request.
		if (settings.StartMode == StartMode.AlwaysRunning)
			lock (sync) StartWorker("the host started");
		return Task.CompletedTask;
	}

	public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	// The host is stopping: Kestrel drains the forwarded requests first (HostOptions.ShutdownTimeout), then
	// StoppedAsync stops the workers.
	public Task StoppingAsync(CancellationToken cancellationToken)
	{
		lock (sync) shuttingDown = true;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public async Task StoppedAsync(CancellationToken cancellationToken)
	{
		List<Worker> workers;
		lock (sync)
		{
			workers = retiring.ToList();
			if (current != null) workers.Add(current);
			if (serving != null) workers.Add(serving);
			current = serving = null;
			currentReady.TrySetResult(null);
		}
		foreach (var worker in workers) worker.RequestStop();
		await Task.WhenAll(workers.Select(worker => WaitForExitAsync(worker, settings.ShutdownTimeLimit)));
	}

	// Starts the next worker; the caller holds the lock. After an application restart, the initialization pages are sent
	// only with doAppInitAfterRestart, as on IIS.
	void StartWorker(string reason, bool afterApplicationRestart = false)
	{
		if (shuttingDown || stopped) return;
		var id = ++lastId;
		var worker = new Worker(id, Path.Combine(runDirectory!.FullName, $"worker-{id}.sock"))
		{
			WarmUpPages = initialization.PagesFor(afterApplicationRestart),
		};
		current = worker;
		if (currentReady.Task.IsCompleted) currentReady = NewReady();
		try
		{
			worker.Start(command, Path.Combine(runDirectory.FullName, "host.sock"), token);
		}
		catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
		{
			logger.LogError(e, "Worker {Id} for {Application} didn't start.", id, command.Application);
			current = null;
			worker.Dispose();
			if (!RecordFailure()) currentReady.TrySetResult(null);
			return;
		}
		logger.LogInformation("Started worker {Id} (process {ProcessId}) for {Application}: {Reason}.", id, worker.ProcessId, command.Application, reason);
		_ = MonitorAsync(worker);
		_ = StartupLimitAsync(worker);
	}

	async Task MonitorAsync(Worker worker)
	{
		await worker.Exited;
		lock (sync) OnExited(worker);
	}

	async Task StartupLimitAsync(Worker worker)
	{
		await Task.Delay(settings.StartupTimeLimit);
		lock (sync)
		{
			if (worker != current || worker.State != WorkerState.Starting) return;
			logger.LogError("Worker {Id} (process {ProcessId}) didn't start serving within {Limit}: stopping it.", worker.Id, worker.ProcessId, settings.StartupTimeLimit);
		}
		worker.Kill(); // OnExited counts it as a failure and replaces it
	}

	// The caller holds the lock.
	void OnExited(Worker worker)
	{
		int exitCode = worker.ExitCode;
		if (worker == serving)
		{
			// The old worker of an overlapped recycle ended by itself: the requests wait for the new one.
			serving = null;
			worker.Dispose();
			if (shuttingDown) return;
			logger.LogError("Worker {Id} (process {ProcessId}) exited unexpectedly ({ExitCode}) while its replacement started.", worker.Id, worker.ProcessId, exitCode);
			RecordFailure();
			return;
		}
		if (retiring.Remove(worker) || worker != current)
		{
			logger.LogInformation("Worker {Id} (process {ProcessId}) exited ({ExitCode}).", worker.Id, worker.ProcessId, exitCode);
			worker.Dispose();
			return;
		}
		current = null;
		worker.Dispose();
		if (shuttingDown)
		{
			currentReady.TrySetResult(null);
			return;
		}
		logger.LogError("Worker {Id} (process {ProcessId}) exited unexpectedly ({ExitCode}){State}.", worker.Id, worker.ProcessId, exitCode,
			worker.State == WorkerState.Starting ? " while starting" : worker.State == WorkerState.Warming ? " while warming up" : "");
		if (RecordFailure())
		{
			// The pool is stopped, as IIS stops it: the old worker of an overlapped recycle stops too.
			if (serving != null) Retire(serving, "the application pool stopped", settings.ShutdownTimeLimit, recycle: true);
			return;
		}
		currentReady.TrySetResult(null);
		StartWorker($"worker {worker.Id} exited");
	}

	// Rapid-fail protection: whether this failure stops the pool. The caller holds the lock.
	bool RecordFailure()
	{
		if (!settings.RapidFailProtection) return false;
		var now = DateTime.UtcNow;
		failures.Enqueue(now);
		while (failures.Count > 0 && now - failures.Peek() > settings.RapidFailProtectionInterval) failures.Dequeue();
		if (failures.Count < settings.RapidFailProtectionMaxCrashes) return false;
		stopped = true;
		currentReady.TrySetResult(null);
		logger.LogCritical("Rapid-fail protection: {Count} worker failures within {Interval}. No more workers are started; requests get 503 until the host is restarted.",
			failures.Count, settings.RapidFailProtectionInterval);
		return true;
	}

	// A worker's control message.
	bool OnControlMessage(string kind, int id, string reason)
	{
		lock (sync)
		{
			var worker = current?.Id == id ? current : null;
			switch (kind)
			{
				case WorkerProtocol.Ready:
					if (worker == null || worker.State != WorkerState.Starting) return true; // retired meanwhile
					// The application's virtual path: the initialization pages are relative to it, as on IIS (#190).
					if (reason.StartsWith('/')) worker.ApplicationPath = reason.TrimEnd('/');
					logger.LogInformation("Worker {Id} (process {ProcessId}) is serving ({Seconds:0.0} s after it started){WarmUp}.", id, worker.ProcessId,
						(DateTime.UtcNow - worker.Started).TotalSeconds, worker.WarmUpPages.Count > 0 ? ": warming it up" : "");
					if (worker.WarmUpPages.Count > 0)
					{
						// IIS's Application Initialization: the worker takes traffic once its initialization pages are answered.
						worker.State = WorkerState.Warming;
						_ = WarmUpAsync(worker);
					}
					else OnServing(worker);
					return true;
				case WorkerProtocol.Overlap:
					// An application pool recycle: the worker serves on until the next one is ready (and warm), as IIS overlaps
					// the old worker process with the new one. Not while the pool can't start another.
					// Otherwise (retired, not serving yet, or the pool stopping) the worker recycles at once.
					if (serving?.Id == id) return true; // already overlapped
					if (worker == null || worker.State != WorkerState.Ready || shuttingDown || stopped) return false;
					serving = worker;
					current = null;
					logger.LogInformation("Recycling worker {Id} (process {ProcessId}): {Reason}. It serves until its replacement is ready.", id,
						worker.ProcessId, reason.Length != 0 ? reason : "application pool recycle");
					StartWorker($"worker {id} recycled");
					return true;
				case WorkerProtocol.Recycle:
					// "<drain seconds> <reason>": how long the worker's requests may take to finish (#181).
					var drain = TimeSpan.Zero;
					int space = reason.IndexOf(' ');
					if (int.TryParse(space < 0 ? reason : reason[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
					{
						drain = TimeSpan.FromSeconds(seconds);
						reason = space < 0 ? "" : reason[(space + 1)..];
					}
					reason = reason.Length != 0 ? reason : "the application restarted";
					if (serving?.Id == id)
					{
						// The old worker of an overlapped recycle restarted by itself: the requests wait for its replacement.
						var old = serving;
						serving = null;
						Retire(old, reason, drain + settings.ShutdownTimeLimit, recycle: false);
						return true;
					}
					if (worker == null) return true; // already retired
					current = null;
					currentReady.TrySetResult(null);
					// The worker finishes its requests, for up to the drain time (<httpRuntime shutdownTimeout> and a sixth of
					// it, as .NET Framework waited for an old domain's requests), and exits by itself; one that doesn't is
					// killed the shutdown time limit after that.
					Retire(worker, reason, drain + settings.ShutdownTimeLimit, recycle: false);
					// A drain time means the application restarted; none, an application pool recycle that wasn't overlapped.
					StartWorker($"worker {id} recycled", afterApplicationRestart: drain > TimeSpan.Zero);
					return true;
				default:
					return false;
			}
		}
	}

	// Takes a worker out of service: it finishes its requests and exits (after the recycle command, or once its
	// application has restarted by itself), or is killed after the limit. The caller holds the lock.
	void Retire(Worker worker, string reason, TimeSpan limit, bool recycle)
	{
		worker.State = WorkerState.Retiring;
		retiring.Add(worker);
		logger.LogInformation("Recycling worker {Id} (process {ProcessId}): {Reason}.", worker.Id, worker.ProcessId, reason);
		if (recycle && !worker.SendCommand(WorkerProtocol.RecycleCommand)) worker.RequestStop();
		_ = KillAfterAsync(worker, limit);
	}

	// The worker takes traffic; the old worker of an overlapped recycle recycles. The caller holds the lock.
	void OnServing(Worker worker)
	{
		worker.State = WorkerState.Ready;
		worker.Touch();
		if (serving != null)
		{
			var old = serving;
			serving = null;
			// An application pool recycle: WAS gave the old worker the shutdown time limit.
			Retire(old, $"worker {worker.Id} is ready", settings.ShutdownTimeLimit, recycle: true);
		}
		currentReady.TrySetResult(worker);
		if (settings.IdleTimeout > TimeSpan.Zero && settings.IdleTimeoutAction == IdleTimeoutAction.Terminate) _ = IdleTimeoutAsync(worker);
	}

	// Sends the initialization pages, one after the other, then lets the worker take traffic, whatever they answered: a
	// page that fails is logged, and IIS too gave the worker traffic after it. The warm-up gets the startup time limit;
	// after that the worker takes traffic anyway.
	async Task WarmUpAsync(Worker worker)
	{
		using var limit = new CancellationTokenSource(settings.StartupTimeLimit);
		var started = DateTime.UtcNow;
		foreach (var (page, hostName) in worker.WarmUpPages)
		{
			if (worker.State != WorkerState.Warming) return; // exited or retired
			var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + worker.ApplicationPath + page) { Version = System.Net.HttpVersion.Version11 };
			request.Headers.Host = hostName ?? "localhost";
			request.Headers.TryAddWithoutValidation("User-Agent", ApplicationInitialization.UserAgent);
			// From the server itself, as IIS sent them.
			request.Headers.TryAddWithoutValidation(WorkerProtocol.RemoteHeader, "[127.0.0.1]:0");
			request.Headers.TryAddWithoutValidation(WorkerProtocol.LocalHeader, "[127.0.0.1]:80");
			request.Headers.TryAddWithoutValidation(WorkerProtocol.SchemeHeader, "http");
			try
			{
				using var response = await worker.Invoker.SendAsync(request, limit.Token);
				await response.Content.LoadIntoBufferAsync(limit.Token);
				if ((int)response.StatusCode >= 400)
					logger.LogWarning("Worker {Id}: initialization page {Host}{Page} answered {Status}.", worker.Id, hostName ?? "", worker.ApplicationPath + page, (int)response.StatusCode);
				else
					logger.LogInformation("Worker {Id}: initialization page {Host}{Page} answered {Status}.", worker.Id, hostName ?? "", worker.ApplicationPath + page, (int)response.StatusCode);
			}
			catch (OperationCanceledException) when (limit.IsCancellationRequested)
			{
				logger.LogWarning("Worker {Id}: the initialization pages took longer than {Limit}: it takes traffic now.", worker.Id, settings.StartupTimeLimit);
				break;
			}
			catch (Exception e) when (e is HttpRequestException || e is IOException || e is OperationCanceledException || e is ObjectDisposedException)
			{
				logger.LogWarning("Worker {Id}: initialization page {Host}{Page} failed: {Message}", worker.Id, hostName ?? "", worker.ApplicationPath + page, e.Message);
			}
			finally
			{
				request.Dispose();
			}
		}
		lock (sync)
		{
			if (worker != current || worker.State != WorkerState.Warming) return;
			logger.LogInformation("Worker {Id} (process {ProcessId}) is warm ({Seconds:0.0} s): it takes traffic.", worker.Id, worker.ProcessId,
				(DateTime.UtcNow - started).TotalSeconds);
			OnServing(worker);
		}
	}

	// <processModel idleTimeout>: the worker stops once it has had no request for the idle timeout; with AlwaysRunning a new
	// one starts at once, otherwise with the next request.
	async Task IdleTimeoutAsync(Worker worker)
	{
		while (true)
		{
			var wait = worker.Requests > 0 ? settings.IdleTimeout : worker.LastActivity + settings.IdleTimeout - DateTime.UtcNow;
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : wait);
				continue;
			}
			lock (sync)
			{
				if (worker != current || worker.State != WorkerState.Ready || shuttingDown) return;
				if (worker.Requests > 0 || DateTime.UtcNow - worker.LastActivity < settings.IdleTimeout) continue;
				current = null;
				worker.State = WorkerState.Retiring;
				retiring.Add(worker);
				logger.LogInformation("Worker {Id} (process {ProcessId}) was idle for {IdleTimeout}: stopping it.", worker.Id, worker.ProcessId, settings.IdleTimeout);
				// Its application ends (Application_End), as when IIS shut an idle worker down.
				worker.RequestStop();
				_ = KillAfterAsync(worker, settings.ShutdownTimeLimit);
				if (settings.StartMode == StartMode.AlwaysRunning) StartWorker($"worker {worker.Id} was idle");
				return;
			}
		}
	}

	async Task KillAfterAsync(Worker worker, TimeSpan limit)
	{
		if (await Task.WhenAny(worker.Exited, Task.Delay(limit)) == worker.Exited) return;
		logger.LogWarning("Worker {Id} (process {ProcessId}) was still running {Limit} after it was recycled: killing it.", worker.Id, worker.ProcessId, limit);
		worker.Kill();
	}

	async Task WaitForExitAsync(Worker worker, TimeSpan limit)
	{
		if (await Task.WhenAny(worker.Exited, Task.Delay(limit)) == worker.Exited) return;
		logger.LogWarning("Worker {Id} (process {ProcessId}) was still running {Limit} after it was asked to stop: killing it.", worker.Id, worker.ProcessId, limit);
		worker.Kill();
		await worker.Exited;
	}

	// The worker that serves, once there is one, with the request counted on it (so that the idle timeout doesn't stop it
	// meanwhile); null when none will within the queue timeout (or the request was aborted). Remap: the worker is being
	// warmed and remapManagedRequestsTo's file answers the request.
	async Task<(Worker? Worker, bool Remap)> GetReadyWorkerAsync(CancellationToken aborted)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
		timeout.CancelAfter(settings.QueueTimeout);
		while (true)
		{
			Task<Worker?> ready;
			lock (sync)
			{
				if (stopped || shuttingDown) return (null, false);
				if (current is { State: WorkerState.Ready } worker)
				{
					worker.BeginRequest();
					return (worker, false);
				}
				// An overlapped recycle: the old worker serves until the new one is ready (#193).
				if (serving is { State: WorkerState.Ready } old)
				{
					old.BeginRequest();
					return (old, false);
				}
				if (current == null) StartWorker("a request arrived");
				if (initialization.RemapFile != null && current is { State: WorkerState.Starting or WorkerState.Warming } warming && warming.WarmUpPages.Count > 0)
					return (null, true);
				ready = currentReady.Task;
			}
			try
			{
				await ready.WaitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				return (null, false);
			}
			// Ready, or that worker won't serve (StartWorker then has a new one starting): look again.
		}
	}

	// <applicationInitialization remapManagedRequestsTo>: the file, while the application warms up, as IIS served it.
	async Task RemapAsync(HttpContext context)
	{
		var file = initialization.RemapFile!;
		if (!File.Exists(file))
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}
		if (!new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType(file, out var contentType))
			contentType = "application/octet-stream";
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = contentType;
		context.Response.Headers.CacheControl = "no-cache";
		if (HttpMethods.IsHead(context.Request.Method))
		{
			context.Response.ContentLength = new FileInfo(file).Length;
			return;
		}
		await context.Response.SendFileAsync(file, context.RequestAborted);
	}

	public async Task ForwardAsync(HttpContext context)
	{
		// Only the host says where a request came from.
		foreach (var header in WorkerProtocol.RequestHeaders) context.Request.Headers.Remove(header);
		// A request can be sent again to the next worker if one that is shutting down didn't take it, its body too (#214).
		var body = await RequestBody.ForAsync(context);
		if (body == null) return; // the client went away while it sent the body
		for (int attempt = 0; ; attempt++)
		{
			var (worker, remap) = await GetReadyWorkerAsync(context.RequestAborted);
			if (remap)
			{
				context.Response.Clear();
				await RemapAsync(context);
				return;
			}
			if (worker == null)
			{
				if (!context.RequestAborted.IsCancellationRequested) await ServiceUnavailableAsync(context);
				return;
			}
			body.Rewind();
			var transformer = new WorkerTransformer(attempt < MaxRetries, body);
			ForwarderError error;
			try
			{
				error = await forwarder.SendAsync(context, "http://localhost/", worker.Invoker, requestConfig, transformer);
			}
			finally
			{
				worker.EndRequest();
			}
			if (transformer.WorkerRestarting && !context.Response.HasStarted)
			{
				// The worker is shutting down: its recycle message is on its way, after which the next worker serves. The
				// forwarder has already set the worker's 503 on the response (and won't forward into a response that isn't a
				// fresh 200): clear it first.
				context.Response.Clear();
				try { await Task.Delay(RetryDelay, context.RequestAborted); }
				catch (OperationCanceledException) { return; }
				continue;
			}
			if (error != ForwarderError.None && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
			{
				var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception;
				if (attempt < MaxRetries && (body.Unread ? await HasEndedAsync(worker) : body.CanSendAgain && await StoppedAfterDrainAsync(worker)))
				{
					// The worker's process ended (a crash, Environment.Exit) without answering: the request goes to the next
					// worker, as one queued for a w3wp that died does on IIS. With a body a worker may have read, only when the
					// worker was retiring and stopped by itself: its application had finished the requests it took, and this
					// one was still waiting on its connection when the worker's Kestrel closed the connections it had no request
					// from yet (#214).
					logger.LogInformation("Worker {Id} ended without answering {Method} {Path}: sending it to the next worker.", worker.Id, context.Request.Method, context.Request.Path);
					context.Response.Clear();
					try { await Task.Delay(RetryDelay, context.RequestAborted); }
					catch (OperationCanceledException) { return; }
					continue;
				}
				if (exception is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
				{
					// The worker couldn't be reached at all: the gateway's error.
					logger.LogWarning(exception, "Forwarding {Method} {Path} to worker {Id} failed: {Error}.", context.Request.Method, context.Request.Path, worker.Id, error);
					context.Response.StatusCode = StatusCodes.Status502BadGateway;
				}
				else
				{
					// The worker took the request and then dropped its connection (Request.Abort(), or the process ended):
					// the client's connection is reset, as IIS resets it.
					logger.LogInformation("Worker {Id} ended {Method} {Path} without a response ({Error}): aborting the client's connection.", worker.Id, context.Request.Method, context.Request.Path, error);
					context.Abort();
				}
			}
			return;
		}
	}

	// Whether a worker that dropped a request is going away: its process exits within a second, or it is already retired.
	// A worker that is still serving afterwards dropped the request itself (Request.Abort()).
	static async Task<bool> HasEndedAsync(Worker worker)
	{
		if (worker.State == WorkerState.Retiring) return true;
		return await Task.WhenAny(worker.Exited, Task.Delay(TimeSpan.FromSeconds(1))) == worker.Exited;
	}

	// Whether a retiring worker stopped by itself (exit code 0, not killed) after its application had drained: then a
	// request it dropped without an answer never reached the application (#214).
	static async Task<bool> StoppedAfterDrainAsync(Worker worker)
	{
		if (worker.State != WorkerState.Retiring) return false;
		if (await Task.WhenAny(worker.Exited, Task.Delay(TimeSpan.FromSeconds(10))) != worker.Exited) return false;
		return worker.ExitCode == 0 && !worker.Killed;
	}

	// IIS's answer when the application pool can't serve: 503.
	static Task ServiceUnavailableAsync(HttpContext context)
	{
		context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
		context.Response.ContentType = "text/html; charset=utf-8";
		return context.Response.WriteAsync("<!DOCTYPE html><html><head><title>Service Unavailable</title></head><body><h1>Service Unavailable</h1><p>HTTP Error 503. The service is unavailable.</p></body></html>");
	}

	public void Dispose()
	{
		control?.Dispose();
		lock (sync)
		{
			current?.Dispose();
			foreach (var worker in retiring) worker.Dispose();
		}
		try { runDirectory?.Delete(recursive: true); }
		catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
	}
}
