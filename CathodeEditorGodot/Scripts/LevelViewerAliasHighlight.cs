using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Orange tint on the specific instance targeted by an alias that carries override parameters.
/// </summary>
public static class LevelViewerAliasHighlight
{
	public static readonly Color HighlightOrange = new(1f, 0.55f, 0.15f, 1f);
	private static readonly Color TintMultiply = new(1.45f, 0.85f, 0.55f, 1f);
	private static readonly Color TintMixToward = new(0.95f, 0.45f, 0.1f, 1f);
	private const float TintMixWeight = 0.5f;

	private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();
	private static readonly List<MeshInstance3D> _highlightMeshes = new();

	public static void Refresh(AlienScene scene, Commands commands, uint activeCompositeId)
	{
		Clear();
		if (scene == null || commands == null || activeCompositeId == 0)
			return;

		Composite composite = commands.GetComposite(new ShortGuid(activeCompositeId));
		if (composite == null)
			return;

		HashSet<ulong> tintedMeshIds = new HashSet<ulong>();
		foreach (AliasEntity alias in composite.aliases)
		{
			if (alias?.parameters == null || alias.parameters.Count == 0)
				continue;

			if (!scene.TryGetEntitySceneNodes(composite.shortGUID, alias.shortGUID, out List<Node3D> aliasNodes))
				continue;

			for (int i = 0; i < aliasNodes.Count; i++)
			{
				if (aliasNodes[i] is not EntityOverride aliasOverride || aliasOverride.PointedEntity == null)
					continue;

				ApplyToNode(aliasOverride.PointedEntity, tintedMeshIds);
			}
		}
	}

	public static void Clear()
	{
		foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
		{
			if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
				entry.Key.MaterialOverride = entry.Value;
		}

		_savedMaterialOverrides.Clear();
		_highlightMeshes.Clear();
	}

	/// <summary>Restores materials orange-tinted under <paramref name="root"/> so selection green can take over.</summary>
	public static void ReleaseNode(Node3D root)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		List<MeshInstance3D> meshes = new();
		CollectMeshes(root, meshes);
		for (int i = 0; i < meshes.Count; i++)
			ReleaseMesh(meshes[i]);
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
		if (root == null || !GodotObject.IsInstanceValid(root) || LevelViewerSelection.IsUnderSelection(root))
			return;

		List<MeshInstance3D> meshes = new();
		CollectMeshes(root, meshes);
		for (int i = 0; i < meshes.Count; i++)
		{
			MeshInstance3D mesh = meshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			ulong meshId = mesh.GetInstanceId();
			if (tintedMeshIds.Contains(meshId) || _savedMaterialOverrides.ContainsKey(mesh))
				continue;

			tintedMeshIds.Add(meshId);
			_highlightMeshes.Add(mesh);
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

		Material current = meshInstance.GetActiveMaterial(0);
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
