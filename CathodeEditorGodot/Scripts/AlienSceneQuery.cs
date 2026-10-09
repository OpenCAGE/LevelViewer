using CATHODE;
using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using OpenCAGE.UnityConnection;
using System.Collections.Generic;

/// <summary>
/// What OpenCAGE's viewport queries (VIEWPORT_QUERY) and look-through placements (VIEWPORT_SET_CAMERA camera_look_through)
/// read from the scene: what a ray lands on, and an entity's node. Read from the scene as it is drawn - an Animation Mode
/// pose, alias overrides and edits not yet saved included - which only this side has.
/// </summary>
public partial class AlienScene
{
	/// <summary>
	/// The node of the entity at <paramref name="path"/>: an instance path from the composite the scene was built from, the
	/// entity last (as SyncedAnimationTarget.path and a selection's path are). Null when it is not in the scene. Main thread.
	/// </summary>
	public Node3D FindEntityNode(List<uint> path)
	{
		if (path == null || path.Count == 0 || _parentNode == null || !GodotObject.IsInstanceValid(_parentNode) || !_content.Loaded)
			return null;

		Node3D node = GetEntityNode(path, _parentNode);
		return node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree() ? node : null;
	}

	/// <summary>
	/// The nearest surface along a ray (Godot world space) that a click could land on - what is in scope of the composite
	/// focus and drawn under the render filters; icon billboards too when <paramref name="camera"/> is given, since they face
	/// it - with the entity drawing it, and for a model what model and material it is. The point and normal are in the space
	/// of the scene's root on CATHODE's axes, as VIEWER_CAMERA_POSE's pose is. Main thread.
	/// </summary>
	public ViewportPickResult PickForQuery(Vector3 origin, Vector3 direction, Camera3D camera)
	{
		ViewportPickResult result = new ViewportPickResult();
		if (_parentNode == null || !GodotObject.IsInstanceValid(_parentNode) || !_content.Loaded || !origin.IsFinite() || !direction.IsFinite()
			|| direction.LengthSquared() < 1e-8f)
			return result;
		direction = direction.Normalized();

		LevelViewerPick.PickHit? hit = LevelViewerPick.RaycastClosest(origin, direction, camera, _parentNode, _content.Level?.Commands);
		if (!hit.HasValue)
			return result;

		MeshInstance3D mesh = hit.Value.HitNode as MeshInstance3D;
		Vector3 normal = -direction;
		if (mesh != null && LevelViewerPick.TryGetSurfaceNormal(mesh, origin, direction, out Vector3 surfaceNormal))
			normal = surfaceNormal;

		//The content is moved to sit near the origin (RecenterContentOrigin): the root node's space is the composite's own
		Transform3D toRoot = _parentNode.GlobalTransform.AffineInverse();
		Vector3 point = toRoot * hit.Value.Position;
		Vector3 rootNormal = (toRoot.Basis * normal).Normalized();
		result.hit = true;
		result.position = new[] { point.X, point.Y, -point.Z };
		result.normal = new[] { rootNormal.X, rootNormal.Y, -rootNormal.Z };
		result.distance = hit.Value.Distance;

		Node3D entityNode = LevelViewerPick.ResolvePickOwnerEntityNode(hit.Value.HitNode, _nodeEntities);
		LevelViewerPick.SelectionTarget? target = entityNode != null ? LevelViewerPick.BuildSelectionTarget(entityNode, _parentNode, _nodeEntities) : null;
		if (target.HasValue)
		{
			result.path_entities = new List<uint>(target.Value.EntityIds);
			result.path_composites = new List<uint>(target.Value.CompositeIds);
		}

		if (mesh != null && _modelReferenceMeshes.TryGetValue(mesh, out Materials.Material material))
		{
			result.kind = "model";
			DescribeModelHit(mesh, material, result);
		}
		else if (mesh != null && _sceneFilterMeshes.TryGetValue(mesh, out SceneFilterMesh filter))
			result.kind = filter.Kind == SceneFilterKind.OcclusionMeshes ? "occlusion" : "collision";
		else
			result.kind = entityNode != null ? "preview" : "other";
		return result;
	}

	/* The model and material a model mesh was built from: the submesh by the shared Godot mesh it was given (one per write
	   index), the material as drawn now (a remap applied). */
	private void DescribeModelHit(MeshInstance3D mesh, Materials.Material material, ViewportPickResult result)
	{
		if (material != null)
		{
			result.material = material.Name ?? "";
			List<Materials.Material> materials = _content.Level?.Materials?.Entries;
			if (materials != null)
				result.material_index = materials.FindIndex(o => ReferenceEquals(o, material));
		}

		Mesh shared = mesh.Mesh;
		if (shared == null)
			return;
		foreach (KeyValuePair<int, MeshHolder> entry in _modelMeshesByWriteIndex)
		{
			if (!ReferenceEquals(entry.Value?.MainMesh, shared))
				continue;
			result.submesh = entry.Key;
			Models.CS2.Component.LOD.Submesh submesh = _content.Level?.Models?.GetAtWriteIndex(entry.Key);
			if (submesh != null && _submeshOwners.TryGetValue(submesh, out SubmeshOwner owner))
			{
				result.model = owner.Model?.Name ?? "";
				result.lod = owner.Lod?.Name ?? "";
			}
			break;
		}

		//Built under its "model: LOD" name, which says as much where the tables above cannot
		if (result.model.Length == 0 && !string.IsNullOrEmpty(shared.ResourceName))
		{
			string name = shared.ResourceName;
			int split = name.IndexOf(": ");
			result.model = split > 0 ? name.Substring(0, split) : name;
			if (split > 0)
				result.lod = name.Substring(split + 2);
		}
	}
}
