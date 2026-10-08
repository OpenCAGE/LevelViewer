using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Greys out and disables picking for geometry outside the composite OpenCAGE is currently viewing.
/// </summary>
public static class LevelViewerCompositeFocus
{
	//Shader parameter names as StringNames, made once: a literal is a temporary whose native handle the
	//binding gives the engine with nothing keeping it alive - see LevelViewerPick. Group names likewise.
	private static readonly StringName BaseGreyParam = new StringName("base_grey");
	private static readonly StringName OpacityParam = new StringName("opacity");
	private static readonly StringName AlbedoColorParam = new StringName("albedo_color");
	private static readonly StringName DiffuseTintParam = new StringName("diffuse_tint");
	private static readonly Color DimBaseGrey = new(0.09f, 0.09f, 0.10f, 1f);
	private const int TransparentRenderPriority = 1;
	private static Shader _dimmedShader;
	private static Shader _dimmedShaderDoubleSided;
	private static Shader _dimmedTransparentShader;
	private static Shader _dimmedTransparentShaderDoubleSided;
	private static ShaderMaterial _cachedDimmedOpaque;
	private static ShaderMaterial _cachedDimmedOpaqueDoubleSided;
	private static ShaderMaterial _cachedDimmedTransparent;
	private static ShaderMaterial _cachedDimmedTransparentDoubleSided;
	private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();
	private static readonly Dictionary<MeshInstance3D, bool> _meshDimmedState = new();
	//Which of the four shared dimmed materials a source maps to, remembered without keeping the source alive: an override
	//material replaced by an edit (each tick of a colour drag on a greyed-out placement) was held here until the next populate
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Material, Material> _dimmedMaterialBySource = new();
	private static readonly Dictionary<Node3D, uint[]> _ownerEntityChainCache = new();
	private static readonly List<uint> _entityChainBuildBuffer = new();
	private static readonly HashSet<uint> _compositesInScope = new();
	private static uint _scopeCacheActiveCompositeId;
	/* The active composite the last Refresh worked the grey-out out for; 0 when none has since the scene was built or the
	   grey-out cleared. Kept apart from the scope cache's id: picking and the reapplies rebuild that cache on their own, and
	   one doing it between a navigation and its Refresh would make the step read as no change of composite. */
	private static uint _appliedActiveCompositeId;
	private static uint[] _lastFocusInstancePath = Array.Empty<uint>();
	private static Node3D _scopeAnchorNode;
	private static IReadOnlyDictionary<Node3D, Entity> _scopeNodeEntities;
	private static Node _scopeContentRoot;
	//Show Zones has the level's materials: nothing is greyed out, though the scope is still kept (see Refresh)
	private static bool _dimmingStoodDown;
	/* Owners a pass left as they were because they were selected. Their grey-out was never worked out for the scope they
	   are in now; ReapplyToOwnersLeftBySelection does it once the selection has moved off them. */
	private static readonly HashSet<Node3D> _ownersPassedOverForSelection = new();
	public static bool HasActiveComposite => PreviewVisibilitySettings.ActiveCompositeId != 0;
	//Zones are (or were) on and the grey-out is off for it; a zones-off refresh has to bring it back
	public static bool DimmingStoodDown => _dimmingStoodDown;

	public static void SetScopeEvaluationContext(IReadOnlyDictionary<Node3D, Entity> nodeEntities, Node contentRoot)
	{
		_scopeNodeEntities = nodeEntities;
		_scopeContentRoot = contentRoot;
	}

	public static void ClearScopeEvaluationContext()
	{
		_scopeNodeEntities = null;
		_scopeContentRoot = null;
	}

	public static void RebuildScopeCache(Commands commands)
	{
		if (!HasActiveComposite || commands == null)
		{
			_compositesInScope.Clear();
			_scopeCacheActiveCompositeId = 0;
			return;
		}

		uint activeId = PreviewVisibilitySettings.ActiveCompositeId;

		// Instance drill path only affects the anchor node, not which composites are in scope.
		if (_scopeCacheActiveCompositeId == activeId && _compositesInScope.Count > 0)
			return;

		_compositesInScope.Clear();
		_scopeCacheActiveCompositeId = activeId;
		_compositesInScope.Add(activeId);

		Composite active = commands.GetComposite(new ShortGuid(activeId));
		if (active == null)
			return;

		Queue<Composite> pending = new Queue<Composite>();
		pending.Enqueue(active);
		HashSet<uint> visited = new HashSet<uint> { activeId };

		//Commands.GetComposite scans every composite: once per instance entity in scope it was most of a second on TECH_Hub
		Dictionary<ShortGuid, Composite> compositesById = new Dictionary<ShortGuid, Composite>(commands.Entries.Count);
		foreach (Composite entry in commands.Entries)
			if (entry != null && !compositesById.ContainsKey(entry.shortGUID))
				compositesById[entry.shortGUID] = entry;

		while (pending.Count > 0)
		{
			Composite composite = pending.Dequeue();
			foreach (Entity entity in composite.functions)
			{
				if (entity.variant != EntityVariant.FUNCTION)
					continue;

				FunctionEntity function = (FunctionEntity)entity;
				if (function.function.IsFunctionType)
					continue;

				compositesById.TryGetValue(function.function, out Composite child);
				if (child == null)
					continue;

				uint childId = child.shortGUID.AsUInt32;
				if (!visited.Add(childId))
					continue;

				_compositesInScope.Add(childId);
				pending.Enqueue(child);
			}
		}
	}

