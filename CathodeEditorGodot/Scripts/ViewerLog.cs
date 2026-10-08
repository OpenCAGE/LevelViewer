using Godot;

/// <summary>
/// Level Viewer logging. Disabled by default; enable <see cref="Enabled"/> for diagnostics.
/// When enabled, each line is mirrored to OpenCAGE via <see cref="ViewerLogBridge"/> and
/// written to <c>user://viewer.log</c>.
/// </summary>
public static class ViewerLog
{
	public static bool Enabled { get; set; }

	private static readonly bool _embedded = System.Environment.GetEnvironmentVariable("OPENCAGE_EMBEDDED") == "1";

	private static readonly object _fileLock = new object();
	private static string _logFilePath;
	private static bool _logFileResolved;
	//Characters written since the file was (re)started: near enough its size, without asking the file system per line
	private static long _logFileChars;
	private const long MaxLogFileChars = 32L * 1024 * 1024;
	private static bool _globalHandlersInstalled;

	public static void InstallGlobalExceptionHandlers()
	{
		if (_globalHandlersInstalled)
			return;
		_globalHandlersInstalled = true;

		System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			PrintErr("[Viewer] FATAL unhandled exception (terminating=" + e.IsTerminating + "): " + e.ExceptionObject);
		};

		System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			PrintErr("[Viewer] Unobserved task exception: " + e.Exception);
			e.SetObserved();
		};
	}

	/* stdout/stderr are the pipe OpenCAGE reads when it hosts us, and since issue #628 it keeps the tail of
	 * that pipe and submits it with the exit code when the process dies. So these lines go to the console
	 * in embedded mode too: the engine's own errors already do, and "[Viewer] Closing: ..." or a FATAL
	 * handler line next to them is what turns an exit code into a diagnosis. */
	public static void Print(string message)
	{
		if (!Enabled)
			return;

		GD.Print(message);
		WriteToFile(message, false);
		ForwardUnlessHosted(message, false);
	}

	public static void PrintErr(string message)
	{
		if (!Enabled)
			return;

		GD.PrintErr(message);
		WriteToFile(message, true);
		ForwardUnlessHosted(message, true);
	}

	/* Hosted, OpenCAGE already reads every line off stdout/stderr (the same relay and crash tail these feed), so sending
	   each one again as a VIEWER_LOG packet only doubled the traffic - one echo per packet received, given the "Packet:"
	   breadcrumbs - for a release build to deserialise and throw away on its UI thread. A viewer run on its own still
	   forwards: nothing reads its console. This relies on run/flush_stdout_on_print in project.godot - a release export
	   buffers stdout otherwise, and the lines reached OpenCAGE minutes late, or not at all when the viewer died. */
	private static void ForwardUnlessHosted(string message, bool isError)
	{
		if (_embedded)
			return;
		ViewerLogBridge.TryForward(message, isError);
	}

	public static string LogFilePath
	{
		get { lock (_fileLock) return LogFilePathLocked(); }
	}

	private static void WriteToFile(string message, bool isError)
	{
		string line = System.DateTime.Now.ToString("HH:mm:ss.fff")
			+ (isError ? " ERR " : " LOG ") + message + "\n";
		try
		{
			lock (_fileLock)
			{
				string path = LogFilePathLocked();
				if (path == null)
					return;

				/* Embedded, every packet adds a breadcrumb, and the file was only started again by the next viewer: a long
				   session grew it without limit. Past the cap the current file becomes viewer.log.1 (replacing the last
				   one) and a new one starts, so the tail of a session - what a crash report wants - is always there. */
				if (_logFileChars > MaxLogFileChars)
				{
					//Counted as started either way: a rotation that fails (viewer.log.1 held open, say) is tried again after
					//another full file, not on every line - and the line below is written regardless
					_logFileChars = 0;
					try
					{
						System.IO.File.Move(path, path + ".1", true); //a rename, not a 32 MB copy under the lock
						System.IO.File.WriteAllText(path, "=== Viewer log continued " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " (earlier lines in viewer.log.1) ===\n");
					}
					catch
					{
					}
				}

				System.IO.File.AppendAllText(path, line);
				_logFileChars += line.Length;
			}
		}
		catch
		{
			// Logging must never throw.
		}
	}

	private static string LogFilePathLocked()
	{
		if (!_logFileResolved)
		{
			_logFileResolved = true;
			if (!Enabled)
				return null;

			try
			{
				_logFilePath = ProjectSettings.GlobalizePath("user://viewer.log");
				System.IO.File.WriteAllText(
					_logFilePath,
					"=== Viewer log session " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\n");
			}
			catch
			{
				_logFilePath = null;
			}
		}

		return _logFilePath;
	}
}
