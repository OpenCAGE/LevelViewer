using Godot;

/// <summary>
/// Frustum / occlusion culling tweaks for dynamically spawned level meshes, and the one safe way to
/// hand a surface to an ArrayMesh.
/// </summary>
public static class LevelViewerMeshUtil
{
	private const float DefaultExtraCullMarginMin = 32f;
	private const float LargeSceneExtraCullMarginCap = 96f;

	/// <summary>
	/// The only AddSurfaceFromArrays the viewer calls. Every site goes through here, and passes an
	/// <paramref name="arrays"/> it is itself holding alive (a `using` local) - see CollisionMeshOverlay.BuildMesh.
	/// </summary>
	/// <remarks>
	/// Calling the two-argument overload is not enough. The binding fills the omitted blendShapes and lods
	/// arguments in with `new Array&lt;Array&gt;()` and `new Dictionary()` of its own, hands the engine their
	/// native handles, and holds no managed reference to either across the icall. The engine reads the lods
	/// dictionary at the very END of the surface build - after the slow vertex/index work - so the collector
	/// has the whole build to notice the wrappers are unreachable, finalize them, and free the natives the
	/// engine is about to walk: an access violation with no backtrace, mid-populate or on the next selection
	/// (release 0.18.0.35). Making the defaults ourselves as locals rooted to the end of the call closes
	/// that, and the KeepAlives make it explicit rather than a property of the current codegen.
	///
	/// Array&lt;T&gt; is not IDisposable in this binding; the untyped Array underneath it is, and the explicit
	/// conversion hands back that same object (shared, not copied), so that is what gets disposed. The typed
	/// wrapper is still what is passed, so the engine sees exactly the typed empty array the default would be.
	/// </remarks>
	public static void AddSurface(ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays)
	{
		var blendShapes = new Godot.Collections.Array<Godot.Collections.Array>();
		using (var blendShapesNative = (Godot.Collections.Array)blendShapes)
		using (var lods = new Godot.Collections.Dictionary())
		{
			mesh.AddSurfaceFromArrays(primitive, arrays, blendShapes, lods);
			System.GC.KeepAlive(arrays);
			System.GC.KeepAlive(blendShapes);
			System.GC.KeepAlive(blendShapesNative);
			System.GC.KeepAlive(lods);
		}
	}

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