	/// <summary>
	/// The composite tree under the active composite has changed - an instance of a composite that
	/// was not there before (dropped from the browser, pasted, undone back) - so which composites are
	/// in scope is worked out again on the next ask. Without this, everything the new instance
	/// spawned was judged out of scope, and nothing in it could be picked.
	/// </summary>
	public static void InvalidateScopeCache()
	{
		_compositesInScope.Clear();
		//Both, so every reader rebuilds: RebuildScopeCache goes by the set being empty, IsOwnerCompositeInScope by the id
		_scopeCacheActiveCompositeId = 0;
	}

	public static bool IsOwnerCompositeInScope(uint ownerCompositeId, Commands commands)
	{
		if (!HasActiveComposite)
			return true;

		if (ownerCompositeId == 0)
			return false;

		if (_scopeCacheActiveCompositeId != PreviewVisibilitySettings.ActiveCompositeId)
			RebuildScopeCache(commands);

		return _compositesInScope.Contains(ownerCompositeId);
	}

	public static bool IsNodeInScope(Node node, Node contentRoot, Commands commands)
	{
		if (!HasActiveComposite || node == null || commands == null)
			return true;

		if (node is Node3D entityNode && AlienScene.HasOwnerComposite(entityNode))
			return IsPickOwnerInScope(entityNode, commands);

		uint ownerCompositeId = ResolveOwnerCompositeId(node, contentRoot);
		if (!IsOwnerCompositeInScope(ownerCompositeId, commands))
			return false;

		if (node is Node3D node3D
			&& _scopeNodeEntities != null
			&& LevelViewerPick.ResolveNearestEntityNode(node3D, _scopeNodeEntities) is Node3D nearestEntity)
		{
			return IsPickOwnerInScope(nearestEntity, commands);
		}

		return IsUnderScopeAnchor(node);
	}

	/// <summary>Scope test for a registered pick owner (entity node).</summary>
	public static bool IsPickOwnerInScope(Node3D owner, Commands commands)
	{
		if (!HasActiveComposite || owner == null || commands == null)
			return true;

		uint ownerCompositeId;
		if (AlienScene.TryGetOwnerCompositeId(owner, out ownerCompositeId))
		{
			if (!IsOwnerCompositeInScope(ownerCompositeId, commands))
				return false;
		}

		return IsOwnerUnderFocusInstancePath(owner);
	}

	/// <summary>
	/// True when the owner's entity-id chain from the level root starts with the focus instance path.
	/// Distinguishes sibling composite instances that share a blueprint but have different placements.
	/// </summary>
	private static bool IsOwnerUnderFocusInstancePath(Node3D owner)
	{
		uint[] focusPath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		if (focusPath.Length == 0)
			return true;

		if (_scopeNodeEntities != null
			&& _scopeContentRoot != null
			&& TryGetOwnerEntityChain(owner, out uint[] chain))
		{
			if (chain.Length < focusPath.Length)
				return false;

			for (int i = 0; i < focusPath.Length; i++)
			{
				if (chain[i] != focusPath[i])
					return false;
			}

			return true;
		}

		return IsUnderScopeAnchor(owner);
	}

	private static bool IsUnderScopeAnchor(Node node)
	{
		uint[] instancePath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		if (instancePath.Length == 0)
			return true;

		if (_scopeAnchorNode == null || !GodotObject.IsInstanceValid(_scopeAnchorNode))
			return false;

		return node == _scopeAnchorNode || _scopeAnchorNode.IsAncestorOf(node);
	}

	public static void Refresh(
		Node3D sceneRoot,
		Node contentRoot,
		Commands commands,
		Node3D scopeAnchorOverride = null,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities = null,
		uint sceneRootCompositeId = 0)
	{
		if (!HasActiveComposite || sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot) || commands == null)
		{
			Clear();
			return;
		}

