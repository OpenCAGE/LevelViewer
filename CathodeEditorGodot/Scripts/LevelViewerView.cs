using Godot;
using System;

/// <summary>
/// Frames the runtime camera (and editor 3D view when running from the editor) onto loaded level content.
/// Unity's Scene view auto-frames via SceneView.FrameLastActiveSceneView; Godot splits editor vs game viewports.
/// </summary>
public static class LevelViewerView
{
	public const string ContentGroup = "level_viewer_content";

	public static bool TryComputeGlobalAabb(Node3D root, out Aabb bounds)
	{
		bounds = new Aabb();
		if (root == null || !GodotObject.IsInstanceValid(root))
			return false;

		bool hasBounds = false;
		AccumulateAabb(root, ref bounds, ref hasBounds);
		return hasBounds && bounds.HasVolume();
	}

	private static void AccumulateAabb(Node node, ref Aabb bounds, ref bool hasBounds)
	{
		if (node is VisualInstance3D visual)
		{
			Aabb local = visual.GetAabb();
			if (local.Size.LengthSquared() > 0.000001f)
			{
				Aabb global = visual.GlobalTransform * local;
				bounds = hasBounds ? bounds.Merge(global) : global;
				hasBounds = true;
			}
		}

		foreach (Node child in node.GetChildren())
			AccumulateAabb(child, ref bounds, ref hasBounds);
	}

	public static void FrameRuntimeCamera(Node3D contentRoot, Camera3D camera = null)
	{
		if (!TryComputeGlobalAabb(contentRoot, out Aabb bounds))
			return;

		if (camera == null || !GodotObject.IsInstanceValid(camera))
			camera = contentRoot.GetViewport()?.GetCamera3D();

		if (camera == null || !GodotObject.IsInstanceValid(camera))
			return;

		ApplyCameraToBounds(camera, bounds, 2.8f, 4f, 500000f);
	}

	public static void FrameRuntimeCameraOnNode(Node3D target, Camera3D camera = null)
	{
		FrameRuntimeCameraClose(target, camera, distanceScale: 2.8f, minDistance: 4f, maxDistance: 500000f);
	}

	/// <summary>Move the game camera to a close, entity-focused view (selection / focus).</summary>
	public static void FrameRuntimeCameraClose(
		Node3D target,
		Camera3D camera = null,
		float distanceScale = 1.35f,
		float minDistance = 1.5f,
		float maxDistance = 64f)
	{
		if (target == null || !GodotObject.IsInstanceValid(target))
			return;

		Aabb bounds = new Aabb(target.GlobalPosition, Vector3.One);
		if (TryComputeGlobalAabb(target, out Aabb subtree))
			bounds = subtree;

		if (camera == null || !GodotObject.IsInstanceValid(camera))
			camera = target.GetViewport()?.GetCamera3D();

		if (camera == null || !GodotObject.IsInstanceValid(camera))
			return;

		ApplyCameraToBounds(camera, bounds, distanceScale, minDistance, maxDistance);
	}

	public static bool TryComputeLocalSubtreeAabb(Node3D root, out Aabb localBounds)
	{
		localBounds = new Aabb();
		if (root == null || !GodotObject.IsInstanceValid(root))
			return false;

		if (!TryComputeGlobalAabb(root, out Aabb globalBounds) || !globalBounds.HasVolume())
			return false;

		Transform3D toLocal = root.GlobalTransform.AffineInverse();
		Vector3 min = toLocal * globalBounds.Position;
		Vector3 max = min;

		Vector3 size = globalBounds.Size;
		Vector3[] corners =
		{
			globalBounds.Position,
			globalBounds.Position + new Vector3(size.X, 0f, 0f),
			globalBounds.Position + new Vector3(0f, size.Y, 0f),
			globalBounds.Position + new Vector3(0f, 0f, size.Z),
			globalBounds.Position + new Vector3(size.X, size.Y, 0f),
			globalBounds.Position + new Vector3(size.X, 0f, size.Z),
			globalBounds.Position + new Vector3(0f, size.Y, size.Z),
			globalBounds.End,
		};

		for (int i = 0; i < corners.Length; i++)
		{
			Vector3 local = toLocal * corners[i];
			min.X = Mathf.Min(min.X, local.X);
			min.Y = Mathf.Min(min.Y, local.Y);
			min.Z = Mathf.Min(min.Z, local.Z);
			max.X = Mathf.Max(max.X, local.X);
			max.Y = Mathf.Max(max.Y, local.Y);
			max.Z = Mathf.Max(max.Z, local.Z);
		}

		localBounds = new Aabb(min, max - min);
		return localBounds.HasVolume();
	}

	private static void ApplyCameraToBounds(
		Camera3D camera,
		Aabb bounds,
		float distanceScale,
		float minDistance,
		float maxDistance)
	{
		Vector3 center = bounds.GetCenter();
		float radius = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z)) * 0.5f;
		float distance = Mathf.Clamp(Mathf.Max(radius * distanceScale, 0.5f), minDistance, maxDistance);

		Vector3 viewOffset = camera.GlobalPosition - center;
		if (viewOffset.LengthSquared() < 0.25f)
			viewOffset = -camera.GlobalTransform.Basis.Z;
		if (viewOffset.LengthSquared() < 0.01f)
			viewOffset = new Vector3(0.85f, 0.55f, 0.85f);

		Vector3 eye = center + viewOffset.Normalized() * distance;
		camera.GlobalPosition = eye;
		camera.LookAt(center, Vector3.Up);
		camera.Near = 0.05f;
		camera.Far = Mathf.Max(distance * 50f, 50000f);

		if (camera is LevelViewerCamera viewerCamera)
			viewerCamera.SyncAnglesFromTransform();
	}

#if TOOLS
	/// <summary>
	/// Selects the runtime node in the editor so the 3D view / Remote tree can frame it (press F).
	/// </summary>
	public static void TryFrameEditorOn(Node3D node)
	{
		// F5 / Play and exported builds: not editor hint — EditorInterface singleton is unavailable.
		if (!Engine.IsEditorHint() || node == null || !GodotObject.IsInstanceValid(node))
			return;

		EditorInterface editor = EditorInterface.Singleton;
		if (editor == null)
			return;

		EditorSelection selection = editor.GetSelection();
		selection.Clear();
		selection.AddNode(node);
		editor.EditNode(node);
	}
#endif

	public static void FrameAll(Node3D contentRoot, Camera3D runtimeCamera, bool focusEditor)
	{
		if (contentRoot == null || !GodotObject.IsInstanceValid(contentRoot))
			return;

		FrameRuntimeCamera(contentRoot, runtimeCamera);

#if TOOLS
		if (focusEditor && Engine.IsEditorHint())
			TryFrameEditorOn(contentRoot);
#endif
	}
}
