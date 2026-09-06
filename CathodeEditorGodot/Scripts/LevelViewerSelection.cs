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
    private static readonly Dictionary<MeshInstance3D, Material> _savedOverrides = new();
    private static Node3D _selectionRoot;
    private static readonly List<MeshInstance3D> _selectionMeshes = new();

    public static void SetSelectionRoot(Node3D root) => _selectionRoot = root;

    /// <summary>
    /// The mode came from OpenCAGE and may have changed with something already selected: what is drawn
    /// now belongs to the old mode, so it comes off before the same selection is drawn the new way.
    /// </summary>
    public static void SetMode(OpenCAGE.UnityConnection.LevelViewerHighlightMode mode)
    {
        if (PreviewVisibilitySettings.SelectionHighlightMode == mode)
            return;

        Node3D selected = _selectionRoot;
        ClearInternal();
        PreviewVisibilitySettings.SelectionHighlightMode = mode;

        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        _selectionRoot = selected;
        CollectSelectionMeshes(selected);
        for (int i = 0; i < _selectionMeshes.Count; i++)
            ApplyMeshHighlight(_selectionMeshes[i]);
    }

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
        LevelViewerHighlightOverlay.RestoreOverrides(_savedOverrides);
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

            ApplyMeshHighlight(mesh); //each mode's apply is a no-op on a mesh it already marked
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
        switch (PreviewVisibilitySettings.SelectionHighlightMode)
        {
            case OpenCAGE.UnityConnection.LevelViewerHighlightMode.None:
                return false;
            case OpenCAGE.UnityConnection.LevelViewerHighlightMode.Wireframe:
                return LevelViewerHighlightOverlay.TryApplyWireframeOverlay(meshInstance, _savedOverlays);
            case OpenCAGE.UnityConnection.LevelViewerHighlightMode.WireframeTransparent:
                return LevelViewerHighlightOverlay.TryApplyWireframe(meshInstance, _savedOverrides);
            default:
                return LevelViewerHighlightOverlay.TryApplyOverlay(
                    meshInstance,
                    _savedOverlays,
                    LevelViewerHighlightOverlay.HighlightOverlayMode.Selection);
        }
    }
}