		PruneInvalidMeshState();

		_scopeNodeEntities = nodeEntities ?? _scopeNodeEntities;
		_scopeContentRoot = contentRoot;
		uint activeId = PreviewVisibilitySettings.ActiveCompositeId;
		uint[] instancePath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		uint[] previousFocusPath = _lastFocusInstancePath;
		uint previousActiveComposite = _appliedActiveCompositeId;
		bool activeCompositeChanged = previousActiveComposite != 0 && previousActiveComposite != activeId;
		bool focusPathChanged = !PreviewVisibilitySettings.InstancePathsEqual(previousFocusPath, instancePath);
		/* No scope has been applied to the scene on screen yet: it has just been built (a populate - the composite on screen
		   rebuilt, the level read again by a restarted viewer) or the grey-out was taken off all of it (Clear, before a preview
		   batch). An incremental change has nothing to be measured against then. Measured from the empty path Clear leaves,
		   every owner of a composite outside the new scope read as out of scope before as well as after, and was never
		   greyed: stepped down, a rebuild left everything but the other placements of the stepped-into composite bright. */
		bool nothingApplied = previousActiveComposite == 0;

		if (activeCompositeChanged)
			ResetDimStateForScopeChange();

		RebuildScopeCache(commands);
		/* Nothing applied since the scene was built, and with no path there is no path change to go by either: a scene whose
		   root composite lies outside the active composite's scope (the level's root built while the editor shows a composite
		   opened on its own) has everything outside that scope to grey, which the "no change" test below left bright. */
		bool unscopedScene = nothingApplied && instancePath.Length == 0 && sceneRootCompositeId != 0 && !_compositesInScope.Contains(sceneRootCompositeId);
		_appliedActiveCompositeId = activeId;
		_scopeAnchorNode = scopeAnchorOverride ?? ResolveScopeAnchorNode(contentRoot, instancePath);
		_lastFocusInstancePath = (uint[])instancePath.Clone();

		/* Show Zones stands the grey-out down. Both work by replacing MaterialOverride across the whole
		   level, and the dimmed material is a flat grey whatever it replaces - so dimming zone-coloured
		   geometry loses the colour entirely, and the two of them saving and restoring each other's
		   materials is how a mesh ends up stuck in a colour after the overlay is switched off. One
		   owner at a time; AlienScene.RefreshZoneOverlay brings the dimming back when zones go off.

		   Only the dimming stands down. The scope worked out above is kept current regardless, because
		   picking goes by it whether or not anything is greyed out: a click tests every owner against
		   the anchor and the focus instance path, and with those thrown away every owner under a
		   stepped-into instance read as out of scope - nothing could be clicked while zones were on. */
		if (PreviewVisibilitySettings.ShowZones)
		{
			if (!_dimmingStoodDown)
			{
				ResetDimStateForScopeChange();
				_dimmingStoodDown = true;
			}

			LevelViewerPick.InvalidateScopedPickables();
			return;
		}

		//Zones have just gone off: nothing is dimmed and the per-mesh state went with it, so the scope is applied in full
		bool dimmingReturns = _dimmingStoodDown;
		_dimmingStoodDown = false;

		if (dimmingReturns || activeCompositeChanged || focusPathChanged || unscopedScene)
		{
			if (!dimmingReturns
				&& !activeCompositeChanged
				&& !nothingApplied
				&& focusPathChanged
				&& TryApplyIncrementalFocusPathChange(previousFocusPath, instancePath, commands))
			{
			}
			else
			{
				ApplyFocusFromPickRegistry(commands);
			}
		}

