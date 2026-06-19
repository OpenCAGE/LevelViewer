using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Greys out and disables picking for geometry outside the composite OpenCAGE is currently viewing.
/// </summary>
public static class LevelViewerCompositeFocus
{
	private const string WireframeOverlayGroup = "model_reference_wireframe_overlay";
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
	private static readonly HashSet<MeshInstance3D> _dimmedPickables = new();
	private static readonly HashSet<uint> _compositesInScope = new();
	private static uint _scopeCacheActiveCompositeId;
	private static uint[] _lastFocusInstancePath = Array.Empty<uint>();
	private static Node3D _scopeAnchorNode;
	private static IReadOnlyDictionary<Node3D, Entity> _scopeNodeEntities;
	private static Node _scopeContentRoot;

	public static bool HasActiveComposite => PreviewVisibilitySettings.ActiveCompositeId != 0;

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

				Composite child = commands.GetComposite(function.function);
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

		if (node is Node3D entityNode && entityNode.HasMeta(AlienScene.OwnerCompositeMetaKey))
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

		if (owner.HasMeta(AlienScene.OwnerCompositeMetaKey))
		{
			uint ownerCompositeId = owner.GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32();
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
			&& TryBuildOwnerEntityIdChain(owner, _scopeContentRoot, _scopeNodeEntities, out List<uint> chain))
		{
			if (chain.Count < focusPath.Length)
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

	private static bool TryBuildOwnerEntityIdChain(
		Node3D owner,
		Node contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		out List<uint> entityIds)
	{
		entityIds = new List<uint>();
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
		IReadOnlyDictionary<Node3D, Entity> nodeEntities = null)
	{
		if (!HasActiveComposite || sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot) || commands == null)
		{
			Clear();
			return;
		}

		_scopeNodeEntities = nodeEntities ?? _scopeNodeEntities;
		_scopeContentRoot = contentRoot;
		uint activeId = PreviewVisibilitySettings.ActiveCompositeId;
		uint[] instancePath = PreviewVisibilitySettings.CompositeFocusInstancePath ?? Array.Empty<uint>();
		uint[] previousFocusPath = _lastFocusInstancePath;
		uint previousScopeComposite = _scopeCacheActiveCompositeId;
		bool activeCompositeChanged = previousScopeComposite != 0 && previousScopeComposite != activeId;
		bool focusPathChanged = !PreviewVisibilitySettings.InstancePathsEqual(previousFocusPath, instancePath);

		if (activeCompositeChanged)
			ResetDimStateForScopeChange();

		RebuildScopeCache(commands);
		_scopeAnchorNode = scopeAnchorOverride ?? ResolveScopeAnchorNode(contentRoot, instancePath);
		_lastFocusInstancePath = (uint[])instancePath.Clone();

		if (activeCompositeChanged || focusPathChanged)
			ApplyFocusFromPickRegistry(commands);

		LevelViewerPick.InvalidateScopedPickables();
	}

	/// <summary>Clears all dimmed materials/pick state before re-applying a new drill scope.</summary>
	private static void ResetDimStateForScopeChange()
	{
		RestoreAllDimmedMeshes();
		_meshDimmedState.Clear();
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
		_lastFocusInstancePath = Array.Empty<uint>();
		_scopeAnchorNode = null;
		_meshDimmedState.Clear();
		_dimmedPickables.Clear();
		ClearScopeEvaluationContext();
	}

	private static void RestoreAllDimmedMeshes()
	{
		foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
		{
			if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
			{
				entry.Key.MaterialOverride = entry.Value;
				SetWireframeOverlayVisible(entry.Key, true);
			}
		}

		foreach (MeshInstance3D mesh in _dimmedPickables)
		{
			if (mesh != null && GodotObject.IsInstanceValid(mesh) && !mesh.IsInGroup(LevelViewerPick.PickableGroup))
				mesh.AddToGroup(LevelViewerPick.PickableGroup);
		}

		_savedMaterialOverrides.Clear();
		_dimmedPickables.Clear();
	}

	private static void ApplyFocusFromPickRegistry(Commands commands)
	{
		LevelViewerPick.ForEachPickOwner((owner, meshes) =>
		{
			if (LevelViewerSelection.IsUnderSelection(owner))
				return;

			bool shouldDim = !IsPickOwnerInScope(owner, commands);
			for (int i = 0; i < meshes.Count; i++)
			{
				MeshInstance3D mesh = meshes[i];
				if (mesh == null || !GodotObject.IsInstanceValid(mesh) || mesh.IsInGroup(WireframeOverlayGroup))
					continue;

				ApplyMeshFocusState(mesh, shouldDim);
			}
		});
	}

