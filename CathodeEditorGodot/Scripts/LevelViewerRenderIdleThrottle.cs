using Godot;

/// <summary>
/// Drops frame rate and skips heavy per-frame work when the user is idle.
/// Embedded-in-OpenCAGE mode keeps the message pump alive and returns focus to the host.
/// </summary>
public static class LevelViewerRenderIdleThrottle
{
	private const double IdleSecondsBeforeSuspend = 2.0;
	private const int SuspendedMaxFps = 1;
	private const int EmbeddedIdleMaxFps = 30;

	// Guards all mutable state below. NotifyUserActivity is called from the WebSocket receive
	// thread as well as the main thread, so reads/writes must be synchronized.
	private static readonly object _stateLock = new object();
	private static double _lastActivitySeconds;
	private static bool _suspended;
	private static bool _embeddedMode;
	private static bool _renderIdle;
	private static int _loadActiveCount;

	public static bool IsIdle
	{
		get
		{
			double now = Time.GetTicksMsec() / 1000.0;
			lock (_stateLock)
				return now - _lastActivitySeconds >= IdleSecondsBeforeSuspend;
		}
	}

	/// <summary>Full suspend (standalone viewer): drops to 1 FPS and stops _Process on gated nodes.</summary>
	public static bool IsSuspended
	{
		get { lock (_stateLock) return _suspended; }
	}

	/// <summary>Embedded idle: lower FPS and skip heavy camera work, but keep processing alive.</summary>
	public static bool IsRenderIdle
	{
		get { lock (_stateLock) return _renderIdle; }
	}

	public static void ConfigureEmbeddedMode(bool embedded)
	{
		lock (_stateLock)
			_embeddedMode = embedded;
	}

	/// <summary>
	/// Safe to call from any thread. State is updated under lock; the actual <c>Engine.MaxFps</c>
	/// mutation is marshalled to the main thread since this is also called from the WebSocket
	/// receive thread and Godot engine objects must not be mutated off the main thread.
	/// </summary>
	public static void NotifyUserActivity()
	{
		bool restore;
		lock (_stateLock)
		{
			_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
			restore = _suspended || _renderIdle;
			if (restore)
			{
				_suspended = false;
				_renderIdle = false;
			}
		}

		// Only fires on the rare idle->active transition. On the main thread (input path) apply
		// immediately; from the WebSocket receive thread, marshal so the engine object is never
		// mutated off the main thread.
		if (restore)
		{
			if (OS.GetThreadCallerId() == OS.GetMainThreadId())
				Engine.MaxFps = 0;
			else
				Callable.From(static () => Engine.MaxFps = 0).CallDeferred();
		}
	}

	/// <summary>While &gt; 0, idle throttling is disabled so level load can run at full frame rate.</summary>
	public static void SetLoadActive(bool active)
	{
		bool restore = false;
		lock (_stateLock)
		{
			if (active)
			{
				_loadActiveCount++;
				_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
				restore = _suspended || _renderIdle;
				if (restore)
				{
					_suspended = false;
					_renderIdle = false;
				}
			}
			else if (_loadActiveCount > 0)
			{
				_loadActiveCount--;
			}
		}

		if (restore)
			Engine.MaxFps = 0;
	}

	/// <summary>Main-thread only: may release Win32 focus/capture to the OpenCAGE host.</summary>
	public static void Update()
	{
		bool enterEmbeddedIdle = false;
		bool enterSuspend = false;
		lock (_stateLock)
		{
			if (_loadActiveCount > 0)
				return;

			if (_suspended || _renderIdle)
				return;

			double now = Time.GetTicksMsec() / 1000.0;
			if (now - _lastActivitySeconds < IdleSecondsBeforeSuspend)
				return;

			if (_embeddedMode)
			{
				_renderIdle = true;
				enterEmbeddedIdle = true;
			}
			else
			{
				_suspended = true;
				enterSuspend = true;
			}
		}

		if (enterSuspend)
		{
			Engine.MaxFps = SuspendedMaxFps;
		}
		else if (enterEmbeddedIdle)
		{
			Engine.MaxFps = EmbeddedIdleMaxFps;
			LevelViewerEmbeddedFocus.ReleaseFocusAndCaptureToHost();
		}
	}
}
