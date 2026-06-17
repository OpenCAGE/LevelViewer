using Godot;

/// <summary>
/// Level Viewer logging that mirrors to OpenCAGE via <see cref="ViewerLogBridge"/>.
/// </summary>
public static class ViewerLog
{
	private static readonly bool _embedded = System.Environment.GetEnvironmentVariable("OPENCAGE_EMBEDDED") == "1";

	public static void Print(string message)
	{
		if (!_embedded)
			GD.Print(message);
		ViewerLogBridge.TryForward(message, false);
	}

	public static void PrintErr(string message)
	{
		if (!_embedded)
			GD.PrintErr(message);
		ViewerLogBridge.TryForward(message, true);
	}
}
