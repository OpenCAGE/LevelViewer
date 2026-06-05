using Godot;

/// <summary>
/// Transform snap values synced from OpenCAGE. Zero means snapping is disabled.
/// </summary>
public static class LevelViewerTransformSnap
{
	public static float GridSize { get; set; }

	public static float RotationDegrees { get; set; }

	public static float SnapValue(float value, float step)
	{
		if (step <= 0f)
			return value;

		return Mathf.Round(value / step) * step;
	}
}
