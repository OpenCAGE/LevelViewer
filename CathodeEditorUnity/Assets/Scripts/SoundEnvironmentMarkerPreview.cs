using UnityEngine;

public class SoundEnvironmentMarkerPreview : PositionMarkerPreview
{
    protected override float TorusRadius => 0.5f;
    protected override float TubeRadius => 0.05f;

    protected override void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new GameObject("SoundEnvironmentMarkerPreview");
        _root.transform.SetParent(transform, false);

        Mesh outerTorus = PreviewVisualUtility.CreateTorusMesh(TorusRadius, TubeRadius);
        GameObject outer = PreviewVisualUtility.CreateMeshPreview("OuterTorus", _root.transform, outerTorus, Color.white, ref _propertyBlock, opaque: true);

        Mesh innerTorus = PreviewVisualUtility.CreateTorusMesh(TorusRadius * 0.55f, TubeRadius * 0.7f);
        GameObject inner = PreviewVisualUtility.CreateMeshPreview("InnerTorus", _root.transform, innerTorus, Color.white, ref _propertyBlock, opaque: true);

        GameObject core = PreviewVisualUtility.CreatePrimitivePreview("Core", _root.transform, PrimitiveType.Sphere, Color.white, ref _propertyBlock, opaque: true);
        core.transform.localScale = Vector3.one * 0.12f;
    }
}
