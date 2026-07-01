using Godot;

/// <summary>
/// Level Viewer logging. Every line is:
///  - mirrored to OpenCAGE via <see cref="ViewerLogBridge"/>,
///  - written to a dedicated <c>user://viewer.log</c> file that is flushed on every line so nothing
///    is lost if the process dies (Godot's own godot.log is buffered and its tail is lost on a hard
///    crash, and in embedded mode GD.Print is suppressed - so that file can't be relied on).
/// </summary>
public static class ViewerLog
{
	private static readonly bool _embedded = System.Environment.GetEnvironmentVariable("OPENCAGE_EMBEDDED") == "1";

	private static readonly object _fileLock = new object();
	private static string _logFilePath;
	private static bool _logFileResolved;
	private static bool _globalHandlersInstalled;

	/// <summary>
	/// Installs process-wide handlers so a managed exception escaping ANY thread we don't explicitly
	/// wrap (background Task, GC finalizer, native-&gt;managed signal callback) is still flushed to the
	/// viewer log before the process dies. If a crash leaves no line here, it was a native crash.
	/// Safe to call multiple times; only the first call takes effect.
	/// </summary>
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

	public static void Print(string message)
	{
		if (!_embedded)
			GD.Print(message);
		WriteToFile(message, false);
		ViewerLogBridge.TryForward(message, false);
	}

	public static void PrintErr(string message)
	{
		if (!_embedded)
			GD.PrintErr(message);
		WriteToFile(message, true);
		ViewerLogBridge.TryForward(message, true);
	}

	/// <summary>Absolute path of the dedicated viewer log, or null if it couldn't be resolved.</summary>
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

				// AppendAllText opens, writes and closes (flushing to disk) each call, so a hard crash
				// can't discard buffered log lines.
				System.IO.File.AppendAllText(path, line);
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
