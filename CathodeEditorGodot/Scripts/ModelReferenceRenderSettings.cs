/// <summary>
/// Viewer options for ModelReference mesh renderables (SpawnRenderable).
/// </summary>
public static class ModelReferenceRenderSettings
{
	public static bool WireframeEnabled { get; private set; }

	public static void SetWireframe(bool enabled)
	{
		WireframeEnabled = enabled;
	}
}
