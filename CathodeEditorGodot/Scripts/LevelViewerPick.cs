using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Screen-space picking against loaded level visuals (mesh triangle ray tests).
/// </summary>
public static class LevelViewerPick
{
	public const string PickableGroup = "level_viewer_pickable";
	public const string OwnerEntityMetaKey = "pick_owner_entity";
	private const string WireframeOverlayGroup = "model_reference_wireframe_overlay";
	private const float RayEpsilon = 0.000001f;

	private static readonly MeshDataTool _meshDataTool = new MeshDataTool();

	public readonly struct PickHit
	{
		public PickHit(Node hitNode, float distance)
		{
			HitNode = hitNode;
			Distance = distance;
		}

		public Node HitNode { get; }
		public float Distance { get; }
	}

	public readonly struct SelectionTarget
	{
		public SelectionTarget(List<uint> entityIds, List<uint> compositeIds, uint leafEntityId)
		{
			EntityIds = entityIds;
			CompositeIds = compositeIds;
			LeafEntityId = leafEntityId;
		}

		public List<uint> EntityIds { get; }
		public List<uint> CompositeIds { get; }
		public uint LeafEntityId { get; }
	}

	public static void RegisterPickableSubtree(Node3D ownerEntityNode)
	{
		if (ownerEntityNode == null)
			return;

		ownerEntityNode.SetMeta(OwnerEntityMetaKey, ownerEntityNode);
		RegisterPickableRecursive(ownerEntityNode, ownerEntityNode);
	}

	private static void RegisterPickableRecursive(Node node, Node3D ownerEntityNode)
	{
		if (node.IsInGroup(WireframeOverlayGroup))
			goto children;

		if (node is MeshInstance3D meshInstance)
		{
			Mesh mesh = meshInstance.Mesh;
			if (mesh != null && mesh.GetSurfaceCount() > 0)
			{
				if (!meshInstance.IsInGroup(PickableGroup))
					meshInstance.AddToGroup(PickableGroup);
				meshInstance.SetMeta(OwnerEntityMetaKey, ownerEntityNode);
			}
		}
		else if (node is VisualInstance3D visual)
		{
			Aabb bounds = visual.GetAabb();
			if (bounds.Size.LengthSquared() > RayEpsilon)
			{
				if (!visual.IsInGroup(PickableGroup))
					visual.AddToGroup(PickableGroup);
				visual.SetMeta(OwnerEntityMetaKey, ownerEntityNode);
			}
		}

		children:
		foreach (Node child in node.GetChildren())
			RegisterPickableRecursive(child, ownerEntityNode);
	}


	private static void CollectHits(
		Node node,
		Vector3 origin,
		Vector3 direction,
		Node contentRoot,
		Commands commands,
		ref PickHit? best)
	{
		if (node.IsInGroup(WireframeOverlayGroup))
			goto children;

		if (node is VisualInstance3D visual && visual.IsInGroup(PickableGroup))
		{
			if (!LevelViewerCompositeFocus.IsNodeInScope(visual, contentRoot, commands))
				goto children;

			Aabb local = visual.GetAabb();
			if (local.Size.LengthSquared() <= RayEpsilon)
				goto children;

			Aabb global = visual.GlobalTransform * local;
			if (!TryRayIntersectAabb(origin, direction, global, out float aabbDistance))
				goto children;

			if (best.HasValue && aabbDistance > best.Value.Distance)
				goto children;

			float distance;
			if (visual is MeshInstance3D meshInstance)
			{
				if (!TryRayIntersectMeshInstance(meshInstance, origin, direction, out distance))
					goto children;
			}
			else if (!TryRayIntersectAabb(origin, direction, global, out distance))
			{
				goto children;
			}

			if (!best.HasValue || distance < best.Value.Distance)
				best = new PickHit(visual, distance);
		}

		children:
		foreach (Node child in node.GetChildren())
			CollectHits(child, origin, direction, contentRoot, commands, ref best);
	}

