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

        ApplyMeshHighlight(selected);
    }

    public static void Clear()
    {
        RestoreMeshHighlights();
        _selectionRoot = null;
    }

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

        ApplyMeshHighlight(_selectionRoot);
    }

    private static void ApplyMeshHighlight(Node3D root)
    {
        if (root is MeshInstance3D meshInstance)
            TintMeshInstance(meshInstance);

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                ApplyMeshHighlight(child3D);
        }
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

    private static void RestoreMeshHighlights()
    {
        foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
        {
            if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
                entry.Key.MaterialOverride = entry.Value;
        }

        _savedMaterialOverrides.Clear();
    }
}
