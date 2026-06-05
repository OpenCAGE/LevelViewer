using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Orange tint on instances targeted by parameterized aliases defined on the active composite.
/// </summary>
public static class LevelViewerAliasHighlight
{
	public static readonly Color HighlightOrange = new(1f, 0.55f, 0.15f, 1f);
	private static readonly Color TintMultiply = new(1.45f, 0.85f, 0.55f, 1f);
	private static readonly Color TintMixToward = new(0.95f, 0.45f, 0.1f, 1f);
	private const float TintMixWeight = 0.5f;

	private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();
	private static readonly List<MeshInstance3D> _highlightMeshes = new();
	private static readonly List<MeshInstance3D> _meshCollectBuffer = new();

	private static uint _cachedActiveCompositeId;
	private static uint[] _cachedInstancePath = Array.Empty<uint>();
	private static bool _cacheValid;

	public static bool NeedsRebuild(uint activeCompositeId)
	{
		if (!_cacheValid)
			return true;

		if (_cachedActiveCompositeId != activeCompositeId)
			return true;

		return !PreviewVisibilitySettings.InstancePathsEqual(
			_cachedInstancePath,
			PreviewVisibilitySettings.ActiveInstanceEntityPath);
	}

	public static void InvalidateCache() => _cacheValid = false;

	public static void Rebuild(AlienScene scene, Commands commands, uint activeCompositeId)
	{
		Clear();
		if (scene == null || commands == null || activeCompositeId == 0)
		{
			_cacheValid = false;
			return;
		}

		Node3D contentRoot = scene.ParentNode;
		if (contentRoot == null)
		{
			_cacheValid = false;
			return;
		}

		HashSet<ulong> tintedMeshIds = new HashSet<ulong>();
		scene.ForEachParameterizedAliasInActiveComposite((ownerComposite, alias) =>
		{
			if (!scene.TryGetEntitySceneNodes(ownerComposite.shortGUID, alias.shortGUID, out List<Node3D> aliasNodes))
				return;

			for (int i = 0; i < aliasNodes.Count; i++)
			{
				if (aliasNodes[i] is not EntityOverride aliasOverride)
					continue;

				if (!LevelViewerCompositeFocus.IsPickOwnerInScope(aliasOverride, commands))
					continue;

				if (!scene.TryResolveAliasPointedSceneNode(
						aliasOverride,
						alias,
						ownerComposite,
						out Node3D pointedNode,
						preferCached: false))
				{
					continue;
				}

				if (!LevelViewerCompositeFocus.IsNodeInScope(pointedNode, contentRoot, commands))
					continue;

				ApplyToNode(pointedNode, tintedMeshIds);
			}
		});

		_cachedActiveCompositeId = activeCompositeId;
		_cachedInstancePath = (uint[])(PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>()).Clone();
		_cacheValid = true;
	}

	/// <summary>Re-applies cached orange tint while skipping the current selection subtree.</summary>
	public static void SyncWithSelection() => ReapplyIfActive();

	public static void Clear()
	{
		foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
		{
			if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
				entry.Key.MaterialOverride = entry.Value;
		}

		_savedMaterialOverrides.Clear();
		_highlightMeshes.Clear();
		_cacheValid = false;
	}

	/// <summary>Restores materials orange-tinted under <paramref name="root"/> so selection green can take over.</summary>
	public static void ReleaseNode(Node3D root)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		_meshCollectBuffer.Clear();
		CollectMeshes(root, _meshCollectBuffer);
		for (int i = 0; i < _meshCollectBuffer.Count; i++)
			ReleaseMeshForSelection(_meshCollectBuffer[i]);
	}

	public static void ReapplyIfActive()
	{
		for (int i = 0; i < _highlightMeshes.Count; i++)
		{
			MeshInstance3D mesh = _highlightMeshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (_savedMaterialOverrides.ContainsKey(mesh))
				continue;

			if (IsMeshUnderSelection(mesh))
				continue;

			TintMeshInstance(mesh);
		}
	}

	private static void ReleaseMeshForSelection(MeshInstance3D mesh)
	{
		if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			return;

		if (_savedMaterialOverrides.TryGetValue(mesh, out Material saved))
			mesh.MaterialOverride = saved;

		_savedMaterialOverrides.Remove(mesh);
	}

	private static void ReleaseMesh(MeshInstance3D mesh)
	{
		if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			return;

		if (_savedMaterialOverrides.TryGetValue(mesh, out Material saved))
			mesh.MaterialOverride = saved;

		_savedMaterialOverrides.Remove(mesh);
		_highlightMeshes.Remove(mesh);
	}

	private static bool IsMeshUnderSelection(MeshInstance3D mesh)
	{
		Node current = mesh;
		while (current != null)
		{
			if (LevelViewerSelection.IsUnderSelection(current))
				return true;

			current = current.GetParent();
		}

		return false;
	}

	private static void ApplyToNode(Node3D root, HashSet<ulong> tintedMeshIds)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		_meshCollectBuffer.Clear();
		CollectMeshes(root, _meshCollectBuffer);
		for (int i = 0; i < _meshCollectBuffer.Count; i++)
		{
			MeshInstance3D mesh = _meshCollectBuffer[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			ulong meshId = mesh.GetInstanceId();
			if (tintedMeshIds.Contains(meshId))
				continue;

			tintedMeshIds.Add(meshId);
			if (!_highlightMeshes.Contains(mesh))
				_highlightMeshes.Add(mesh);

			if (_savedMaterialOverrides.ContainsKey(mesh) || IsMeshUnderSelection(mesh))
				continue;

			TintMeshInstance(mesh);
		}
	}

	private static void CollectMeshes(Node node, List<MeshInstance3D> meshes)
	{
		if (node is MeshInstance3D meshInstance)
			meshes.Add(meshInstance);

		foreach (Node child in node.GetChildren())
			CollectMeshes(child, meshes);
	}

	private static Color BlendHighlightColor(Color color)
	{
		return color * TintMultiply + TintMixToward * TintMixWeight;
	}

	private static void TintMeshInstance(MeshInstance3D meshInstance)
	{
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
			return;

		if (meshInstance.IsInGroup("model_reference_wireframe_overlay"))
			return;

		if (_savedMaterialOverrides.ContainsKey(meshInstance))
			return;

		Material current = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
		if (current == null)
			return;

		_savedMaterialOverrides[meshInstance] = meshInstance.MaterialOverride;

		Material tinted = (Material)current.Duplicate();
		if (tinted is StandardMaterial3D standard)
		{
			standard.EmissionEnabled = true;
			standard.Emission = HighlightOrange;
			standard.EmissionEnergyMultiplier = 2f;
			standard.AlbedoColor = BlendHighlightColor(standard.AlbedoColor);
		}
		else if (tinted is ShaderMaterial shaderMaterial)
		{
			shaderMaterial.SetShaderParameter("emission_enabled", true);
			shaderMaterial.SetShaderParameter("emission", new Vector3(HighlightOrange.R, HighlightOrange.G, HighlightOrange.B));
			shaderMaterial.SetShaderParameter("emission_energy", 2f);
			TryTintShaderColor(shaderMaterial, "diffuse_tint");
			TryTintShaderColor(shaderMaterial, "albedo_color");
			TryTintShaderColor(shaderMaterial, "albedo");
		}

		meshInstance.MaterialOverride = tinted;
	}

	private static void TryTintShaderColor(ShaderMaterial material, string parameterName)
	{
		Variant value = material.GetShaderParameter(parameterName);
		if (value.VariantType == Variant.Type.Color)
			material.SetShaderParameter(parameterName, BlendHighlightColor(value.AsColor()));
	}
}