	public static PickHit? PickClosest(
		Node3D searchRoot,
		Camera3D camera,
		Vector2 screenPosition,
		Node contentRoot,
		Commands commands)
	{
		if (searchRoot == null || camera == null)
			return null;

		Vector3 origin = camera.ProjectRayOrigin(screenPosition);
		Vector3 direction = camera.ProjectRayNormal(screenPosition);
		if (direction.LengthSquared() < 0.0001f)
			return null;

		direction = direction.Normalized();

		if (LevelViewerCompositeFocus.HasActiveComposite && commands != null)
			LevelViewerCompositeFocus.RebuildScopeCache(commands);

		PickHit? best = null;
		CollectHits(searchRoot, origin, direction, contentRoot, commands, ref best);
		return best;
	}

	/// <summary>
	/// Closest ancestor entity root node (keys in the scene entity map), for correct nested drill paths.
	/// </summary>
	public static Node3D ResolveNearestEntityNode(Node start, IReadOnlyDictionary<Node3D, Entity> nodeEntities)
	{
		if (start == null || nodeEntities == null)
			return null;

		Node current = start;
		while (current != null)
		{
			if (current is EntityOverride entityOverride)
				return entityOverride;

			if (current is Node3D node3D && nodeEntities.ContainsKey(node3D))
				return node3D;

			current = current.GetParent();
		}

		return null;
	}

	public static SelectionTarget? BuildSelectionTarget(
		Node3D hitEntityNode,
		Node3D contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities)
	{
		if (hitEntityNode == null || contentRoot == null || nodeEntities == null)
			return null;

		List<Node3D> chain = new List<Node3D>();
		Node current = hitEntityNode;
		while (current != null && current != contentRoot)
		{
			if (current is Node3D node3D && nodeEntities.ContainsKey(node3D))
				chain.Add(node3D);
			current = current.GetParent();
		}

		if (chain.Count == 0)
			return null;

		chain.Reverse();

		List<uint> entityIds = new List<uint>(chain.Count);
		List<uint> compositeIds = new List<uint>(chain.Count);
		for (int i = 0; i < chain.Count; i++)
		{
			if (!nodeEntities.TryGetValue(chain[i], out Entity entity))
				return null;

			entityIds.Add(entity.shortGUID.AsUInt32);
			if (!chain[i].HasMeta(AlienScene.OwnerCompositeMetaKey))
				return null;

			compositeIds.Add(chain[i].GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32());
		}

		return new SelectionTarget(entityIds, compositeIds, entityIds[entityIds.Count - 1]);
	}

	public static bool IsCompositeInstanceEntity(Entity entity, Commands commands)
	{
		return TryGetEnteredCompositeGuid(entity, commands, out _);
	}

	public static bool TryGetEnteredCompositeGuid(Entity entity, Commands commands, out uint compositeGuid)
	{
		compositeGuid = 0;
		if (entity == null || commands == null || entity.variant != EntityVariant.FUNCTION)
			return false;

		FunctionEntity function = (FunctionEntity)entity;
		if (function.function.IsFunctionType)
			return false;

		Composite child = commands.GetComposite(function.function);
		if (child == null)
			return false;

		compositeGuid = child.shortGUID.AsUInt32;
		return true;
	}

	public static bool TryResolveChainEntity(
		Commands commands,
		SelectionTarget target,
		int index,
		out Entity entity)
	{
		entity = null;
		if (commands == null || target.EntityIds == null || target.CompositeIds == null)
			return false;

		if (index < 0 || index >= target.EntityIds.Count)
			return false;

		Composite composite = commands.GetComposite(new ShortGuid(target.CompositeIds[0]));
		if (composite == null)
			return false;

		for (int i = 0; i < index; i++)
		{
			Entity stepEntity = composite.GetEntityByID(new ShortGuid(target.EntityIds[i]));
			if (stepEntity == null || !TryGetEnteredCompositeGuid(stepEntity, commands, out uint childCompositeGuid))
				return false;

			composite = commands.GetComposite(new ShortGuid(childCompositeGuid));
			if (composite == null)
				return false;
		}

		entity = composite.GetEntityByID(new ShortGuid(target.EntityIds[index]));
		return entity != null;
	}