	private static void ApplyMeshFocusState(MeshInstance3D mesh, bool shouldDim)
	{
		if (shouldDim)
		{
			if (_meshDimmedState.TryGetValue(mesh, out bool wasDimmed) && wasDimmed)
				return;

			SetMeshDimmed(mesh, true);
			_meshDimmedState[mesh] = true;
			return;
		}

		if (_meshDimmedState.TryGetValue(mesh, out bool wasDimmedOut) && !wasDimmedOut && !IsMeshVisuallyDimmed(mesh))
			return;

		SetMeshDimmed(mesh, false);
		_meshDimmedState[mesh] = false;
	}

	private static void SetMeshDimmed(MeshInstance3D mesh, bool dimmed)
	{
		if (dimmed)
		{
			DimMesh(mesh);
			SetWireframeOverlayVisible(mesh, false);
			if (mesh.IsInGroup(LevelViewerPick.PickableGroup))
			{
				mesh.RemoveFromGroup(LevelViewerPick.PickableGroup);
				_dimmedPickables.Add(mesh);
			}

			return;
		}

		SetWireframeOverlayVisible(mesh, true);
		RestoreMesh(mesh);
	}

	private static void RestoreMesh(MeshInstance3D meshInstance)
	{
		if (_savedMaterialOverrides.TryGetValue(meshInstance, out Material saved))
		{
			meshInstance.MaterialOverride = saved;
			_savedMaterialOverrides.Remove(meshInstance);
		}

		if (_dimmedPickables.Remove(meshInstance) && !meshInstance.IsInGroup(LevelViewerPick.PickableGroup))
			meshInstance.AddToGroup(LevelViewerPick.PickableGroup);
	}

	private static void DimMesh(MeshInstance3D meshInstance)
	{
		if (!_savedMaterialOverrides.ContainsKey(meshInstance))
		{
			Material current = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
			if (current == null)
				return;

			_savedMaterialOverrides[meshInstance] = meshInstance.MaterialOverride;
			meshInstance.MaterialOverride = GetSharedDimmedMaterial(current);
		}
	}

	private static Material GetSharedDimmedMaterial(Material original)
	{
		bool doubleSided = IsDoubleSidedMaterial(original);
		bool transparent = IsTransparentMaterial(original, out _);

		if (transparent)
		{
			if (doubleSided)
			{
				_cachedDimmedTransparentDoubleSided ??= CreateSharedDimmedMaterial(
					GetDimmedTransparentDoubleSidedShader(), transparent: true);
				return _cachedDimmedTransparentDoubleSided;
			}

			_cachedDimmedTransparent ??= CreateSharedDimmedMaterial(
				GetDimmedTransparentShader(), transparent: true);
			return _cachedDimmedTransparent;
		}

		if (doubleSided)
		{
			_cachedDimmedOpaqueDoubleSided ??= CreateSharedDimmedMaterial(
				GetDimmedDoubleSidedShader(), transparent: false);
			return _cachedDimmedOpaqueDoubleSided;
		}

		_cachedDimmedOpaque ??= CreateSharedDimmedMaterial(GetDimmedShader(), transparent: false);
		return _cachedDimmedOpaque;
	}

	private static ShaderMaterial CreateSharedDimmedMaterial(Shader shader, bool transparent)
	{
		ShaderMaterial material = new ShaderMaterial
		{
			Shader = shader,
		};
		material.SetShaderParameter("base_grey", DimBaseGrey);
		if (transparent)
		{
			material.SetShaderParameter("opacity", 0.24f);
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
			opacity = TryGetShaderColorAlpha(shaderMaterial, "albedo_color", 0.24f);
			return true;
		}

		if (path.Contains("transparent") || path.Contains("wireframe_transparent"))
		{
			opacity = TryGetShaderColorAlpha(shaderMaterial, "diffuse_tint", 1f);
			if (opacity >= 0.999f)
				opacity = TryGetShaderColorAlpha(shaderMaterial, "albedo_color", opacity);
			return true;
		}

		return false;
	}

	private static float TryGetShaderColorAlpha(ShaderMaterial material, string parameterName, float fallback)
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
		foreach (Node child in solidMesh.GetChildren())
		{
			if (child is Node3D node3D && child.IsInGroup(WireframeOverlayGroup))
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

			if (current.HasMeta(AlienScene.OwnerCompositeMetaKey))
				return current.GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32();

			current = current.GetParent();
		}

		return 0;
	}
}
