using Godot;
using System.Collections.Generic;

/// <summary>
/// Runtime selection highlight in the Game viewport (wireframe bounds + emissive mesh tint).
/// </summary>
public static class LevelViewerSelection
{
    public static readonly Color WireframeColor = new Color(1f, 0.55f, 0.05f, 1f);
    public static readonly Color MeshEmissionColor = new Color(1f, 0.7f, 0.15f, 1f);

    private static Node3D _highlightRoot;
    private static readonly Dictionary<MeshInstance3D, Material> _savedMaterialOverrides = new();

    public static void Apply(Node3D selected)
    {
        Clear();

        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        _highlightRoot = new Node3D { Name = "SelectionHighlight" };
        selected.AddChild(_highlightRoot);

        ApplyMeshEmissionHighlight(selected);
        BuildWireframeBounds(_highlightRoot, selected);
    }

    public static void Clear()
    {
        RestoreMeshEmissionHighlight();

        if (_highlightRoot != null && GodotObject.IsInstanceValid(_highlightRoot))
            _highlightRoot.QueueFree();

        _highlightRoot = null;
    }

    private static void ApplyMeshEmissionHighlight(Node3D root)
    {
        if (root == _highlightRoot)
            return;

        if (root is MeshInstance3D meshInstance)
            TintMeshInstance(meshInstance);

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                ApplyMeshEmissionHighlight(child3D);
        }
    }

    private static void TintMeshInstance(MeshInstance3D meshInstance)
    {
        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
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
            standard.Emission = MeshEmissionColor;
            standard.EmissionEnergyMultiplier = 1.25f;
        }
        else if (tinted is ShaderMaterial shaderMaterial)
        {
            shaderMaterial.SetShaderParameter("emission_enabled", true);
            shaderMaterial.SetShaderParameter("emission", MeshEmissionColor);
            shaderMaterial.SetShaderParameter("emission_energy", 1.25f);
        }

        meshInstance.MaterialOverride = tinted;
    }

    private static void RestoreMeshEmissionHighlight()
    {
        foreach (KeyValuePair<MeshInstance3D, Material> entry in _savedMaterialOverrides)
        {
            if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
                entry.Key.MaterialOverride = entry.Value;
        }

        _savedMaterialOverrides.Clear();
    }

    private static void BuildWireframeBounds(Node3D highlightRoot, Node3D selected)
    {
        if (!LevelViewerView.TryComputeLocalSubtreeAabb(selected, out Aabb bounds) || !bounds.HasVolume())
        {
            bounds = new Aabb(Vector3.Zero, Vector3.One * 0.5f);
        }

        Vector3 min = bounds.Position;
        Vector3 max = bounds.Position + bounds.Size;
        float lineWidth = Mathf.Clamp(bounds.Size.Length() * 0.004f, 0.02f, 0.35f);

        Vector3[] corners =
        {
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z),
        };

        int[,] edges =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
            { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },
        };

        for (int i = 0; i < edges.GetLength(0); i++)
        {
            PreviewVisualUtility.CreateLineSegment(
                $"Edge{i}",
                highlightRoot,
                corners[edges[i, 0]],
                corners[edges[i, 1]],
                lineWidth,
                WireframeColor);
        }
    }
}