	/// <summary>
	/// True when <paramref name="candidate"/> is a strict prefix of the current drill path (shallower target).
	/// </summary>
	public static bool IsStrictAncestorPath(IReadOnlyList<uint> candidate, IReadOnlyList<uint> currentPath, int currentDepth)
	{
		if (candidate == null || currentPath == null || currentDepth <= 0)
			return false;

		if (candidate.Count >= currentDepth || candidate.Count == 0)
			return false;

		for (int i = 0; i < candidate.Count; i++)
		{
			if (candidate[i] != currentPath[i])
				return false;
		}

		return true;
	}

	/// <summary>
	/// True when the first <paramref name="depth"/> entities match between paths (shared drill branch).
	/// </summary>
	public static bool SharesDrillPrefix(IReadOnlyList<uint> candidate, IReadOnlyList<uint> currentPath, int depth)
	{
		if (candidate == null || currentPath == null || depth <= 0)
			return false;

		int compareCount = Mathf.Min(depth, Mathf.Min(candidate.Count, currentPath.Count));
		if (compareCount == 0)
			return false;

		for (int i = 0; i < compareCount; i++)
		{
			if (candidate[i] != currentPath[i])
				return false;
		}

		return true;
	}

	/// <summary>
	/// Index of the deepest entity in the pick chain that belongs to <paramref name="activeCompositeId"/>.
	/// </summary>
	public static bool TryFindEntityIndexInActiveComposite(
		SelectionTarget target,
		uint activeCompositeId,
		out int entityIndex)
	{
		entityIndex = -1;
		if (activeCompositeId == 0 || target.CompositeIds == null)
			return false;

		for (int i = target.CompositeIds.Count - 1; i >= 0; i--)
		{
			if (target.CompositeIds[i] == activeCompositeId)
			{
				entityIndex = i;
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Selects the entity under the cursor that lives in the composite OpenCAGE is currently viewing.
	/// </summary>
	public static bool TryBuildActiveCompositeSelectionPath(
		SelectionTarget target,
		uint activeCompositeId,
		out List<uint> pathEntities,
		out List<uint> pathComposites)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();

		if (!TryFindEntityIndexInActiveComposite(target, activeCompositeId, out int entityIndex))
			return false;

		for (int i = 0; i <= entityIndex; i++)
		{
			pathEntities.Add(target.EntityIds[i]);
			pathComposites.Add(target.CompositeIds[i]);
		}

		return pathEntities.Count > 0;
	}

	/// <summary>
	/// Steps into the composite instance clicked in the active composite (Ctrl+MMB).
	/// </summary>
	public static bool TryBuildCompositeDrillPath(
		SelectionTarget target,
		uint activeCompositeId,
		Commands commands,
		out List<uint> pathEntities,
		out List<uint> pathComposites)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();

		if (commands == null || !TryFindEntityIndexInActiveComposite(target, activeCompositeId, out int entityIndex))
			return false;

		if (!TryResolveChainEntity(commands, target, entityIndex, out Entity entity)
			|| !IsCompositeInstanceEntity(entity, commands))
		{
			return false;
		}

		int steps = entityIndex + 1;
		return TryBuildSelectionPath(target, steps, commands, out pathEntities, out pathComposites, out bool entitySelected)
			&& !entitySelected;
	}

	/// <summary>
	/// Builds editor path packets for stepping into composite instances (not entity selection).
	/// </summary>
	public static bool TryBuildSelectionPath(
		SelectionTarget target,
		int entityStepCount,
		Commands commands,
		out List<uint> pathEntities,
		out List<uint> pathComposites,
		out bool entitySelected)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();
		entitySelected = false;

		if (commands == null || target.EntityIds == null || target.CompositeIds == null || target.EntityIds.Count == 0)
			return false;

		int steps = Mathf.Clamp(entityStepCount, 1, target.EntityIds.Count);
		for (int i = 0; i < steps; i++)
			pathEntities.Add(target.EntityIds[i]);

		bool atLeafStep = steps == target.EntityIds.Count;
		entitySelected = atLeafStep
			&& TryResolveChainEntity(commands, target, steps - 1, out Entity leafEntity)
			&& !IsCompositeInstanceEntity(leafEntity, commands);

		pathComposites.Add(target.CompositeIds[0]);
		for (int i = 1; i < steps; i++)
			pathComposites.Add(target.CompositeIds[i]);

		if (!entitySelected)
		{
			if (!TryResolveChainEntity(commands, target, steps - 1, out Entity enteredThrough)
				|| !TryGetEnteredCompositeGuid(enteredThrough, commands, out uint enteredComposite))
			{
				return false;
			}

			pathComposites.Add(enteredComposite);
		}

		return pathComposites.Count > 0;
	}

