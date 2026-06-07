using Godot;
using System.Collections.Generic;

/// <summary>
/// Runtime selection highlight in the Game viewport (green additive overlay per mesh).
/// Uses shared overlay materials — no per-mesh Material.Duplicate().
/// </summary>
public static class LevelViewerSelection
{
    public static readonly Color HighlightGreen = new(0.35f, 1f, 0.45f, 1f);

    private static readonly Dictionary<MeshInstance3D, Material> _savedOverlays = new();
    private static Node3D _selectionRoot;
    private static readonly List<MeshInstance3D> _selectionMeshes = new();

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
            ApplyMeshHighlight(_selectionMeshes[i]);
    }

    public static void Clear()
    {
        LevelViewerHighlightOverlay.RestoreOverlays(_savedOverlays);
        _selectionRoot = null;
        _selectionMeshes.Clear();
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

        for (int i = 0; i < _selectionMeshes.Count; i++)
        {
            MeshInstance3D mesh = _selectionMeshes[i];
            if (mesh != null && GodotObject.IsInstanceValid(mesh) && !_savedOverlays.ContainsKey(mesh))
                ApplyMeshHighlight(mesh);
        }
    }

    private static void CollectSelectionMeshes(Node3D selected)
    {
        _selectionMeshes.Clear();
        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        var buffer = new List<MeshInstance3D>();
        PreviewVisualUtility.CollectMeshInstances(selected, buffer);
        _selectionMeshes.AddRange(buffer);
    }

    private static void ApplyMeshHighlight(MeshInstance3D meshInstance)
    {
        LevelViewerHighlightOverlay.TryApplyOverlay(meshInstance, _savedOverlays, aliasHighlight: false);
    }
}
