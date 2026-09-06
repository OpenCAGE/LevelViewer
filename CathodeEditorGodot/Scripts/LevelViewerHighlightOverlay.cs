using Godot;
using System.Collections.Generic;

/// <summary>
/// Shared additive MaterialOverlay materials for mesh/billboard highlights.
/// </summary>
public static class LevelViewerHighlightOverlay
{
    public enum HighlightOverlayMode
    {
        Selection,
        Alias,
        Proxy,
    }

    private const float DefaultStrength = 0.55f;
    private const int RenderPriority = 2;

    private const string BackFacesShaderPath = "res://shaders/selection_highlight_overlay_backfaces.gdshader";
    private const string WireframeShaderPath = "res://shaders/selection_wireframe.gdshader";


    private static ShaderMaterial _selectionWireframe;

    private static ShaderMaterial _selectionMeshOverlay;
    private static ShaderMaterial _selectionBillboardOverlay;
    private static ShaderMaterial _selectionBackFacesOverlay;
    private static ShaderMaterial _aliasMeshOverlay;
    private static ShaderMaterial _aliasBillboardOverlay;
    private static ShaderMaterial _aliasBackFacesOverlay;
    private static ShaderMaterial _proxyMeshOverlay;
    private static ShaderMaterial _proxyBillboardOverlay;
    private static ShaderMaterial _proxyBackFacesOverlay;

    public static ShaderMaterial GetSelectionMeshOverlay()
        => GetOrCreate(ref _selectionMeshOverlay, "res://shaders/selection_highlight_overlay.gdshader", LevelViewerSelection.HighlightGreen);

    public static ShaderMaterial GetSelectionBillboardOverlay()
        => GetOrCreate(ref _selectionBillboardOverlay, "res://shaders/selection_highlight_overlay_billboard.gdshader", LevelViewerSelection.HighlightGreen);

    public static ShaderMaterial GetAliasMeshOverlay()
        => GetOrCreate(ref _aliasMeshOverlay, "res://shaders/selection_highlight_overlay.gdshader", LevelViewerAliasHighlight.HighlightOrange);

    public static ShaderMaterial GetAliasBillboardOverlay()
        => GetOrCreate(ref _aliasBillboardOverlay, "res://shaders/selection_highlight_overlay_billboard.gdshader", LevelViewerAliasHighlight.HighlightOrange);

    public static ShaderMaterial GetProxyMeshOverlay()
        => GetOrCreate(ref _proxyMeshOverlay, "res://shaders/selection_highlight_overlay.gdshader", LevelViewerProxyHighlight.HighlightBlue);

    public static ShaderMaterial GetProxyBillboardOverlay()
        => GetOrCreate(ref _proxyBillboardOverlay, "res://shaders/selection_highlight_overlay_billboard.gdshader", LevelViewerProxyHighlight.HighlightBlue);

    public static ShaderMaterial GetSelectionBackFacesOverlay()
        => GetOrCreate(ref _selectionBackFacesOverlay, BackFacesShaderPath, LevelViewerSelection.HighlightGreen);

    public static ShaderMaterial GetAliasBackFacesOverlay()
        => GetOrCreate(ref _aliasBackFacesOverlay, BackFacesShaderPath, LevelViewerAliasHighlight.HighlightOrange);

    public static ShaderMaterial GetProxyBackFacesOverlay()
        => GetOrCreate(ref _proxyBackFacesOverlay, BackFacesShaderPath, LevelViewerProxyHighlight.HighlightBlue);