	private static bool TryRayIntersectMeshInstance(
		MeshInstance3D meshInstance,
		Vector3 origin,
		Vector3 direction,
		out float distance)
	{
		distance = 0f;
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return false;

		Mesh mesh = meshInstance.Mesh;
		if (mesh == null || mesh.GetSurfaceCount() == 0)
			return false;

		Transform3D inverse = meshInstance.GlobalTransform.AffineInverse();
		Vector3 localOrigin = inverse * origin;
		Vector3 localDirection = inverse.Basis * direction;
		if (localDirection.LengthSquared() < RayEpsilon)
			return false;

		localDirection = localDirection.Normalized();

		float closestLocalT = float.MaxValue;
		bool anyHit = false;
		bool doubleSided = IsDoubleSidedMesh(meshInstance);

		for (int surfaceIndex = 0; surfaceIndex < mesh.GetSurfaceCount(); surfaceIndex++)
		{
			if (mesh is ArrayMesh arrayMesh)
				CollectMeshDataToolSurfaceHits(arrayMesh, surfaceIndex, localOrigin, localDirection, doubleSided, ref closestLocalT, ref anyHit);
			else
				CollectTriangleListSurfaceHits(mesh, surfaceIndex, localOrigin, localDirection, doubleSided, ref closestLocalT, ref anyHit);
		}

		if (!anyHit)
			return false;

		Vector3 worldHit = meshInstance.GlobalTransform * (localOrigin + localDirection * closestLocalT);
		distance = origin.DistanceTo(worldHit);
		return true;
	}

	private static void CollectTriangleListSurfaceHits(
		Mesh mesh,
		int surfaceIndex,
		Vector3 localOrigin,
		Vector3 localDirection,
		bool doubleSided,
		ref float closestLocalT,
		ref bool anyHit)
	{
		Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surfaceIndex);
		if (arrays == null || arrays.Count == 0)
			return;

		Variant verticesVariant = arrays[(int)Mesh.ArrayType.Vertex];
		if (verticesVariant.VariantType != Variant.Type.PackedVector3Array)
			return;

		Vector3[] vertices = verticesVariant.AsVector3Array();
		if (vertices.Length < 3)
			return;

		Variant indexVariant = arrays[(int)Mesh.ArrayType.Index];
		if (indexVariant.VariantType == Variant.Type.PackedInt32Array)
		{
			int[] indices = indexVariant.AsInt32Array();
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				TryUpdateClosestTriangleHit(
					localOrigin,
					localDirection,
					vertices[indices[i]],
					vertices[indices[i + 1]],
					vertices[indices[i + 2]],
					doubleSided,
					ref closestLocalT,
					ref anyHit);
			}

