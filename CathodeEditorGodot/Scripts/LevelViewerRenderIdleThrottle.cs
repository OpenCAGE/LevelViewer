using Godot;

/// <summary>
/// Lowers GPU load when the user is idle in a large scene (reduces sustained Vulkan pressure).
/// </summary>
public static class LevelViewerRenderIdleThrottle
{
	private const double IdleSecondsBeforeCap = 3.0;
	private const int IdleMaxFps = 12;

	private static double _lastActivitySeconds;
	private static bool _fpsCapped;

	public static void NotifyUserActivity()
	{
		_lastActivitySeconds = Time.GetTicksMsec() / 1000.0;
		if (_fpsCapped)
			RestoreFps();
	}

	public static void Update(double deltaSeconds)
	{
		if (!ModelReferenceRenderSettings.UseDistanceCulling)
		{
			RestoreFps();
			return;
		}

		double now = Time.GetTicksMsec() / 1000.0;
		double idleSeconds = now - _lastActivitySeconds;
		if (!_fpsCapped && idleSeconds >= IdleSecondsBeforeCap)
		{
			Engine.MaxFps = IdleMaxFps;
			_fpsCapped = true;
		}
	}

	private static void RestoreFps()
	{
		if (!_fpsCapped)
			return;

		Engine.MaxFps = 0;
		_fpsCapped = false;
	}
}