    public static ShaderMaterial GetOverlayForMesh(Material sourceMaterial, HighlightOverlayMode mode)
    {
        bool billboard = PreviewVisualUtility.IsIconBillboardMaterial(sourceMaterial);

        //Geometry drawn back-face-only (occlusion hulls) needs the overlay culled the same way, or the
        //highlight paints in the near half of the hull and blocks the view of everything inside it.
        bool backFaces = !billboard && AlienSceneMaterials.IsBackFaceOnlyMaterial(sourceMaterial);

        switch (mode)
        {
            case HighlightOverlayMode.Alias:
                if (backFaces) return GetAliasBackFacesOverlay();
                return billboard ? GetAliasBillboardOverlay() : GetAliasMeshOverlay();
            case HighlightOverlayMode.Proxy:
                if (backFaces) return GetProxyBackFacesOverlay();
                return billboard ? GetProxyBillboardOverlay() : GetProxyMeshOverlay();
            default:
                if (backFaces) return GetSelectionBackFacesOverlay();
                return billboard ? GetSelectionBillboardOverlay() : GetSelectionMeshOverlay();
        }
    }

    /// <summary>
    /// Draws the mesh's edges over the top of it, leaving the surface as it is - the shape stays
    /// readable and the selection reads as an outline drawn through it.
    /// </summary>
    public static bool TryApplyWireframeOverlay(MeshInstance3D meshInstance, Dictionary<MeshInstance3D, Material> savedOverlays)
    {
        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
            return false;

        if (meshInstance.IsInGroup("model_reference_wireframe_overlay"))
            return false;

        if (savedOverlays.ContainsKey(meshInstance))
            return false;

        ShaderMaterial wireframe = GetSelectionWireframeMaterial();
        if (wireframe == null)
            return false;

        savedOverlays[meshInstance] = meshInstance.MaterialOverlay;
        meshInstance.MaterialOverlay = wireframe;
        return true;
    }

    /// <summary>Draws the mesh as its own edges. Replaces the material, so the surface stops filling in.</summary>
    public static bool TryApplyWireframe(MeshInstance3D meshInstance, Dictionary<MeshInstance3D, Material> savedOverrides)
    {
        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
            return false;

        if (meshInstance.IsInGroup("model_reference_wireframe_overlay"))
            return false;

        if (savedOverrides.ContainsKey(meshInstance))
            return false;

        ShaderMaterial wireframe = GetSelectionWireframeMaterial();
        if (wireframe == null)
            return false;

        savedOverrides[meshInstance] = meshInstance.MaterialOverride;
        meshInstance.MaterialOverride = wireframe;
        return true;
    }

    /// <summary>
    /// Puts back the materials TryApplyWireframe replaced - unless something else has taken the
    /// override over since (a material edit while the entity was selected), which is then left alone.
    /// </summary>
    public static void RestoreOverrides(Dictionary<MeshInstance3D, Material> savedOverrides)
    {
        foreach (KeyValuePair<MeshInstance3D, Material> entry in savedOverrides)
        {
            if (entry.Key == null || !GodotObject.IsInstanceValid(entry.Key))
                continue;
            if (_selectionWireframe != null && entry.Key.MaterialOverride != _selectionWireframe)
                continue;
            entry.Key.MaterialOverride = entry.Value;
        }

        savedOverrides.Clear();
    }

    /// <summary>




    public static ShaderMaterial GetSelectionWireframeMaterial()
    {
        if (_selectionWireframe == null)
        {
            Shader shader = GD.Load<Shader>(WireframeShaderPath);
            if (shader == null)
            {
                ViewerLog.PrintErr("[Selection] Missing " + WireframeShaderPath + " - the wireframe highlight needs the project exporting again.");
                return null;
            }
            _selectionWireframe = new ShaderMaterial { Shader = shader };
            _selectionWireframe.SetShaderParameter("wireframe_color", LevelViewerSelection.HighlightGreen);
            _selectionWireframe.RenderPriority = RenderPriority;
        }

        return _selectionWireframe;
    }


    public static bool TryApplyOverlay(
        MeshInstance3D meshInstance,
        Dictionary<MeshInstance3D, Material> savedOverlays,
        HighlightOverlayMode mode)
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
        meshInstance.MaterialOverlay = GetOverlayForMesh(current, mode);
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