			return;
		}

		for (int i = 0; i + 2 < vertices.Length; i += 3)
		{
			TryUpdateClosestTriangleHit(
				localOrigin,
				localDirection,
				vertices[i],
				vertices[i + 1],
				vertices[i + 2],
				doubleSided,
				ref closestLocalT,
				ref anyHit);
		}
	}

	private static void CollectMeshDataToolSurfaceHits(
		ArrayMesh mesh,
		int surfaceIndex,
		Vector3 localOrigin,
		Vector3 localDirection,
		bool doubleSided,
		ref float closestLocalT,
		ref bool anyHit)
	{
		if (_meshDataTool.CreateFromSurface(mesh, surfaceIndex) != Error.Ok)
			return;

		int faceCount = _meshDataTool.GetFaceCount();
		for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
		{
			TryUpdateClosestTriangleHit(
				localOrigin,
				localDirection,
				_meshDataTool.GetVertex(_meshDataTool.GetFaceVertex(faceIndex, 0)),
				_meshDataTool.GetVertex(_meshDataTool.GetFaceVertex(faceIndex, 1)),
				_meshDataTool.GetVertex(_meshDataTool.GetFaceVertex(faceIndex, 2)),
				doubleSided,
				ref closestLocalT,
				ref anyHit);
		}
	}

	private static void TryUpdateClosestTriangleHit(
		Vector3 localOrigin,
		Vector3 localDirection,
		Vector3 a,
		Vector3 b,
		Vector3 c,
		bool doubleSided,
		ref float closestLocalT,
		ref bool anyHit)
	{
		if (!TryRayIntersectTriangle(localOrigin, localDirection, a, b, c, doubleSided, out float localT))
			return;

		if (localT >= closestLocalT)
			return;

		closestLocalT = localT;
		anyHit = true;
	}

	private static bool IsDoubleSidedMesh(MeshInstance3D meshInstance)
	{
		Material material = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
		if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null)
			return false;

		string path = shaderMaterial.Shader.ResourcePath;
		if (string.IsNullOrEmpty(path))
			return false;

		return path.Contains("double_sided")
			|| path.Contains("preview_icon_billboard")
			|| path.Contains("preview_overlay_line");
	}

	private static bool TryRayIntersectTriangle(
		Vector3 origin,
		Vector3 direction,
		Vector3 v0,
		Vector3 v1,
		Vector3 v2,
		bool doubleSided,
		out float t)
	{
		t = 0f;

		Vector3 edge1 = v1 - v0;
		Vector3 edge2 = v2 - v0;
		Vector3 pvec = direction.Cross(edge2);
		float det = edge1.Dot(pvec);
		if (Mathf.Abs(det) < RayEpsilon)
			return false;

		if (!doubleSided && det > 0f)
			return false;

		float invDet = 1f / det;
		Vector3 tvec = origin - v0;
		float u = tvec.Dot(pvec) * invDet;
		if (u < 0f || u > 1f)
			return false;

		Vector3 qvec = tvec.Cross(edge1);
		float v = direction.Dot(qvec) * invDet;
		if (v < 0f || u + v > 1f)
			return false;

		t = edge2.Dot(qvec) * invDet;
		return t >= RayEpsilon;
	}

	private static bool TryRayIntersectAabb(Vector3 origin, Vector3 direction, Aabb bounds, out float distance)
	{
		distance = 0f;
		Vector3 min = bounds.Position;
		Vector3 max = bounds.Position + bounds.Size;

		float tMin = -float.MaxValue;
		float tMax = float.MaxValue;

		if (!TrySlab(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax)
			|| !TrySlab(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax)
			|| !TrySlab(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax))
		{
			return false;
		}

		if (tMax < 0f || tMin > tMax)
			return false;

		distance = tMin >= 0f ? tMin : tMax;
		return distance >= 0f;
	}

	private static bool TrySlab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
	{
		if (Mathf.Abs(direction) < 0.00001f)
			return origin >= min && origin <= max;

		float inv = 1f / direction;
		float t0 = (min - origin) * inv;
		float t1 = (max - origin) * inv;
		if (t0 > t1)
		{
			float swap = t0;
			t0 = t1;
			t1 = swap;
		}

		if (t0 > tMin)
			tMin = t0;
		if (t1 < tMax)
			tMax = t1;
		return true;
	}
}
