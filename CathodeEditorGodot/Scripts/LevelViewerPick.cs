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

	private sealed class CachedMeshSurface
	{
		public Vector3[] Vertices;
		public int[] Indices = System.Array.Empty<int>();
	}

	private static readonly Dictionary<ulong, CachedMeshSurface[]> _meshGeometryCache = new();
	private static readonly Dictionary<Node3D, List<MeshInstance3D>> _pickablesByOwner = new();
	private static readonly Dictionary<Node3D, Aabb> _ownerGlobalBounds = new();
	private static readonly List<Node3D> _scopedPickOwners = new();
	private static readonly HashSet<MeshInstance3D> _registeredPickables = new();
	private static bool _scopedPickablesDirty = true;

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

	public static void ClearRegistry()
	{
		_meshGeometryCache.Clear();
		_pickablesByOwner.Clear();
		_ownerGlobalBounds.Clear();
		_scopedPickOwners.Clear();
		_registeredPickables.Clear();
		_scopedPickablesDirty = true;
	}

	public static void InvalidateScopedPickables()
	{
		_scopedPickablesDirty = true;
	}

	public static void RegisterPickableSubtree(Node3D ownerEntityNode)
	{
		if (ownerEntityNode == null)
			return;

		ownerEntityNode.SetMeta(OwnerEntityMetaKey, ownerEntityNode);
		RegisterPickableRecursive(ownerEntityNode, ownerEntityNode);
	}

	public static void RegisterPickableMesh(MeshInstance3D meshInstance, Node3D ownerNode)
	{
		if (meshInstance == null || ownerNode == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (meshInstance.IsInGroup(WireframeOverlayGroup))
			return;

		Mesh mesh = meshInstance.Mesh;
		if (mesh == null || mesh.GetSurfaceCount() == 0)
			return;

		if (!_registeredPickables.Add(meshInstance))
			return;

		if (!meshInstance.IsInGroup(PickableGroup))
			meshInstance.AddToGroup(PickableGroup);
		meshInstance.SetMeta(OwnerEntityMetaKey, ownerNode);

		if (!_pickablesByOwner.TryGetValue(ownerNode, out List<MeshInstance3D> meshes))
		{
			meshes = new List<MeshInstance3D>();
			_pickablesByOwner.Add(ownerNode, meshes);
		}

		meshes.Add(meshInstance);
		_ownerGlobalBounds.Remove(ownerNode);
		_scopedPickablesDirty = true;
	}

	private static void RegisterPickableRecursive(Node node, Node3D ownerEntityNode)
	{
		if (node.IsInGroup(WireframeOverlayGroup))
			goto children;

		if (node is MeshInstance3D meshInstance)
			RegisterPickableMesh(meshInstance, ownerEntityNode);
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

	private static void EnsureScopedPickOwners(Node contentRoot, Commands commands)
	{
		if (!_scopedPickablesDirty)
			return;

		_scopedPickOwners.Clear();
		foreach (KeyValuePair<Node3D, List<MeshInstance3D>> entry in _pickablesByOwner)
		{
			Node3D owner = entry.Key;
			if (owner == null || !GodotObject.IsInstanceValid(owner))
				continue;

			List<MeshInstance3D> meshes = entry.Value;
			if (meshes == null || meshes.Count == 0)
				continue;

			MeshInstance3D probe = meshes[0];
			if (probe == null || !GodotObject.IsInstanceValid(probe))
				continue;

			if (!LevelViewerCompositeFocus.IsNodeInScope(probe, contentRoot, commands))
				continue;

			_scopedPickOwners.Add(owner);
		}

		_scopedPickablesDirty = false;
	}

	private static Aabb GetOwnerGlobalBounds(Node3D owner)
	{
		if (owner != null && GodotObject.IsInstanceValid(owner) && _ownerGlobalBounds.TryGetValue(owner, out Aabb cached) && cached.HasVolume())
			return cached;

		Aabb merged = new Aabb();
		bool hasBounds = false;
		if (_pickablesByOwner.TryGetValue(owner, out List<MeshInstance3D> meshes))
		{
			for (int i = 0; i < meshes.Count; i++)
			{
				MeshInstance3D mesh = meshes[i];
				if (mesh == null || !GodotObject.IsInstanceValid(mesh))
					continue;

				Aabb local = mesh.GetAabb();
				if (local.Size.LengthSquared() <= RayEpsilon)
					continue;

				Aabb global = mesh.GlobalTransform * local;
				if (!hasBounds)
				{
					merged = global;
					hasBounds = true;
				}
				else
				{
					merged = merged.Merge(global);
				}
			}
		}

		if (owner != null && GodotObject.IsInstanceValid(owner))
			_ownerGlobalBounds[owner] = merged;

		return merged;
	}

	private static bool TryPickOwner(
		Node3D owner,
		Vector3 origin,
		Vector3 direction,
		ref PickHit? best)
	{
		if (!_pickablesByOwner.TryGetValue(owner, out List<MeshInstance3D> meshes) || meshes.Count == 0)
			return false;

		Aabb ownerBounds = GetOwnerGlobalBounds(owner);
		if (!ownerBounds.HasVolume() || !TryRayIntersectAabb(origin, direction, ownerBounds, out float ownerDistance))
			return false;

		if (best.HasValue && ownerDistance > best.Value.Distance)
			return false;

		bool anyHit = false;
		for (int i = 0; i < meshes.Count; i++)
		{
			MeshInstance3D meshInstance = meshes[i];
			if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
				continue;

			if (!TryRayIntersectMeshInstance(meshInstance, origin, direction, out float distance))
				continue;

			if (best.HasValue && distance >= best.Value.Distance)
				continue;

			best = new PickHit(meshInstance, distance);
			anyHit = true;
		}

		return anyHit;
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

		EnsureScopedPickOwners(contentRoot, commands);

		PickHit? best = null;
		for (int i = 0; i < _scopedPickOwners.Count; i++)
			TryPickOwner(_scopedPickOwners[i], origin, direction, ref best);

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
		CachedMeshSurface[] surfaces = GetCachedMeshSurfaces(mesh);

		for (int surfaceIndex = 0; surfaceIndex < surfaces.Length; surfaceIndex++)
		{
			CachedMeshSurface surface = surfaces[surfaceIndex];
			if (surface.Indices.Length > 0)
			{
				int[] indices = surface.Indices;
				Vector3[] vertices = surface.Vertices;
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
			}
			else
			{
				Vector3[] vertices = surface.Vertices;
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
		}

		if (!anyHit)
			return false;

		Vector3 worldHit = meshInstance.GlobalTransform * (localOrigin + localDirection * closestLocalT);
		distance = origin.DistanceTo(worldHit);
		return true;
	}

	private static CachedMeshSurface[] GetCachedMeshSurfaces(Mesh mesh)
	{
		ulong meshId = mesh.GetRid().Id;
		if (_meshGeometryCache.TryGetValue(meshId, out CachedMeshSurface[] cached))
			return cached;

		int surfaceCount = mesh.GetSurfaceCount();
		CachedMeshSurface[] surfaces = new CachedMeshSurface[surfaceCount];
		for (int surfaceIndex = 0; surfaceIndex < surfaceCount; surfaceIndex++)
			surfaces[surfaceIndex] = BuildCachedMeshSurface(mesh, surfaceIndex);

		_meshGeometryCache[meshId] = surfaces;
		return surfaces;
	}

	private static CachedMeshSurface BuildCachedMeshSurface(Mesh mesh, int surfaceIndex)
	{
		CachedMeshSurface surface = new CachedMeshSurface { Vertices = System.Array.Empty<Vector3>() };
		Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surfaceIndex);
		if (arrays == null || arrays.Count == 0)
			return surface;

		Variant verticesVariant = arrays[(int)Mesh.ArrayType.Vertex];
		if (verticesVariant.VariantType != Variant.Type.PackedVector3Array)
			return surface;

		surface.Vertices = verticesVariant.AsVector3Array();
		Variant indexVariant = arrays[(int)Mesh.ArrayType.Index];
		if (indexVariant.VariantType == Variant.Type.PackedInt32Array)
			surface.Indices = indexVariant.AsInt32Array();

		return surface;
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
