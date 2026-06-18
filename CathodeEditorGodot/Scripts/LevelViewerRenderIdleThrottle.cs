using Godot;

/// <summary>
/// Drops frame rate and skips heavy per-frame work when the user is idle.
/// </summary>
public static class LevelViewerRenderIdleThrottle
{
	private const double IdleSecondsBeforeSuspend = 2.0;
	private const int SuspendedMaxFps = 1;

	private static double _lastActivitySeconds;
	private static bool _suspended;

	public static bool IsIdle
	{
		get
		{
			double now = Time.GetTicksMsec() / 1000.0;
			return now - _lastActivitySeconds >= IdleSecondsBeforeSuspend;
		}
	}

	public static bool IsSuspended => _suspended;

	public static void NotifyUserActivity()
	{
		_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
		if (_suspended)
			Restore();
	}

	public static void Update()
	{
		double now = Time.GetTicksMsec() / 1000.0;
		double idleSeconds = now - _lastActivitySeconds;
		if (!_suspended && idleSeconds >= IdleSecondsBeforeSuspend)
			EnterSuspend();
	}

	private static void EnterSuspend()
	{
		_suspended = true;
		Engine.MaxFps = SuspendedMaxFps;
	}

	private static void Restore()
	{
		if (!_suspended)
			return;

		_suspended = false;
		Engine.MaxFps = 0;
	}
}
