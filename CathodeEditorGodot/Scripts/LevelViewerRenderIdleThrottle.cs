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
			return now - _lastActivitySeconds >= IdleSecondsBeforeSuspend;
		}
	}

	/// <summary>Full suspend (standalone viewer): drops to 1 FPS and stops _Process on gated nodes.</summary>
	public static bool IsSuspended => _suspended;

	/// <summary>Embedded idle: lower FPS and skip heavy camera work, but keep processing alive.</summary>
	public static bool IsRenderIdle => _renderIdle;

	public static void ConfigureEmbeddedMode(bool embedded)
	{
		_embeddedMode = embedded;
	}

	public static void NotifyUserActivity()
	{
		_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
		if (_suspended || _renderIdle)
			Restore();
	}

	/// <summary>While &gt; 0, idle throttling is disabled so level load can run at full frame rate.</summary>
	public static void SetLoadActive(bool active)
	{
		if (active)
		{
			_loadActiveCount++;
			_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
			if (_suspended || _renderIdle)
				Restore();
			return;
		}

		if (_loadActiveCount > 0)
			_loadActiveCount--;
	}

	public static void Update()
	{
		if (_loadActiveCount > 0)
			return;

		double now = Time.GetTicksMsec() / 1000.0;
		double idleSeconds = now - _lastActivitySeconds;
		if (_suspended || _renderIdle)
			return;

		if (idleSeconds >= IdleSecondsBeforeSuspend)
		{
			if (_embeddedMode)
				EnterEmbeddedIdle();
			else
				EnterSuspend();
		}
	}

	private static void EnterSuspend()
	{
		_suspended = true;
		Engine.MaxFps = SuspendedMaxFps;
	}

	private static void EnterEmbeddedIdle()
	{
		_renderIdle = true;
		Engine.MaxFps = EmbeddedIdleMaxFps;
		LevelViewerEmbeddedFocus.ReleaseFocusAndCaptureToHost();
	}

	private static void Restore()
	{
		_suspended = false;
		_renderIdle = false;
		Engine.MaxFps = 0;
	}
}
