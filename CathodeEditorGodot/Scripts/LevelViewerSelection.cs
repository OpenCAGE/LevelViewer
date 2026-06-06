using Godot;
using System.Collections.Generic;

/// <summary>
/// Runtime selection highlight in the Game viewport (green tint on mesh materials).
/// </summary>
public static class LevelViewerSelection
{
    public static readonly Color HighlightGreen = new(0.35f, 1f, 0.45f, 1f);
    private static readonly Color TintMultiply = new(0.55f, 1.45f, 0.65f, 1f);
    private static readonly Color TintMixToward = new(0.3f, 0.95f, 0.4f, 1f);
    private const float TintMixWeight = 0.5f;

    private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();
    private static readonly Dictionary<MeshInstance3D, Material> _savedBillboardOverlays = new();
    private static ShaderMaterial _billboardHighlightOverlay;
    private static Node3D _selectionRoot;

    public static void SetSelectionRoot(Node3D root) => _selectionRoot = root;

    public static void Apply(Node3D selected)
    {
        if (selected != null && GodotObject.IsInstanceValid(selected) && selected == _selectionRoot)
            return;

		Clear();
		_selectionRoot = selected;

		if (selected == null || !GodotObject.IsInstanceValid(selected))
			return;

		CollectSelectionMeshes(selected);
		for (int i = 0; i < _selectionMeshes.Count; i++)
			TintMeshInstance(_selectionMeshes[i]);
	}

	public static void Clear()
	{
		RestoreMeshHighlights();
		_selectionRoot = null;
		_selectionMeshes.Clear();
	}

	private static readonly List<MeshInstance3D> _selectionMeshes = new();

    public static bool IsUnderSelection(Node node)
    {
        if (_selectionRoot == null || node == null)
            return false;

        Node current = node;
        while (current != null)
        {
            if (current == _selectionRoot)
                return true;

            current = current.GetParent();
        }

        return false;
    }

	public static void ReapplyIfSelectionActive()
	{
		if (_selectionRoot == null || !GodotObject.IsInstanceValid(_selectionRoot))
			return;

		for (int i = 0; i < _selectionMeshes.Count; i++)
		{
			MeshInstance3D mesh = _selectionMeshes[i];
			if (mesh != null && GodotObject.IsInstanceValid(mesh)
				&& !_savedMaterialOverrides.ContainsKey(mesh)
				&& !_savedBillboardOverlays.ContainsKey(mesh))
				TintMeshInstance(mesh);
		}
	}

	private static void CollectSelectionMeshes(Node3D root)
	{
		_selectionMeshes.Clear();
		CollectSelectionMeshesRecursive(root);
	}

	private static void CollectSelectionMeshesRecursive(Node node)
	{
		if (node is MeshInstance3D meshInstance)
			_selectionMeshes.Add(meshInstance);

		foreach (Node child in node.GetChildren())
			CollectSelectionMeshesRecursive(child);
	}

	private static void ApplyMeshHighlight(Node3D root)
	{
		CollectSelectionMeshes(root);
		for (int i = 0; i < _selectionMeshes.Count; i++)
			TintMeshInstance(_selectionMeshes[i]);
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

        if (PreviewVisualUtility.IsIconBillboardMaterial(current))
        {
            if (_savedBillboardOverlays.ContainsKey(meshInstance))
                return;

            _savedBillboardOverlays[meshInstance] = meshInstance.MaterialOverlay;
            meshInstance.MaterialOverlay = GetBillboardHighlightOverlay();
            return;
        }

        _savedMaterialOverrides[meshInstance] = meshInstance.MaterialOverride;

        Material tinted = (Material)current.Duplicate();
        if (tinted is StandardMaterial3D standard)
        {
            standard.EmissionEnabled = true;
            standard.Emission = HighlightGreen;
            standard.EmissionEnergyMultiplier = 2f;
            standard.AlbedoColor = BlendHighlightColor(standard.AlbedoColor);
        }
        else if (tinted is ShaderMaterial shaderMaterial)
        {
            ApplyShaderHighlight(shaderMaterial);
        }

        meshInstance.MaterialOverride = tinted;
    }

    private static void ApplyShaderHighlight(ShaderMaterial material)
    {
        material.SetShaderParameter("emission_enabled", true);
        material.SetShaderParameter("emission", new Vector3(HighlightGreen.R, HighlightGreen.G, HighlightGreen.B));
        material.SetShaderParameter("emission_energy", 2f);
        TryTintShaderColor(material, "diffuse_tint");
        TryTintShaderColor(material, "albedo_color");
        TryTintShaderColor(material, "albedo");
    }

    private static void TryTintShaderColor(ShaderMaterial material, string parameterName)
    {
        Variant value = material.GetShaderParameter(parameterName);
        if (value.VariantType == Variant.Type.Color)
            material.SetShaderParameter(parameterName, BlendHighlightColor(value.AsColor()));
    }

    private static ShaderMaterial GetBillboardHighlightOverlay()
    {
        if (_billboardHighlightOverlay == null)
        {
            _billboardHighlightOverlay = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://shaders/selection_highlight_overlay_billboard.gdshader"),
            };
            _billboardHighlightOverlay.SetShaderParameter("highlight_color", HighlightGreen);
            _billboardHighlightOverlay.SetShaderParameter("highlight_strength", 0.55f);
            _billboardHighlightOverlay.RenderPriority = 2;
        }

        return _billboardHighlightOverlay;
    }

    private static void RestoreMeshHighlights()
    {
        foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
        {
            if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
                entry.Key.MaterialOverride = entry.Value;
        }

        foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedBillboardOverlays)
        {
            if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
                entry.Key.MaterialOverlay = entry.Value;
        }

        _savedMaterialOverrides.Clear();
        _savedBillboardOverlays.Clear();
    }
}
