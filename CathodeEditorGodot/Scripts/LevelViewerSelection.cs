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
    /* Everything currently marked. One entry for an ordinary selection; several once entities are
       ctrl-clicked together. The first is the one the rest of the viewer treats as the selection. */
    private static readonly List<Node3D> _selectionRoots = new();
    private static readonly List<MeshInstance3D> _selectionMeshes = new();

    /// <summary>
    /// The mode came from OpenCAGE and may have changed with something already selected: what is drawn
    /// now belongs to the old mode, so it comes off before the same selection is drawn the new way.
    /// </summary>
    public static void SetMode(OpenCAGE.UnityConnection.LevelViewerHighlightMode mode)
    {
        if (PreviewVisibilitySettings.SelectionHighlightMode == mode)
            return;

        List<Node3D> selected = new List<Node3D>(_selectionRoots);
        ClearInternal();
        PreviewVisibilitySettings.SelectionHighlightMode = mode;
        MarkAll(selected);
    }

    public static void Apply(Node3D selected)
    {
        Apply(selected == null ? null : new List<Node3D> { selected });
    }

    /// <summary>Mark every node in the selection. The first is the one the gizmo and camera follow.</summary>
    public static void Apply(IReadOnlyList<Node3D> selected)
    {
        if (SameAsCurrent(selected))
            return;

        ClearInternal();
        MarkAll(selected);
    }

    private static void MarkAll(IReadOnlyList<Node3D> selected)
    {
        if (selected == null)
            return;

        for (int i = 0; i < selected.Count; i++)
        {
            Node3D node = selected[i];
            if (node == null || !GodotObject.IsInstanceValid(node))
                continue;

            _selectionRoots.Add(node);
            CollectSelectionMeshes(node, append: true);
        }

        for (int i = 0; i < _selectionMeshes.Count; i++)
            ApplyMeshHighlight(_selectionMeshes[i]);
    }

    private static bool SameAsCurrent(IReadOnlyList<Node3D> selected)
    {
        int count = selected?.Count ?? 0;
        int valid = 0;
        for (int i = 0; i < count; i++)
            if (selected[i] != null && GodotObject.IsInstanceValid(selected[i]))
                valid++;

        if (valid != _selectionRoots.Count || valid == 0)
            return false;

        int at = 0;
        for (int i = 0; i < count; i++)
        {
            Node3D node = selected[i];
            if (node == null || !GodotObject.IsInstanceValid(node))
                continue;
            if (_selectionRoots[at++] != node)
                return false;
        }

        return true;
    }

    public static void Clear()
    {
        ClearInternal();
    }

    private static void ClearInternal()
    {
        LevelViewerHighlightOverlay.RestoreOverlays(_savedOverlays);
        LevelViewerHighlightOverlay.RestoreOverrides(_savedOverrides);
        _selectionRoots.Clear();
        _selectionMeshes.Clear();
    }

    public static bool IsUnderSelection(Node node)
    {
        if (node == null || _selectionRoots.Count == 0)
            return false;

        Node current = node;
        while (current != null)
        {
            for (int i = 0; i < _selectionRoots.Count; i++)
                if (current == _selectionRoots[i])
                    return true;

            current = current.GetParent();
        }

        return false;
    }

    public static void ReapplyIfSelectionActive()
    {
        if (_selectionRoots.Count == 0)
            return;

        for (int i = 0; i < _selectionMeshes.Count; i++)
        {
            MeshInstance3D mesh = _selectionMeshes[i];
            if (mesh == null || !GodotObject.IsInstanceValid(mesh))
                continue;

            ApplyMeshHighlight(mesh); //each mode's apply is a no-op on a mesh it already marked
        }
    }

    private static void CollectSelectionMeshes(Node3D selected, bool append = false)
    {
        if (!append)
            _selectionMeshes.Clear();

        if (selected == null || !GodotObject.IsInstanceValid(selected))
            return;

        int before = _selectionMeshes.Count;
        LevelViewerPick.CollectPickMeshesForEntitySubtree(selected, _selectionMeshes);
        if (_selectionMeshes.Count > before)
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
