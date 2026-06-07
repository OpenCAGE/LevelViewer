/// <summary>
/// Viewer options for ModelReference mesh renderables (SpawnRenderable).
/// </summary>
public static class ModelReferenceRenderSettings
{
	/// <summary>When model-ref mesh count exceeds this, enable GPU-saving distance/occlusion culling.</summary>
	public const int LargeSceneMeshThreshold = 15000;

	public static bool WireframeEnabled { get; private set; }
	public static bool UseDistanceCulling { get; private set; }
	public static float VisibilityRangeEnd { get; private set; }

	public static void ResetForLevelLoad()
	{
		UseDistanceCulling = false;
		VisibilityRangeEnd = 0f;
	}

	public static void NotifyMeshSpawned(int spawnedCount)
	{
		if (UseDistanceCulling || spawnedCount < LargeSceneMeshThreshold)
			return;

		UseDistanceCulling = true;
		VisibilityRangeEnd = ComputeVisibilityRangeEnd(spawnedCount);
	}

	public static void FinalizeLevelLoad(int meshCount)
	{
		if (meshCount < LargeSceneMeshThreshold)
		{
			UseDistanceCulling = false;
			VisibilityRangeEnd = 0f;
			return;
		}

		UseDistanceCulling = true;
		VisibilityRangeEnd = ComputeVisibilityRangeEnd(meshCount);
	}

	public static void SetWireframe(bool enabled)
	{
		WireframeEnabled = enabled;
	}

	private static float ComputeVisibilityRangeEnd(int meshCount)
	{
		if (meshCount >= 65000)
			return 5000f;
		if (meshCount >= 40000)
			return 6000f;
		return 7500f;
	}
}
