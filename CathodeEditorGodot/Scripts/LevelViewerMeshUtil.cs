using Godot;

/// <summary>
/// Frustum / occlusion culling tweaks for dynamically spawned level meshes.
/// </summary>
public static class LevelViewerMeshUtil
{
	private const float DefaultExtraCullMarginMin = 32f;
	private const float LargeSceneExtraCullMarginCap = 96f;

	public static void ConfigureMeshInstance(MeshInstance3D meshInstance, Vector3[] sourceVertices = null)
	{
		if (meshInstance == null)
			return;

		meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		meshInstance.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
		meshInstance.IgnoreOcclusionCulling = !ModelReferenceRenderSettings.UseDistanceCulling;

		Aabb bounds = meshInstance.Mesh != null ? meshInstance.Mesh.GetAabb() : new Aabb();
		if (bounds.Size.LengthSquared() < 1e-8f && sourceVertices != null && sourceVertices.Length > 0)
			bounds = ComputeAabb(sourceVertices);

		float grow = Mathf.Max(bounds.Size.Length() * 0.5f, DefaultExtraCullMarginMin);
		if (ModelReferenceRenderSettings.UseDistanceCulling)
			grow = Mathf.Min(grow, LargeSceneExtraCullMarginCap);

		meshInstance.ExtraCullMargin = grow;

		if (bounds.HasVolume())
			meshInstance.CustomAabb = bounds.Grow(grow * 0.25f);

		if (ModelReferenceRenderSettings.UseDistanceCulling)
			ApplyDistanceCulling(meshInstance, ModelReferenceRenderSettings.VisibilityRangeEnd);
	}

	public static void ApplyLargeSceneOptimizations(MeshInstance3D meshInstance, float visibilityRangeEnd)
	{
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		meshInstance.IgnoreOcclusionCulling = false;
		meshInstance.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
		meshInstance.ExtraCullMargin = Mathf.Min(meshInstance.ExtraCullMargin, LargeSceneExtraCullMarginCap);
		ApplyDistanceCulling(meshInstance, visibilityRangeEnd);
	}

	private static void ApplyDistanceCulling(MeshInstance3D meshInstance, float visibilityRangeEnd)
	{
		if (visibilityRangeEnd <= 0f)
			return;

		meshInstance.VisibilityRangeBegin = 0f;
		meshInstance.VisibilityRangeEnd = visibilityRangeEnd;
		meshInstance.VisibilityRangeBeginMargin = 0f;
		meshInstance.VisibilityRangeEndMargin = Mathf.Min(visibilityRangeEnd * 0.1f, 384f);
		meshInstance.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
	}

	public static Aabb ComputeAabb(Vector3[] vertices)
	{
		if (vertices == null || vertices.Length == 0)
			return new Aabb();

		Vector3 min = vertices[0];
		Vector3 max = vertices[0];
		for (int i = 1; i < vertices.Length; i++)
		{
			Vector3 v = vertices[i];
			min.X = Mathf.Min(min.X, v.X);
			min.Y = Mathf.Min(min.Y, v.Y);
			min.Z = Mathf.Min(min.Z, v.Z);
			max.X = Mathf.Max(max.X, v.X);
			max.Y = Mathf.Max(max.Y, v.Y);
			max.Z = Mathf.Max(max.Z, v.Z);
		}

		return new Aabb(min, max - min);
	}
}
