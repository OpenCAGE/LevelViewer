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

        ClearInternal();
        _selectionRoot = selected;

        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        CollectSelectionMeshes(selected);

        for (int i = 0; i < _selectionMeshes.Count; i++)
            ApplyMeshHighlight(_selectionMeshes[i]);
    }

    public static void Clear()
    {
        ClearInternal();
    }

    private static void ClearInternal()
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
            if (mesh == null || !GodotObject.IsInstanceValid(mesh))
                continue;

            if (_savedOverlays.ContainsKey(mesh))
                continue;

            ApplyMeshHighlight(mesh);
        }
    }

    private static void CollectSelectionMeshes(Node3D selected)
    {
        _selectionMeshes.Clear();
        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        LevelViewerPick.CollectPickMeshesForEntitySubtree(selected, _selectionMeshes);
        if (_selectionMeshes.Count > 0)
            return;

        PreviewVisualUtility.CollectMeshInstancesForEntityVisual(selected, _selectionMeshes);
    }

    private static bool ApplyMeshHighlight(MeshInstance3D meshInstance)
    {
        return LevelViewerHighlightOverlay.TryApplyOverlay(
            meshInstance,
            _savedOverlays,
            LevelViewerHighlightOverlay.HighlightOverlayMode.Selection);
    }
}
