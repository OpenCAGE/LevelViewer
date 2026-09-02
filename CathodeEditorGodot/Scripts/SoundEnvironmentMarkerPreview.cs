using Godot;

public partial class SoundEnvironmentMarkerPreview : PositionMarkerPreview
{
    protected override float TorusRadius => 0.5f;
    protected override float TubeRadius => 0.05f;

    protected override void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new Node3D { Name = "SoundEnvironmentMarkerPreview" };
        AddChild(_root);

        ArrayMesh outerTorus = PreviewVisualUtility.CreateTorusMesh(TorusRadius, TubeRadius);
        PreviewVisualUtility.CreateMeshPreview("OuterTorus", _root, outerTorus, Colors.White);

        ArrayMesh innerTorus = PreviewVisualUtility.CreateTorusMesh(TorusRadius * 0.55f, TubeRadius * 0.7f);
        PreviewVisualUtility.CreateMeshPreview("InnerTorus", _root, innerTorus, Colors.White);

        PrimitiveMesh coreMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("soundenv:core", () => new SphereMesh { Radius = 0.06f, Height = 0.12f });
        PreviewVisualUtility.CreatePrimitivePreview("Core", _root, coreMesh, Colors.White);
    }
}
