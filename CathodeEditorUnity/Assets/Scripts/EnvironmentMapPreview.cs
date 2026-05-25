using CATHODE.Scripting;
using UnityEngine;

/// <summary>
/// Environment map entity preview: coloured sphere at the entity origin.
/// </summary>
public class EnvironmentMapPreview : FunctionEntityPreview
{
    private const float SphereRadius = 0.4f;

    private GameObject _root;
    private MaterialPropertyBlock _propertyBlock;

    protected override GameObject GetVisibilityRoot() => _root;

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyObject(_root);
        _root = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (SyncVisibility(visible, _root))
            return;

        EnsureVisual();

        MeshRenderer sphere = _root.transform.Find("Sphere")?.GetComponent<MeshRenderer>();
        if (sphere != null)
            PreviewVisualUtility.ApplyColor(sphere, PreviewVisualUtility.GetOpaquePreviewColor(Entity), ref _propertyBlock, opaque: true);
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new GameObject("EnvironmentMapPreview");
        _root.transform.SetParent(transform, false);

        GameObject sphere = PreviewVisualUtility.CreatePrimitivePreview(
            "Sphere",
            _root.transform,
            PrimitiveType.Sphere,
            Color.white,
            ref _propertyBlock,
            opaque: true);
        sphere.transform.localScale = Vector3.one * (SphereRadius * 2f);
    }
}