		LevelViewerPick.InvalidateScopedPickables();
	}

	/// <summary>
	/// Gives the entities under <paramref name="root"/> (itself included) the grey-out their place in the scope calls for,
	/// as they are now. <see cref="Refresh"/> only works the focus out when the scope changes, so anything that rebuilds or
	/// recolours an entity's meshes in place has to come back here: a ModelReference parameter or resource edit respawns
	/// the entity in EVERY placement of its composite - the greyed-out sibling placements too - and the new meshes arrive
	/// in their own material; a preview recolour writes its colour over the grey. Left alone they stayed bright until the
	/// active composite changed, and an incremental focus change passed them by as "unchanged".
	/// </summary>
	public static void ReapplyToSubtree(Node3D root, Commands commands)
	{
		if (root == null || !GodotObject.IsInstanceValid(root) || !PrepareForReapply(commands))
			return;

		uint[] focusPath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		List<MeshInstance3D> meshes = new List<MeshInstance3D>();
		int dimmed = 0, undimmed = 0, unchanged = 0;
		Stack<Node3D> pending = new Stack<Node3D>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			Node3D owner = pending.Pop();
			if (owner == null || !GodotObject.IsInstanceValid(owner))
				continue;

			//Same rules as ApplyFocusToPickOwners: one decision per registered pick owner; the selection is left alone
			if (LevelViewerSelection.IsUnderSelection(owner))
				_ownersPassedOverForSelection.Add(owner);
			else
				ApplyCurrentFocusToOwner(owner, focusPath, meshes, ref dimmed, ref undimmed, ref unchanged);

			int childCount = owner.GetChildCount();
			for (int i = 0; i < childCount; i++)
			{
				if (owner.GetChild(i) is Node3D child && AlienScene.HasOwnerComposite(child))
					pending.Push(child);
			}
		}
	}

	/// <summary>
	/// The selection has just moved or been cleared: the owners a pass left alone while they were selected get the
	/// grey-out their place in the scope calls for. On a step into another placement the focus is applied BEFORE the new
	/// selection, so whatever was selected in the placement being left - one entity, or a composite instance holding the
	/// whole level - kept its old look. Only the owners actually passed over are judged, so moving the selection off a
	/// large instance without a scope change in between costs nothing.
	/// </summary>
	public static void ReapplyToOwnersLeftBySelection(Commands commands)
	{
		if (_ownersPassedOverForSelection.Count == 0)
			return;

		List<Node3D> released = null;
		foreach (Node3D owner in _ownersPassedOverForSelection)
		{
			if (owner == null || !GodotObject.IsInstanceValid(owner) || !LevelViewerSelection.IsUnderSelection(owner))
				(released ??= new List<Node3D>()).Add(owner);
		}
		if (released == null)
			return;

		for (int i = 0; i < released.Count; i++)
			_ownersPassedOverForSelection.Remove(released[i]);
		//Stood down or not worked out yet: the full pass that brings the grey-out back covers these too
		if (!PrepareForReapply(commands))
			return;

		uint[] focusPath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		List<MeshInstance3D> meshes = new List<MeshInstance3D>();
		int dimmed = 0, undimmed = 0, unchanged = 0;
		for (int i = 0; i < released.Count; i++)
		{
			Node3D owner = released[i];
			if (owner != null && GodotObject.IsInstanceValid(owner))
				ApplyCurrentFocusToOwner(owner, focusPath, meshes, ref dimmed, ref undimmed, ref unchanged);
		}
	}

	/// <summary>Whether a reapply can judge against the current scope (rebuilding its cache when that was invalidated).</summary>
	private static bool PrepareForReapply(Commands commands)
	{
		if (!HasActiveComposite || _dimmingStoodDown || commands == null)
			return false;
		//Nothing has been worked out since the scene was (re)built or cleared: the Refresh that does it covers this too
		if (_scopeContentRoot == null || _scopeNodeEntities == null)
			return false;
		//The scope cache may have been invalidated (a composite instance added): judging against an empty set would
		//grey the active composite's own placements
		if (_scopeCacheActiveCompositeId != PreviewVisibilitySettings.ActiveCompositeId || _compositesInScope.Count == 0)
			RebuildScopeCache(commands);
		return true;
	}

	/// <summary>One owner's grey-out for the scope as it is now (the decision ApplyFocusToPickOwners makes).</summary>
	private static void ApplyCurrentFocusToOwner(
		Node3D owner,
		uint[] focusPath,
		List<MeshInstance3D> meshes,
		ref int dimmed,
		ref int undimmed,
		ref int unchanged)
	{
		meshes.Clear();
		if (!LevelViewerPick.TryCopyPickMeshesForOwner(owner, meshes))
			return;

		bool inScope = IsOwnerCompositeInScopeForOwner(owner) && MatchesFocusInstancePath(owner, focusPath);
		//In scope and never greyed (a fresh respawn, mostly): nothing to undo, and no state worth keeping for it -
		//the per-mesh state is only pruned on a scope change, so a slider drag would pile entries up
		if (inScope)
			meshes.RemoveAll(mesh => !_meshDimmedState.ContainsKey(mesh) && !_savedMaterialOverrides.ContainsKey(mesh));
		ApplyOwnerMeshFocusState(meshes, !inScope, ref dimmed, ref undimmed, ref unchanged);
	}

	/// <summary>The grey this class put on is what the mesh is drawn with now (nothing has written over it since).</summary>
	private static bool IsShowingDimmedMaterial(MeshInstance3D mesh)
	{
		Material current = mesh.MaterialOverride;
		return current != null
			&& (current == _cachedDimmedOpaque
				|| current == _cachedDimmedOpaqueDoubleSided
				|| current == _cachedDimmedTransparent
				|| current == _cachedDimmedTransparentDoubleSided);
	}

	/// <summary>Clears all dimmed materials/pick state before re-applying a new drill scope.</summary>
	private static void ResetDimStateForScopeChange()
	{
		RestoreAllDimmedMeshes();
		_meshDimmedState.Clear();
		//Everything is judged again by the full pass that follows (or when zones go off), the selection noted afresh
		_ownersPassedOverForSelection.Clear();
	}

	/// <summary>
	/// A node about to be freed (a removed subtree, a refreshed preview's old meshes): its per-mesh and per-owner state
	/// goes with it. Only a scope change cleared these, and the state of a mesh never greyed was never pruned at all - an
	/// instance deleted in place left an entry for each of its meshes. Never for a node that stays: a greyed mesh would
	/// lose the material it goes back to.
	/// </summary>
	public static void ForgetNode(Node3D node)
	{
		if (node == null)
			return;

		if (node is MeshInstance3D mesh)
		{
			_meshDimmedState.Remove(mesh);
			_savedMaterialOverrides.Remove(mesh);
		}
		_ownerEntityChainCache.Remove(node);
		_ownersPassedOverForSelection.Remove(node);
	}

	/// <summary>True when composite focus has this mesh greyed out (used to avoid re-pickable registration).</summary>
	public static bool IsMeshVisuallyDimmed(MeshInstance3D mesh)
	{
		return mesh != null && _savedMaterialOverrides.ContainsKey(mesh);
	}

	public static void Clear()
	{
		RestoreAllDimmedMeshes();
		_compositesInScope.Clear();
		_scopeCacheActiveCompositeId = 0;
		_appliedActiveCompositeId = 0;
		_lastFocusInstancePath = Array.Empty<uint>();
		_scopeAnchorNode = null;
		_dimmingStoodDown = false;
		_meshDimmedState.Clear();
		_dimmedMaterialBySource.Clear();
		_ownerEntityChainCache.Clear();
		_ownersPassedOverForSelection.Clear();
		ClearScopeEvaluationContext();
	}

	private static void RestoreAllDimmedMeshes()
	{
		bool wireframeEnabled = ModelReferenceRenderSettings.WireframeEnabled;
		foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
		{
			if (entry.Key == null || !GodotObject.IsInstanceValid(entry.Key))
				continue;

			try
			{
				entry.Key.MaterialOverride = entry.Value;
				if (wireframeEnabled)
					SetWireframeOverlayVisible(entry.Key, true);
			}
			catch (Exception)
			{
			}
		}

		_savedMaterialOverrides.Clear();
	}

	private static void PruneInvalidMeshState()
	{
		List<MeshInstance3D> staleMeshes = null;
		foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
		{
			if (entry.Key == null || !GodotObject.IsInstanceValid(entry.Key))
				(staleMeshes ??= new List<MeshInstance3D>()).Add(entry.Key);
		}

		if (staleMeshes != null)
		{
			for (int i = 0; i < staleMeshes.Count; i++)
			{
				_savedMaterialOverrides.Remove(staleMeshes[i]);
				_meshDimmedState.Remove(staleMeshes[i]);
			}
		}

		List<Node3D> staleOwners = null;
		foreach (KeyValuePair<Node3D, uint[]> entry in _ownerEntityChainCache)
		{
			if (entry.Key == null || !GodotObject.IsInstanceValid(entry.Key))
				(staleOwners ??= new List<Node3D>()).Add(entry.Key);
		}

		if (staleOwners != null)
		{
			for (int i = 0; i < staleOwners.Count; i++)
				_ownerEntityChainCache.Remove(staleOwners[i]);
		}

		_ownersPassedOverForSelection.RemoveWhere(owner => owner == null || !GodotObject.IsInstanceValid(owner));
	}

	private static void ApplyFocusFromPickRegistry(Commands commands)
	{
		_ownerEntityChainCache.Clear();
		ApplyFocusToPickOwners(commands, null, null);
	}

	private static bool TryApplyIncrementalFocusPathChange(
		uint[] previousPath,
		uint[] newPath,
		Commands commands)
	{
		previousPath ??= Array.Empty<uint>();
		newPath ??= Array.Empty<uint>();

		bool extension = IsStrictPathPrefix(previousPath, newPath)
			|| (previousPath.Length == 0 && newPath.Length > 0);
		bool retraction = IsStrictPathPrefix(newPath, previousPath) && newPath.Length < previousPath.Length;
		if (!extension && !retraction)
			return false;

		ApplyFocusToPickOwners(commands, previousPath, newPath);
		return true;
	}

	private static void ApplyFocusToPickOwners(
		Commands commands,
		uint[] previousFocusPath,
		uint[] newFocusPath)
	{
		bool incremental = previousFocusPath != null && newFocusPath != null;
		uint[] evaluatePath = incremental ? newFocusPath : PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		previousFocusPath ??= Array.Empty<uint>();

		int meshesDimmed = 0;
		int meshesUndimmed = 0;
		int meshesUnchanged = 0;

		_ownerEntityChainCache.Clear();

		LevelViewerPick.ForEachPickOwner((owner, meshes) =>
		{
			if (LevelViewerSelection.IsUnderSelection(owner))
			{
				_ownersPassedOverForSelection.Add(owner);
				return;
			}

			bool compositeInScope = IsOwnerCompositeInScopeForOwner(owner);
			bool nowInScope = compositeInScope && MatchesFocusInstancePath(owner, evaluatePath);

			if (incremental)
			{
				bool wasInScope = compositeInScope && MatchesFocusInstancePath(owner, previousFocusPath);
				if (wasInScope == nowInScope)
					return;
			}

			ApplyOwnerMeshFocusState(meshes, !nowInScope, ref meshesDimmed, ref meshesUndimmed, ref meshesUnchanged);
		});
	}

	private static bool IsOwnerCompositeInScopeForOwner(Node3D owner)
	{
		uint ownerCompositeId;
		if (!AlienScene.TryGetOwnerCompositeId(owner, out ownerCompositeId))
			return true;

		return _compositesInScope.Contains(ownerCompositeId);
	}

	private static bool MatchesFocusInstancePath(Node3D owner, uint[] focusPath)
	{
		if (focusPath.Length == 0)
			return true;

		if (!TryGetOwnerEntityChain(owner, out uint[] chain))
			return IsUnderScopeAnchor(owner);

		if (chain.Length < focusPath.Length)
			return false;

		for (int i = 0; i < focusPath.Length; i++)
		{
			if (chain[i] != focusPath[i])
				return false;
		}

		return true;
	}

	private static bool TryGetOwnerEntityChain(Node3D owner, out uint[] chain)
	{
		if (owner != null && _ownerEntityChainCache.TryGetValue(owner, out chain))
			return chain.Length > 0;

		if (!TryBuildOwnerEntityIdChain(owner, _scopeContentRoot, _scopeNodeEntities, _entityChainBuildBuffer))
		{
			chain = Array.Empty<uint>();
			if (owner != null)
				_ownerEntityChainCache[owner] = chain;
			return false;
		}

		chain = _entityChainBuildBuffer.ToArray();
		if (owner != null)
			_ownerEntityChainCache[owner] = chain;
		return true;
	}

	private static bool TryBuildOwnerEntityIdChain(
		Node3D owner,
		Node contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		List<uint> entityIds)
	{
		entityIds.Clear();
		if (owner == null || contentRoot == null || nodeEntities == null)
			return false;

		Node current = owner;
		while (current != null && current != contentRoot)
		{
			if (current is Node3D node3D && nodeEntities.TryGetValue(node3D, out Entity entity))
				entityIds.Add(entity.shortGUID.AsUInt32);

			current = current.GetParent();
		}

		if (entityIds.Count == 0)
			return false;

		entityIds.Reverse();
		return true;
	}

	private static bool IsStrictPathPrefix(uint[] prefix, uint[] path)
	{
		if (prefix == null || path == null || prefix.Length == 0 || prefix.Length >= path.Length)
			return false;

		for (int i = 0; i < prefix.Length; i++)
		{
			if (prefix[i] != path[i])
				return false;
		}

		return true;
	}

	private static void ApplyOwnerMeshFocusState(
		IReadOnlyList<MeshInstance3D> meshes,
		bool shouldDim,
		ref int meshesDimmed,
		ref int meshesUndimmed,
		ref int meshesUnchanged)
	{
		for (int i = 0; i < meshes.Count; i++)
		{
			//Every mesh of the level on a composite switch: most of a second on TECH_Hub
			LevelViewerSentMessages.PumpIfDue();
			MeshInstance3D mesh = meshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh) || mesh.IsInGroup(LevelViewerPick.WireframeOverlayGroupName))
				continue;

			switch (ApplyMeshFocusState(mesh, shouldDim))
			{
				case MeshFocusChange.Dimmed:
					meshesDimmed++;
					break;
				case MeshFocusChange.Undimmed:
					meshesUndimmed++;
					break;
				case MeshFocusChange.Unchanged:
					meshesUnchanged++;
					break;
			}
		}
	}

	private enum MeshFocusChange
	{
		Unchanged,
		Dimmed,
		Undimmed,
	}

	private static MeshFocusChange ApplyMeshFocusState(MeshInstance3D mesh, bool shouldDim)
	{
		if (shouldDim)
		{
			//Greyed before and still drawn grey. One that something has drawn over since is greyed again (DimMesh).
			if (_meshDimmedState.TryGetValue(mesh, out bool wasDimmed) && wasDimmed
				&& (!_savedMaterialOverrides.ContainsKey(mesh) || IsShowingDimmedMaterial(mesh)))
				return MeshFocusChange.Unchanged;

			SetMeshDimmed(mesh, true);
			_meshDimmedState[mesh] = true;
			return MeshFocusChange.Dimmed;
		}

		if (_meshDimmedState.TryGetValue(mesh, out bool wasDimmedOut) && !wasDimmedOut && !IsMeshVisuallyDimmed(mesh))
			return MeshFocusChange.Unchanged;

		SetMeshDimmed(mesh, false);
		_meshDimmedState[mesh] = false;
		return MeshFocusChange.Undimmed;
	}

	private static void SetMeshDimmed(MeshInstance3D mesh, bool dimmed)
	{
		//Scene-filter geometry answers to its own filter, not to focus. Greying out an occlusion hull
		//would hide the very thing the filter was switched on to show.
		if (mesh != null && mesh.IsInGroup(LevelViewerPick.SceneFilterGroupName))
			return;

		if (dimmed)
		{
			DimMesh(mesh);
			if (ModelReferenceRenderSettings.WireframeEnabled)
				SetWireframeOverlayVisible(mesh, false);
			return;
		}

		if (ModelReferenceRenderSettings.WireframeEnabled)
			SetWireframeOverlayVisible(mesh, true);
		RestoreMesh(mesh);
	}

	private static void RestoreMesh(MeshInstance3D meshInstance)
	{
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (!_savedMaterialOverrides.TryGetValue(meshInstance, out Material saved))
			return;

		try
		{
			meshInstance.MaterialOverride = saved;
			_savedMaterialOverrides.Remove(meshInstance);
		}
		catch (Exception)
		{
			_savedMaterialOverrides.Remove(meshInstance);
		}
	}

	private static void DimMesh(MeshInstance3D meshInstance)
	{
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (_savedMaterialOverrides.ContainsKey(meshInstance))
		{
			if (IsShowingDimmedMaterial(meshInstance))
				return;
			//Written over since it was greyed (a preview recoloured, a material remapped in place): that is now
			//what it goes back to, and the grey goes on over it again
			_savedMaterialOverrides.Remove(meshInstance);
		}

		try
		{
			Material current = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
			if (current == null)
				return;

			_savedMaterialOverrides[meshInstance] = meshInstance.MaterialOverride;
			meshInstance.MaterialOverride = GetSharedDimmedMaterial(current);
		}
		catch (Exception)
		{
			_savedMaterialOverrides.Remove(meshInstance);
			_meshDimmedState.Remove(meshInstance);
		}
	}

	private static Material GetSharedDimmedMaterial(Material original)
	{
		if (_dimmedMaterialBySource.TryGetValue(original, out Material cached))
			return cached;

		bool doubleSided = IsDoubleSidedMaterial(original);
		bool transparent = IsTransparentMaterial(original, out _);
		Material dimmed;

		if (transparent)
		{
			if (doubleSided)
			{
				_cachedDimmedTransparentDoubleSided ??= CreateSharedDimmedMaterial(
					GetDimmedTransparentDoubleSidedShader(), transparent: true);
				dimmed = _cachedDimmedTransparentDoubleSided;
			}
			else
			{
				_cachedDimmedTransparent ??= CreateSharedDimmedMaterial(
					GetDimmedTransparentShader(), transparent: true);
				dimmed = _cachedDimmedTransparent;
			}
		}
		else if (doubleSided)
		{
			_cachedDimmedOpaqueDoubleSided ??= CreateSharedDimmedMaterial(
				GetDimmedDoubleSidedShader(), transparent: false);
			dimmed = _cachedDimmedOpaqueDoubleSided;
		}
		else
		{
			_cachedDimmedOpaque ??= CreateSharedDimmedMaterial(GetDimmedShader(), transparent: false);
			dimmed = _cachedDimmedOpaque;
		}

		_dimmedMaterialBySource.AddOrUpdate(original, dimmed);
		return dimmed;
	}

	private static ShaderMaterial CreateSharedDimmedMaterial(Shader shader, bool transparent)
	{
		ShaderMaterial material = new ShaderMaterial
		{
			Shader = shader,
		};
		material.SetShaderParameter(BaseGreyParam, DimBaseGrey);
		if (transparent)
		{
			material.SetShaderParameter(OpacityParam, 0.24f);
			material.RenderPriority = TransparentRenderPriority;
		}

		return material;
	}

	private static Shader GetDimmedShader()
	{
		if (_dimmedShader == null)
			_dimmedShader = GD.Load<Shader>("res://shaders/composite_focus_dimmed.gdshader");
		return _dimmedShader;
	}

	private static Shader GetDimmedDoubleSidedShader()
	{
		if (_dimmedShaderDoubleSided == null)
			_dimmedShaderDoubleSided = GD.Load<Shader>("res://shaders/composite_focus_dimmed_double_sided.gdshader");
		return _dimmedShaderDoubleSided;
	}

	private static Shader GetDimmedTransparentShader()
	{
		if (_dimmedTransparentShader == null)
			_dimmedTransparentShader = GD.Load<Shader>("res://shaders/composite_focus_dimmed_transparent.gdshader");
		return _dimmedTransparentShader;
	}

	private static Shader GetDimmedTransparentDoubleSidedShader()
	{
		if (_dimmedTransparentShaderDoubleSided == null)
			_dimmedTransparentShaderDoubleSided = GD.Load<Shader>("res://shaders/composite_focus_dimmed_transparent_double_sided.gdshader");
		return _dimmedTransparentShaderDoubleSided;
	}

	private static bool IsTransparentMaterial(Material material, out float opacity)
	{
		opacity = 1f;
		if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null)
			return false;

		string path = shaderMaterial.Shader.ResourcePath;
		if (string.IsNullOrEmpty(path))
			return false;

		if (path.Contains("preview_transparent") || path.Contains("preview_icon_billboard"))
		{
			opacity = TryGetShaderColorAlpha(shaderMaterial, AlbedoColorParam, 0.24f);
			return true;
		}

		if (path.Contains("transparent") || path.Contains("wireframe_transparent"))
		{
			opacity = TryGetShaderColorAlpha(shaderMaterial, DiffuseTintParam, 1f);
			if (opacity >= 0.999f)
				opacity = TryGetShaderColorAlpha(shaderMaterial, AlbedoColorParam, opacity);
			return true;
		}

		return false;
	}

	private static float TryGetShaderColorAlpha(ShaderMaterial material, StringName parameterName, float fallback)
	{
		if (material == null)
			return fallback;

		Variant value = material.GetShaderParameter(parameterName);
		if (value.VariantType != Variant.Type.Color)
			return fallback;

		return value.AsColor().A;
	}

	private static bool IsDoubleSidedMaterial(Material material)
	{
		if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null)
			return false;

		string path = shaderMaterial.Shader.ResourcePath;
		if (string.IsNullOrEmpty(path))
			return false;

		return path.Contains("double_sided")
			|| path.Contains("preview_icon_billboard")
			|| path.Contains("preview_overlay_line");
	}

	private static void SetWireframeOverlayVisible(MeshInstance3D solidMesh, bool visible)
	{
		//By index: this runs for every mesh in a focus pass, and GetChildren() is a native array each time
		int childCount = solidMesh.GetChildCount();
		for (int i = 0; i < childCount; i++)
		{
			Node child = solidMesh.GetChild(i);
			if (child is Node3D node3D && child.IsInGroup(LevelViewerPick.WireframeOverlayGroupName))
				node3D.Visible = visible;
		}
	}

	private static Node3D ResolveScopeAnchorNode(Node contentRoot, uint[] instanceEntityPath)
	{
		if (contentRoot is not Node3D contentRoot3D)
			return null;

		if (instanceEntityPath == null || instanceEntityPath.Length == 0)
			return contentRoot3D;

		Node current = contentRoot3D;
		for (int i = 0; i < instanceEntityPath.Length; i++)
		{
			current = current.GetNodeOrNull(instanceEntityPath[i].ToString());
			if (current == null)
				return null;
		}

		return current as Node3D;
	}

	private static uint ResolveOwnerCompositeId(Node start, Node contentRoot)
	{
		Node current = start;
		while (current != null && current != contentRoot)
		{
			if (current is FunctionEntityPreview preview && preview.OwnerCompositeId != 0)
				return preview.OwnerCompositeId;

			uint ownerCompositeId;
			if (AlienScene.TryGetOwnerCompositeId(current, out ownerCompositeId))
				return ownerCompositeId;

			current = current.GetParent();
		}

		return 0;
	}
}
