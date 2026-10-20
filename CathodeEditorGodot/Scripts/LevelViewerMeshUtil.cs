using Godot;

/// <summary>
/// Frustum / occlusion culling tweaks for dynamically spawned level meshes, and the one safe way to
/// hand a surface to an ArrayMesh.
/// </summary>
public static class LevelViewerMeshUtil
{
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
		{
			bounds = ComputeAabb(sourceVertices);
			if (bounds.HasVolume())
				meshInstance.CustomAabb = bounds;
		}

		/* Culled by its own bounds: nothing the viewer draws moves its vertices in a shader. These used to cull by a box
		   grown by their radius - 32 m at the least - all round, so every view drew everything within 32 m or more of the
		   camera whichever way it faced (TECH_HUB facing a wall: 21k objects for the 547 meshes in front of it). */
		meshInstance.ExtraCullMargin = 0f;

		ApplyDistanceCulling(meshInstance, DrawDistanceFor(bounds));
	}

	public static void ApplyLargeSceneOptimizations(MeshInstance3D meshInstance, float visibilityRangeEnd)
	{
		//A planet is drawn at the sky, not where its mesh is (PlanetLook): never culled by distance
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance) || PlanetLook.IsPlanet(meshInstance))
			return;

		meshInstance.IgnoreOcclusionCulling = false;
		meshInstance.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
		float sized = DrawDistanceFor(meshInstance.Mesh != null ? meshInstance.Mesh.GetAabb() : new Aabb());
		ApplyDistanceCulling(meshInstance, visibilityRangeEnd > 0f ? Mathf.Min(visibilityRangeEnd, sized) : sized);
	}

	/* How far a mesh is drawn: 100 times its radius, 15 m at the least - a 1 m prop to 50 m, a 10 m wall to half a kilometre.
	   The viewer's frame is bound by how many things it draws, not by the GPU (TECH_HUB: ~74k objects, 61 ms on the render
	   thread, the GPU 7 ms of it), and the level's every prop was drawn from anywhere in it, walls between or not: this took
	   TECH_HUB's views from 15 to 34 fps with nothing missing from them. The level's own LOD ranges don't cull (98% are
	   10,000 m - the game leaves that to its zones). Composite previews lift it for their capture. */
	public const float DrawDistancePerRadius = 100f;
	public const float MinDrawDistance = 15f;

	public static float DrawDistanceFor(Aabb bounds)
	{
		return Mathf.Max(bounds.Size.Length() * 0.5f * DrawDistancePerRadius, MinDrawDistance);
	}

	/// <summary>
	/// The draw distance of a mesh drawn scaled - a unit volume stretched to an entity's size (SurfaceEffectOverlay) - by the
	/// size it is drawn at: by the unit mesh's own, a 20 m frost box stopped drawing at 87 m.
	/// </summary>
	public static void ApplyDrawDistance(MeshInstance3D meshInstance, Vector3 drawnSize)
	{
		if (meshInstance != null)
			ApplyDistanceCulling(meshInstance, DrawDistanceFor(new Aabb(Vector3.Zero, drawnSize)));
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
