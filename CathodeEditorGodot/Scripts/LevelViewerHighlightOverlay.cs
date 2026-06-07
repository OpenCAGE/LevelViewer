using Godot;
using System.Collections.Generic;

/// <summary>
/// Shared additive MaterialOverlay materials for mesh/billboard highlights.
/// </summary>
public static class LevelViewerHighlightOverlay
{
    private const float DefaultStrength = 0.55f;
    private const int RenderPriority = 2;

    private static ShaderMaterial _selectionMeshOverlay;
    private static ShaderMaterial _selectionBillboardOverlay;
    private static ShaderMaterial _aliasMeshOverlay;
    private static ShaderMaterial _aliasBillboardOverlay;

    public static ShaderMaterial GetSelectionMeshOverlay()
        => GetOrCreate(ref _selectionMeshOverlay, "res://shaders/selection_highlight_overlay.gdshader", LevelViewerSelection.HighlightGreen);

    public static ShaderMaterial GetSelectionBillboardOverlay()
        => GetOrCreate(ref _selectionBillboardOverlay, "res://shaders/selection_highlight_overlay_billboard.gdshader", LevelViewerSelection.HighlightGreen);

    public static ShaderMaterial GetAliasMeshOverlay()
        => GetOrCreate(ref _aliasMeshOverlay, "res://shaders/selection_highlight_overlay.gdshader", LevelViewerAliasHighlight.HighlightOrange);

    public static ShaderMaterial GetAliasBillboardOverlay()
        => GetOrCreate(ref _aliasBillboardOverlay, "res://shaders/selection_highlight_overlay_billboard.gdshader", LevelViewerAliasHighlight.HighlightOrange);

    public static ShaderMaterial GetOverlayForMesh(Material sourceMaterial, bool aliasHighlight)
    {
        bool billboard = PreviewVisualUtility.IsIconBillboardMaterial(sourceMaterial);
        if (aliasHighlight)
            return billboard ? GetAliasBillboardOverlay() : GetAliasMeshOverlay();

        return billboard ? GetSelectionBillboardOverlay() : GetSelectionMeshOverlay();
    }

    public static bool TryApplyOverlay(
        MeshInstance3D meshInstance,
        Dictionary<MeshInstance3D, Material> savedOverlays,
        bool aliasHighlight)
    {
        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
            return false;

        if (meshInstance.IsInGroup("model_reference_wireframe_overlay"))
            return false;

        if (savedOverlays.ContainsKey(meshInstance))
            return false;

        Material current = meshInstance.MaterialOverride ?? meshInstance.GetActiveMaterial(0);
        if (current == null)
            return false;

        savedOverlays[meshInstance] = meshInstance.MaterialOverlay;
        meshInstance.MaterialOverlay = GetOverlayForMesh(current, aliasHighlight);
        return true;
    }

    public static void RestoreOverlays(Dictionary<MeshInstance3D, Material> savedOverlays)
    {
        foreach (KeyValuePair<MeshInstance3D, Material> entry in savedOverlays)
        {
            if (entry.Key != null && GodotObject.IsInstanceValid(entry.Key))
                entry.Key.MaterialOverlay = entry.Value;
        }

        savedOverlays.Clear();
    }

    private static ShaderMaterial GetOrCreate(ref ShaderMaterial cache, string shaderPath, Color highlightColor)
    {
        if (cache == null)
        {
            cache = new ShaderMaterial
            {
                Shader = GD.Load<Shader>(shaderPath),
            };
            cache.SetShaderParameter("highlight_color", highlightColor);
            cache.SetShaderParameter("highlight_strength", DefaultStrength);
            cache.RenderPriority = RenderPriority;
        }

        return cache;
    }
}
