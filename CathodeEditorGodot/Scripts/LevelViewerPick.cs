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
	//Every IsInGroup/AddToGroup taking a string interns a fresh StringName, and registering a
	//level's meshes does that hundreds of thousands of times. These are the same names, converted once.
	private static readonly StringName PickableGroupName = new StringName("level_viewer_pickable");

	/// <summary>
	/// Scene-filter geometry (occlusion hulls). Registered and unregistered by the filter that draws
	/// it rather than by a subtree walk, so a switched-off hull never becomes clickable.
	/// </summary>
	public const string SceneFilterGroup = "scene_filter_renderable";

	private const string WireframeOverlayGroup = "model_reference_wireframe_overlay";
	private static readonly StringName WireframeOverlayGroupName = new StringName("model_reference_wireframe_overlay");
	private const float RayEpsilon = 0.000001f;

	private enum PickFaceMode
	{
		FrontOnly,
		BackOnly,
		DoubleSided,
	}

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
	private static readonly HashSet<Node3D> _suppressedPickOwners = new();
	private static bool _scopedPickablesDirty = true;

	// Batched bounds invalidation: while a batch is open, moved nodes are collected and the (expensive)
	// per-node registry scan is deferred to BatchInvalidatePickBounds. Above the threshold we just drop
	// the whole AABB cache, which is O(1) and rebuilt lazily on the next pick.
	private static readonly HashSet<Node3D> _batchInvalidateNodes = new();
	private static bool _batchInvalidateActive;
	private const int BatchInvalidateClearAllThreshold = 16;

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
		_pickOwners.Clear();
		_suppressedPickOwners.Clear();
		_scopedPickablesDirty = true;
		_batchInvalidateActive = false;
		_batchInvalidateNodes.Clear();
	}

	public static int PickOwnerCount => _pickablesByOwner.Count;

	public static bool OwnerHasPickMeshes(Node3D owner)
	{
		return owner != null
			&& _pickablesByOwner.TryGetValue(owner, out List<MeshInstance3D> meshes)
			&& meshes != null
			&& meshes.Count > 0;
	}

	public static bool TryCopyPickMeshesForOwner(Node3D owner, List<MeshInstance3D> destination)
	{
		if (destination == null || owner == null || !GodotObject.IsInstanceValid(owner))
			return false;

		if (!_pickablesByOwner.TryGetValue(owner, out List<MeshInstance3D> meshes) || meshes == null || meshes.Count == 0)
			return false;

		for (int i = 0; i < meshes.Count; i++)
		{
			MeshInstance3D mesh = meshes[i];
			if (mesh != null && GodotObject.IsInstanceValid(mesh))
				destination.Add(mesh);
		}

		return destination.Count > 0;
	}

	/// <summary>
	/// Collects registered pick meshes for <paramref name="root"/> and nested entity nodes under it.
	/// Walks the entity tree only — not every mesh in the scene subtree.
	/// </summary>
	public static void CollectPickMeshesForEntitySubtree(Node3D root, List<MeshInstance3D> destination)
	{
		if (root == null || destination == null || !GodotObject.IsInstanceValid(root))
			return;

		TryCopyPickMeshesForOwner(root, destination);

		foreach (Node child in root.GetChildren())
		{
			if (child is not Node3D child3D || !GodotObject.IsInstanceValid(child3D))
				continue;

			if (!AlienScene.HasOwnerComposite(child3D))
				continue;

			CollectPickMeshesForEntitySubtree(child3D, destination);
		}
	}

	public static void SetOwnerSuppressed(Node3D owner, bool suppressed)
	{
		if (owner == null || !GodotObject.IsInstanceValid(owner))
			return;

		if (suppressed)
			_suppressedPickOwners.Add(owner);
		else
			_suppressedPickOwners.Remove(owner);

		_scopedPickablesDirty = true;
	}

	public static bool IsOwnerSuppressed(Node3D owner)
	{
		return owner != null && _suppressedPickOwners.Contains(owner);
	}

	public static void InvalidateScopedPickables()
	{
		_scopedPickablesDirty = true;
	}

	/// <summary>Iterate registered pick owners (one scope/dim decision per entity, not per mesh).</summary>
	public static void ForEachPickOwner(System.Action<Node3D, IReadOnlyList<MeshInstance3D>> action)
	{
		if (action == null)
			return;

		foreach (KeyValuePair<Node3D, List<MeshInstance3D>> entry in _pickablesByOwner)
		{
			Node3D owner = entry.Key;
			if (owner == null || !GodotObject.IsInstanceValid(owner))
				continue;

			List<MeshInstance3D> meshes = entry.Value;
			if (meshes == null || meshes.Count == 0)
				continue;

			PruneInvalidPickMeshes(meshes);
			if (meshes.Count == 0)
				continue;

			action(owner, meshes);
		}
	}

	private static void PruneInvalidPickMeshes(List<MeshInstance3D> meshes)
	{
		for (int i = meshes.Count - 1; i >= 0; i--)
		{
			MeshInstance3D mesh = meshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				meshes.RemoveAt(i);
		}
	}

	/// <summary>Clear every cached owner AABB (e.g. after shifting the level content root).</summary>
	public static void InvalidateAllPickBounds()
	{
		_ownerGlobalBounds.Clear();
	}

	/// <summary>
	/// Opens a batch so repeated <see cref="InvalidatePickBounds"/> calls (e.g. moving every instance of
	/// an entity) collapse into a single registry pass on <see cref="EndBatchPickBoundsInvalidation"/>,
	/// instead of rescanning the whole owner registry once per moved node.
	/// </summary>
	public static void BeginBatchPickBoundsInvalidation()
	{
		_batchInvalidateActive = true;
		_batchInvalidateNodes.Clear();
	}

	/// <summary>Flushes and closes a batch opened by <see cref="BeginBatchPickBoundsInvalidation"/>.</summary>
	public static void EndBatchPickBoundsInvalidation()
	{
		if (!_batchInvalidateActive)
			return;

		_batchInvalidateActive = false;

		if (_batchInvalidateNodes.Count == 0)
			return;

		// Many nodes moved: clearing the whole cache is O(1) and cheaper than N owner scans.
		if (_batchInvalidateNodes.Count >= BatchInvalidateClearAllThreshold)
		{
			_ownerGlobalBounds.Clear();
			_batchInvalidateNodes.Clear();
			return;
		}

		foreach (Node3D node in _batchInvalidateNodes)
			InvalidatePickBoundsImmediate(node);

		_batchInvalidateNodes.Clear();
	}

	/// <summary>
	/// Drop cached broad-phase AABBs for pick owners whose meshes moved with <paramref name="node"/>.
	/// </summary>
	public static void InvalidatePickBounds(Node3D node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;

		if (_batchInvalidateActive)
		{
			_batchInvalidateNodes.Add(node);
			return;
		}

		InvalidatePickBoundsImmediate(node);
	}

	private static void InvalidatePickBoundsImmediate(Node3D node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;

		foreach (KeyValuePair<Node3D, List<MeshInstance3D>> entry in _pickablesByOwner)
		{
			Node3D owner = entry.Key;
			if (owner == null || !GodotObject.IsInstanceValid(owner))
				continue;

			if (owner == node || owner.IsAncestorOf(node))
			{
				_ownerGlobalBounds.Remove(owner);
				continue;
			}

			// Model-ref / alias: meshes live under a pointed target outside the alias branch.
			List<MeshInstance3D> meshes = entry.Value;
			if (meshes == null)
				continue;

			for (int i = 0; i < meshes.Count; i++)
			{
				MeshInstance3D mesh = meshes[i];
				if (mesh != null && GodotObject.IsInstanceValid(mesh)
					&& (node == mesh || node.IsAncestorOf(mesh)))
				{
					_ownerGlobalBounds.Remove(owner);
					break;
				}
			}
		}
	}

	public static void RegisterPickableSubtree(Node3D ownerEntityNode)
	{
		if (ownerEntityNode == null)
			return;

		PruneInvalidPickables(ownerEntityNode);
		_pickOwners[ownerEntityNode] = ownerEntityNode;
		RegisterPickableRecursive(ownerEntityNode, ownerEntityNode);
	}

	/// <summary>Registers pickables under a preview subtree without touching sibling meshes on the owner entity.</summary>
	public static void RegisterPickablePreviewSubtree(FunctionEntityPreview preview, Node3D ownerEntityNode)
	{
		if (preview == null || ownerEntityNode == null)
			return;

		PruneInvalidPickables(ownerEntityNode);
		RegisterPickableRecursive(preview, ownerEntityNode);
	}

	/// <summary>Removes pickables registered from a preview subtree only.</summary>
	public static void UnregisterPickablePreviewSubtree(FunctionEntityPreview preview, Node3D ownerEntityNode)
	{
		if (preview == null || ownerEntityNode == null)
			return;

		UnregisterPickableRecursive(preview, ownerEntityNode);

		if (_pickablesByOwner.TryGetValue(ownerEntityNode, out List<MeshInstance3D> meshes)
			&& meshes.Count == 0)
		{
			_pickablesByOwner.Remove(ownerEntityNode);
			_ownerGlobalBounds.Remove(ownerEntityNode);
		}

		_scopedPickablesDirty = true;
	}

	private static void PruneInvalidPickables(Node3D ownerEntityNode)
	{
		if (!_pickablesByOwner.TryGetValue(ownerEntityNode, out List<MeshInstance3D> meshes))
			return;

		for (int i = meshes.Count - 1; i >= 0; i--)
		{
			MeshInstance3D mesh = meshes[i];
			if (mesh != null && GodotObject.IsInstanceValid(mesh))
				continue;

			if (mesh != null)
				_registeredPickables.Remove(mesh);

			meshes.RemoveAt(i);
		}

		if (meshes.Count == 0)
		{
			_pickablesByOwner.Remove(ownerEntityNode);
			_ownerGlobalBounds.Remove(ownerEntityNode);
			_scopedPickablesDirty = true;
		}
	}

	public static void RegisterPickableMesh(MeshInstance3D meshInstance, Node3D ownerNode)
	{
		if (meshInstance == null || ownerNode == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (meshInstance.IsInGroup(WireframeOverlayGroupName))
			return;

		Mesh mesh = meshInstance.Mesh;
		if (mesh == null || mesh.GetSurfaceCount() == 0)
			return;

		if (!_registeredPickables.Add(meshInstance))
			return;

		if (!LevelViewerCompositeFocus.IsMeshVisuallyDimmed(meshInstance))
		{
			if (!meshInstance.IsInGroup(PickableGroupName))
				meshInstance.AddToGroup(PickableGroupName);
		}
		_pickOwners[meshInstance] = ownerNode;

		if (!_pickablesByOwner.TryGetValue(ownerNode, out List<MeshInstance3D> meshes))
		{
			meshes = new List<MeshInstance3D>();
			_pickablesByOwner.Add(ownerNode, meshes);
		}

		meshes.Add(meshInstance);
		_ownerGlobalBounds.Remove(ownerNode);
		_scopedPickablesDirty = true;
	}

	/// <summary>
	/// Which entity node a pickable mesh belongs to. This was node metadata, but a metadata store
	/// is allocated per node and a level registers tens of thousands of meshes.
	/// </summary>
	private static readonly Dictionary<GodotObject, Node3D> _pickOwners = new Dictionary<GodotObject, Node3D>();

	public static bool TryGetPickOwner(GodotObject pickable, out Node3D ownerNode)
	{
		if (pickable != null)
			return _pickOwners.TryGetValue(pickable, out ownerNode);

		ownerNode = null;
		return false;
	}

	private static void RegisterPickableRecursive(Node node, Node3D ownerEntityNode)
	{
		if (node.IsInGroup(WireframeOverlayGroup) || node.IsInGroup(SceneFilterGroup))
			goto children;

		if (node is MeshInstance3D meshInstance)
			RegisterPickableMesh(meshInstance, ownerEntityNode);
		else if (node is VisualInstance3D visual)
			RegisterPickableVisual(visual, ownerEntityNode);

		children:
		foreach (Node child in node.GetChildren())
			RegisterPickableRecursive(child, ownerEntityNode);
	}

	private static void UnregisterPickableRecursive(Node node, Node3D ownerEntityNode)
	{
		if (node is MeshInstance3D meshInstance)
			UnregisterPickableMesh(meshInstance, ownerEntityNode);
		else if (node is VisualInstance3D visual)
			UnregisterPickableVisual(visual);

		foreach (Node child in node.GetChildren())
			UnregisterPickableRecursive(child, ownerEntityNode);
	}

	private static void RegisterPickableVisual(VisualInstance3D visual, Node3D ownerEntityNode)
	{
		if (visual == null || visual.IsInGroup(WireframeOverlayGroup))
			return;

		Aabb bounds = visual.GetAabb();
		if (bounds.Size.LengthSquared() <= RayEpsilon)
			return;

		if (!visual.IsInGroup(PickableGroup))
			visual.AddToGroup(PickableGroup);
		_pickOwners[visual] = ownerEntityNode;
	}

	private static void UnregisterPickableVisual(VisualInstance3D visual)
	{
		if (visual == null || !GodotObject.IsInstanceValid(visual))
			return;

		if (visual.IsInGroup(PickableGroup))
			visual.RemoveFromGroup(PickableGroup);
		_pickOwners.Remove(visual);
	}

	public static void UnregisterPickableMesh(MeshInstance3D meshInstance, Node3D ownerEntityNode)
	{
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (meshInstance.IsInGroup(PickableGroup))
			meshInstance.RemoveFromGroup(PickableGroup);
		_pickOwners.Remove(meshInstance);
		_registeredPickables.Remove(meshInstance);

		if (ownerEntityNode != null
			&& _pickablesByOwner.TryGetValue(ownerEntityNode, out List<MeshInstance3D> meshes)
			&& meshes.Remove(meshInstance))
		{
			//The owner just got smaller, so its cached bounds would keep claiming space it no longer has
			_ownerGlobalBounds.Remove(ownerEntityNode);
			_scopedPickablesDirty = true;
		}
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

			if (!LevelViewerCompositeFocus.IsPickOwnerInScope(owner, commands))
				continue;

			if (IsOwnerSuppressed(owner))
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

				Aabb global = PreviewVisualUtility.IsIconBillboardMaterial(mesh.MaterialOverride ?? mesh.GetActiveMaterial(0))
					? PreviewVisualUtility.GetIconBillboardPickBounds(mesh)
					: mesh.GlobalTransform * local;
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
		Camera3D camera,
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

			if (!TryRayIntersectMeshInstance(meshInstance, camera, origin, direction, out float distance))
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
			TryPickOwner(_scopedPickOwners[i], camera, origin, direction, ref best);

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

	/// <summary>
	/// Uses mesh pick-owner metadata when present so selection paths match registered geometry.
	/// </summary>
	public static Node3D ResolvePickOwnerEntityNode(Node hitNode, IReadOnlyDictionary<Node3D, Entity> nodeEntities)
	{
		if (hitNode == null || nodeEntities == null)
			return null;

		Node3D owner;
		if (hitNode is MeshInstance3D mesh && TryGetPickOwner(mesh, out owner))
		{
			if (owner is Node3D owner3D && GodotObject.IsInstanceValid(owner3D))
			{
				Node3D resolved = ResolveNearestEntityNode(owner3D, nodeEntities);
				if (resolved != null)
					return resolved;

				return owner3D;
			}
		}

		return ResolveNearestEntityNode(hitNode, nodeEntities);
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
			uint ownerCompositeId;
			if (!AlienScene.TryGetOwnerCompositeId(chain[i], out ownerCompositeId))
				return null;

			compositeIds.Add(ownerCompositeId);
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
	/// How many composite levels below the active view exist on the pick chain (0 = pick stays in active composite).
	/// </summary>
	public static int GetDeepSelectMaxDepth(
		SelectionTarget target,
		uint activeCompositeId,
		uint[] instanceEntityPath,
		Commands commands = null)
	{
		if (!TryGetAliasHierarchyStartIndex(target, activeCompositeId, instanceEntityPath, commands, out int start))
			return 0;

		return CountChildCompositeSegments(target, activeCompositeId, start);
	}

	/// <summary>
	/// Alias hierarchy for deep select at a composite depth below the active view (1 = first child composite).
	/// </summary>
	public static bool TryBuildDeepSelectAliasHierarchyPath(
		SelectionTarget target,
		uint activeCompositeId,
		uint[] instanceEntityPath,
		int depthLevel,
		out ShortGuid[] hierarchy,
		Commands commands = null)
	{
		hierarchy = null;
		if (depthLevel <= 0)
			return false;

		if (!TryGetAliasHierarchyStartIndex(target, activeCompositeId, instanceEntityPath, commands, out int start))
			return false;

		if (!TryFindDeepSelectSegmentEndIndex(target, activeCompositeId, start, depthLevel, out int deepestIndex))
			return false;

		int count = deepestIndex - start + 1;
		if (count <= 0)
			return false;

		hierarchy = new ShortGuid[count];
		for (int i = 0; i < count; i++)
			hierarchy[i] = new ShortGuid(target.EntityIds[start + i]);

		return true;
	}

	private static int CountChildCompositeSegments(SelectionTarget target, uint activeCompositeId, int start)
	{
		int segmentCount = 0;
		int i = start;
		while (i < target.EntityIds.Count)
		{
			while (i < target.EntityIds.Count && target.CompositeIds[i] == activeCompositeId)
				i++;

			if (i >= target.EntityIds.Count)
				break;

			uint segmentCompositeId = target.CompositeIds[i];
			while (i < target.EntityIds.Count && target.CompositeIds[i] == segmentCompositeId)
				i++;

			segmentCount++;
		}

		return segmentCount;
	}

	private static bool TryFindDeepSelectSegmentEndIndex(
		SelectionTarget target,
		uint activeCompositeId,
		int start,
		int depthLevel,
		out int deepestIndex)
	{
		deepestIndex = -1;
		int segmentCount = CountChildCompositeSegments(target, activeCompositeId, start);
		if (segmentCount == 0)
			return false;

		int clampedDepth = Mathf.Clamp(depthLevel, 1, segmentCount);
		int segmentIndex = 0;
		int i = start;

		while (i < target.EntityIds.Count && segmentIndex < clampedDepth)
		{
			while (i < target.EntityIds.Count && target.CompositeIds[i] == activeCompositeId)
				i++;

			if (i >= target.EntityIds.Count)
				return false;

			uint segmentCompositeId = target.CompositeIds[i];
			int segmentEnd = i;
			while (i < target.EntityIds.Count && target.CompositeIds[i] == segmentCompositeId)
			{
				segmentEnd = i;
				i++;
			}

			segmentIndex++;
			if (segmentIndex == clampedDepth)
				deepestIndex = segmentEnd;
		}

		return deepestIndex >= start;
	}

	/// <summary>
	/// Builds an alias hierarchy path relative to <paramref name="ownerCompositeId"/>.
	/// </summary>
	public static bool TryBuildAliasHierarchyPath(
		SelectionTarget target,
		uint ownerCompositeId,
		uint[] instanceEntityPath,
		out ShortGuid[] hierarchy,
		Commands commands = null)
	{
		hierarchy = null;
		if (!TryGetAliasHierarchyStartIndex(target, ownerCompositeId, instanceEntityPath, commands, out int start))
			return false;

		int count = target.EntityIds.Count - start;
		if (count <= 0)
			return false;

		hierarchy = new ShortGuid[count];
		for (int i = 0; i < count; i++)
			hierarchy[i] = new ShortGuid(target.EntityIds[start + i]);

		return true;
	}

	private static bool TryGetAliasHierarchyStartIndex(
		SelectionTarget target,
		uint ownerCompositeId,
		uint[] instanceEntityPath,
		Commands commands,
		out int start)
	{
		start = -1;
		if (ownerCompositeId == 0 || target.EntityIds == null || target.CompositeIds == null || target.EntityIds.Count == 0)
			return false;

		if (TryMatchInstancePathPrefix(target, instanceEntityPath, out start)
			|| TryMatchInstancePathPrefix(
				target,
				PreviewVisibilitySettings.CompositeFocusInstancePath ?? System.Array.Empty<uint>(),
				out start))
		{
			return start >= 0 && start < target.EntityIds.Count;
		}

		start = FindLastCompositeIndex(target, ownerCompositeId);
		if (start >= 0)
			return true;

		if (commands != null
			&& target.CompositeIds.Count > 0
			&& IsCompositeDefinitionReachableFromOwner(
				commands.GetComposite(new ShortGuid(ownerCompositeId)),
				target.CompositeIds[0],
				commands,
				new HashSet<uint>()))
		{
			start = 0;
			return true;
		}

		return false;
	}

	private static bool TryMatchInstancePathPrefix(SelectionTarget target, uint[] instanceEntityPath, out int start)
	{
		start = -1;
		if (instanceEntityPath == null || instanceEntityPath.Length == 0)
			return false;

		if (instanceEntityPath.Length > target.EntityIds.Count)
			return false;

		for (int i = 0; i < instanceEntityPath.Length; i++)
		{
			if (target.EntityIds[i] != instanceEntityPath[i])
				return false;
		}

		start = instanceEntityPath.Length;
		return true;
	}

	private static int FindLastCompositeIndex(SelectionTarget target, uint compositeId)
	{
		int last = -1;
		for (int i = 0; i < target.CompositeIds.Count; i++)
		{
			if (target.CompositeIds[i] == compositeId)
				last = i;
		}

		return last;
	}

	private static bool IsCompositeDefinitionReachableFromOwner(
		Composite ownerComposite,
		uint candidateCompositeId,
		Commands commands,
		HashSet<uint> visited)
	{
		if (ownerComposite == null || commands == null || candidateCompositeId == 0)
			return false;

		if (!visited.Add(ownerComposite.shortGUID.AsUInt32))
			return false;

		foreach (Entity entity in ownerComposite.functions)
		{
			if (entity is not FunctionEntity function || function.function.IsFunctionType)
				continue;

			Composite child = commands.GetComposite(function.function);
			if (child == null)
				continue;

			if (child.shortGUID.AsUInt32 == candidateCompositeId)
				return true;

			if (IsCompositeDefinitionReachableFromOwner(child, candidateCompositeId, commands, visited))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Builds an OpenCAGE selection path to an alias in <paramref name="ownerCompositeId"/>,
	/// prefixing the active instance drill path when the editor is inside nested composites.
	/// </summary>
	public static bool TryBuildAliasSelectionPath(
		SelectionTarget target,
		uint ownerCompositeId,
		uint aliasEntityId,
		IReadOnlyList<uint> syncedPathComposites,
		out List<uint> pathEntities,
		out List<uint> pathComposites)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();

		if (ownerCompositeId == 0 || aliasEntityId == 0)
			return false;

		uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
		for (int i = 0; i < instancePath.Length; i++)
		{
			pathEntities.Add(instancePath[i]);
			if (syncedPathComposites != null && i < syncedPathComposites.Count)
				pathComposites.Add(syncedPathComposites[i]);
			else if (target.CompositeIds != null && i < target.CompositeIds.Count)
				pathComposites.Add(target.CompositeIds[i]);
			else
				pathComposites.Add(ownerCompositeId);
		}

		pathEntities.Add(aliasEntityId);
		pathComposites.Add(ownerCompositeId);

		return pathEntities.Count > 0 && pathComposites.Count == pathEntities.Count;
	}

	public static bool TryFindAliasWithPath(Composite composite, ShortGuid[] hierarchy, out AliasEntity alias)
	{
		alias = null;
		if (composite == null || hierarchy == null || hierarchy.Length == 0)
			return false;

		EntityPath desiredPath = new EntityPath(hierarchy);
		foreach (AliasEntity candidate in composite.aliases)
		{
			if (candidate?.alias == desiredPath)
			{
				alias = candidate;
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Selects the deepest selectable entity on the full pick chain (deep-select LMB).
	/// Falls back to drilling into the innermost composite instance when only composite instances remain on the chain.
	/// </summary>
	public static bool TryBuildDeepSelectEntityPath(
		SelectionTarget target,
		Commands commands,
		out List<uint> pathEntities,
		out List<uint> pathComposites,
		out bool entitySelected)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();
		entitySelected = false;

		if (commands == null || target.EntityIds == null || target.EntityIds.Count == 0)
			return false;

		int lastIndex = target.EntityIds.Count - 1;
		if (TryResolveChainEntity(commands, target, lastIndex, out Entity leafEntity)
			&& !IsCompositeInstanceEntity(leafEntity, commands))
		{
			return TryBuildSelectionPath(
				target,
				target.EntityIds.Count,
				commands,
				out pathEntities,
				out pathComposites,
				out entitySelected)
				&& entitySelected;
		}

		entitySelected = false;
		return TryBuildDeepDrillPath(target, commands, out pathEntities, out pathComposites);
	}

	/// <summary>
	/// Drills through every composite instance on the pick chain down to the picked entity's place in the hierarchy (deep-select Ctrl+MMB).
	/// </summary>
	public static bool TryBuildDeepDrillPath(
		SelectionTarget target,
		Commands commands,
		out List<uint> pathEntities,
		out List<uint> pathComposites)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();

		if (commands == null || target.EntityIds == null || target.EntityIds.Count == 0)
			return false;

		int lastIndex = target.EntityIds.Count - 1;
		int drillSteps;
		if (TryResolveChainEntity(commands, target, lastIndex, out Entity leafEntity)
			&& IsCompositeInstanceEntity(leafEntity, commands))
		{
			drillSteps = target.EntityIds.Count;
		}
		else if (lastIndex == 0)
		{
			return false;
		}
		else
		{
			drillSteps = lastIndex;
			if (!TryResolveChainEntity(commands, target, drillSteps - 1, out Entity enterThrough)
				|| !IsCompositeInstanceEntity(enterThrough, commands))
			{
				return false;
			}
		}

		return TryBuildSelectionPath(target, drillSteps, commands, out pathEntities, out pathComposites, out bool entitySelected)
			&& !entitySelected;
	}

	/// <summary>
	/// Steps into the composite at progressive deep-select depth (Ctrl+MMB after repeated LMB clicks).
	/// </summary>
	public static bool TryBuildProgressiveDeepDrillPath(
		SelectionTarget target,
		uint activeCompositeId,
		uint[] instanceEntityPath,
		int depthLevel,
		Commands commands,
		out List<uint> pathEntities,
		out List<uint> pathComposites)
	{
		pathEntities = new List<uint>();
		pathComposites = new List<uint>();

		if (commands == null || depthLevel <= 0)
			return false;

		if (!TryFindDeepSelectDrillEnterIndex(
				target,
				activeCompositeId,
				instanceEntityPath,
				depthLevel,
				commands,
				out int enterIndex))
		{
			return false;
		}

		int steps = enterIndex + 1;
		return TryBuildSelectionPath(target, steps, commands, out pathEntities, out pathComposites, out bool entitySelected)
			&& !entitySelected;
	}

	private static bool TryFindDeepSelectDrillEnterIndex(
		SelectionTarget target,
		uint activeCompositeId,
		uint[] instanceEntityPath,
		int depthLevel,
		Commands commands,
		out int enterIndex)
	{
		enterIndex = -1;
		if (depthLevel <= 0 || commands == null)
			return false;

		if (!TryGetAliasHierarchyStartIndex(target, activeCompositeId, instanceEntityPath, commands, out int start))
			return false;

		int segmentCount = CountChildCompositeSegments(target, activeCompositeId, start);
		if (segmentCount == 0 || depthLevel > segmentCount)
			return false;

		int i = start;
		for (int depth = 1; depth <= depthLevel; depth++)
		{
			uint regionId = depth == 1 ? activeCompositeId : target.CompositeIds[i];
			int lastCompositeInstance = -1;

			while (i < target.EntityIds.Count && target.CompositeIds[i] == regionId)
			{
				if (TryResolveChainEntity(commands, target, i, out Entity entity)
					&& IsCompositeInstanceEntity(entity, commands))
				{
					lastCompositeInstance = i;
				}

				i++;
			}

			if (lastCompositeInstance < 0)
				return false;

			if (depth == depthLevel)
			{
				enterIndex = lastCompositeInstance;
				return true;
			}

			if (i >= target.EntityIds.Count)
				return false;
		}

		return false;
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
	/// Deepest non-composite-instance entity on the pick chain that lives in <paramref name="enteredCompositeId"/>.
	/// </summary>
	public static uint TryFindPreservedEntityInComposite(
		SelectionTarget target,
		uint enteredCompositeId,
		Commands commands)
	{
		if (commands == null || enteredCompositeId == 0
			|| target.EntityIds == null || target.CompositeIds == null)
		{
			return 0;
		}

		Composite enteredComposite = commands.GetComposite(new ShortGuid(enteredCompositeId));
		if (enteredComposite == null)
			return 0;

		for (int i = target.EntityIds.Count - 1; i >= 0; i--)
		{
			if (target.CompositeIds[i] != enteredCompositeId)
				continue;

			uint entityId = target.EntityIds[i];
			Entity entity = enteredComposite.GetEntityByID(new ShortGuid(entityId));
			if (entity == null || IsCompositeInstanceEntity(entity, commands))
				continue;

			return entityId;
		}

		return 0;
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
		Camera3D camera,
		Vector3 origin,
		Vector3 direction,
		out float distance)
	{
		distance = 0f;
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return false;

		Material material = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
		if (PreviewVisualUtility.IsIconBillboardMaterial(material))
			return PreviewVisualUtility.TryRayIntersectIconBillboard(meshInstance, camera, origin, direction, out distance);

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
		PickFaceMode faceMode = GetMeshFaceMode(meshInstance);
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
						faceMode,
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
						faceMode,
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
		PickFaceMode faceMode,
		ref float closestLocalT,
		ref bool anyHit)
	{
		if (!TryRayIntersectTriangle(localOrigin, localDirection, a, b, c, faceMode, out float localT))
			return;

		if (localT >= closestLocalT)
			return;

		closestLocalT = localT;
		anyHit = true;
	}

	/// <summary>
	/// Which faces of a mesh the ray is allowed to hit, taken from how its material draws them.
	/// A face that isn't drawn isn't there to be clicked - an occlusion hull is drawn back-face-only,
	/// so testing its near half would let an invisible surface swallow clicks meant for the geometry
	/// on screen inside it.
	/// </summary>
	private static PickFaceMode GetMeshFaceMode(MeshInstance3D meshInstance)
	{
		Material material = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
		if (AlienSceneMaterials.IsBackFaceOnlyMaterial(material))
			return PickFaceMode.BackOnly;

		if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null)
			return PickFaceMode.FrontOnly;

		string path = shaderMaterial.Shader.ResourcePath;
		if (string.IsNullOrEmpty(path))
			return PickFaceMode.FrontOnly;

		bool doubleSided = path.Contains("double_sided")
			|| path.Contains("scene_filter_shaded")
			|| path.Contains("preview_icon_billboard")
			|| path.Contains("preview_overlay_line")
			|| path.Contains("preview_opaque")
			|| path.Contains("preview_transparent");

		return doubleSided ? PickFaceMode.DoubleSided : PickFaceMode.FrontOnly;
	}

	private static bool TryRayIntersectTriangle(
		Vector3 origin,
		Vector3 direction,
		Vector3 v0,
		Vector3 v1,
		Vector3 v2,
		PickFaceMode faceMode,
		out float t)
	{
		t = 0f;

		Vector3 edge1 = v1 - v0;
		Vector3 edge2 = v2 - v0;
		Vector3 pvec = direction.Cross(edge2);
		float det = edge1.Dot(pvec);
		if (Mathf.Abs(det) < RayEpsilon)
			return false;

		//Front faces come out negative here, so the sign is what tells the two halves of a hull apart
		if (faceMode == PickFaceMode.FrontOnly && det > 0f)
			return false;
		if (faceMode == PickFaceMode.BackOnly && det < 0f)
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
