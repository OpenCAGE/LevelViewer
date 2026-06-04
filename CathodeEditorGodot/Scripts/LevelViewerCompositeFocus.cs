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
	private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();
	private static readonly Dictionary<MeshInstance3D, bool> _meshDimmedState = new();
	private static readonly HashSet<MeshInstance3D> _dimmedPickables = new();
	private static readonly HashSet<uint> _compositesInScope = new();
	private static uint _scopeCacheActiveCompositeId;
	private static uint[] _scopeCacheInstancePath = Array.Empty<uint>();
	private static Node3D _scopeAnchorNode;

	public static bool HasActiveComposite => PreviewVisibilitySettings.ActiveCompositeId != 0;

	public static void RebuildScopeCache(Commands commands)
	{
		if (!HasActiveComposite || commands == null)
		{
			_compositesInScope.Clear();
			_scopeCacheActiveCompositeId = 0;
			_scopeCacheInstancePath = Array.Empty<uint>();
			return;
		}

		uint activeId = PreviewVisibilitySettings.ActiveCompositeId;
		uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();

		if (_scopeCacheActiveCompositeId == activeId
			&& PreviewVisibilitySettings.InstancePathsEqual(_scopeCacheInstancePath, instancePath)
			&& _compositesInScope.Count > 0)
		{
			return;
		}

		_compositesInScope.Clear();
		_scopeCacheActiveCompositeId = activeId;
		_scopeCacheInstancePath = (uint[])instancePath.Clone();
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

		uint ownerCompositeId = ResolveOwnerCompositeId(node, contentRoot);
		if (!IsOwnerCompositeInScope(ownerCompositeId, commands))
			return false;

		uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
		if (instancePath.Length == 0)
			return true;

		if (_scopeAnchorNode == null || !GodotObject.IsInstanceValid(_scopeAnchorNode))
			return false;

		return node == _scopeAnchorNode || IsDescendantOf(node, _scopeAnchorNode);
	}

	public static void Refresh(Node3D sceneRoot, Node contentRoot, Commands commands)
	{
		if (!HasActiveComposite || sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot) || commands == null)
		{
			Clear();
			return;
		}

		uint activeId = PreviewVisibilitySettings.ActiveCompositeId;
		uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
		if (_scopeCacheActiveCompositeId != activeId
			|| !PreviewVisibilitySettings.InstancePathsEqual(_scopeCacheInstancePath, instancePath))
		{
			_meshDimmedState.Clear();
		}

		RebuildScopeCache(commands);
		_scopeAnchorNode = ResolveScopeAnchorNode(contentRoot, instancePath);
		LevelViewerPick.InvalidateScopedPickables();
		ApplyRecursive(sceneRoot, contentRoot, commands);
	}

	public static void Clear()
	{
		RestoreAllDimmedMeshes();
		_compositesInScope.Clear();
		_scopeCacheActiveCompositeId = 0;
		_scopeCacheInstancePath = Array.Empty<uint>();
		_scopeAnchorNode = null;
		_meshDimmedState.Clear();
		_dimmedPickables.Clear();
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
	}

	private static void ApplyRecursive(Node node, Node contentRoot, Commands commands)
	{
		if (node is MeshInstance3D meshInstance && GodotObject.IsInstanceValid(meshInstance))
		{
			if (!node.IsInGroup(WireframeOverlayGroup))
				ApplyMeshFocus(meshInstance, contentRoot, commands);
		}

		foreach (Node child in node.GetChildren())
			ApplyRecursive(child, contentRoot, commands);
	}

	private static void ApplyMeshFocus(MeshInstance3D mesh, Node contentRoot, Commands commands)
	{
		if (LevelViewerSelection.IsUnderSelection(mesh))
			return;

		bool shouldDim = !IsNodeInScope(mesh, contentRoot, commands);
		if (_meshDimmedState.TryGetValue(mesh, out bool wasDimmed) && wasDimmed == shouldDim)
			return;

		if (shouldDim)
			SetMeshDimmed(mesh, true);
		else
			SetMeshDimmed(mesh, false);

		_meshDimmedState[mesh] = shouldDim;
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
		if (_savedMaterialOverrides.ContainsKey(meshInstance))
			return;

		Material current = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
		if (current == null)
			return;

		_savedMaterialOverrides[meshInstance] = meshInstance.MaterialOverride;
		meshInstance.MaterialOverride = CreateDimmedMaterial(current);
	}

	private static Material CreateDimmedMaterial(Material original)
	{
		bool doubleSided = IsDoubleSidedMaterial(original);
		bool transparent = IsTransparentMaterial(original, out float opacity);

		Shader shader;
		if (transparent)
		{
			shader = doubleSided
				? GetDimmedTransparentDoubleSidedShader()
				: GetDimmedTransparentShader();
		}
		else
		{
			shader = doubleSided
				? GetDimmedDoubleSidedShader()
				: GetDimmedShader();
		}

		ShaderMaterial material = new ShaderMaterial
		{
			Shader = shader,
		};
		material.SetShaderParameter("base_grey", DimBaseGrey);
		if (transparent)
		{
			material.SetShaderParameter("opacity", opacity);
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

	private static bool IsDescendantOf(Node node, Node ancestor)
	{
		if (node == null || ancestor == null)
			return false;

		Node current = node;
		while (current != null)
		{
			if (current == ancestor)
				return true;

			current = current.GetParent();
		}

		return false;
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
