using CATHODE.Scripting;
using Godot;

/// <summary>
/// Environment map entity preview: coloured sphere at the entity origin.
/// </summary>
public partial class EnvironmentMapPreview : FunctionEntityPreview
{
    private const float SphereRadius = 0.4f;

    private Node3D _root;

    protected override Node3D GetVisibilityRoot() => _root;

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyNode(_root);
        _root = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (!visible)
        {
            SyncVisibility(false, _root);
            return;
        }

        EnsureVisual();
        SyncVisibility(true, _root);

        MeshInstance3D sphere = _root.GetNodeOrNull<MeshInstance3D>("Sphere");
        if (sphere != null)
            PreviewVisualUtility.ApplyFunctionPreviewColor(sphere, Entity);
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new Node3D { Name = "EnvironmentMapPreview" };
        AddChild(_root);

        PrimitiveMesh sphereMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("envmap:sphere:" + SphereRadius, () => new SphereMesh
        {
            Radius = SphereRadius,
            Height = SphereRadius * 2f,
        });
        PreviewVisualUtility.CreatePrimitivePreview("Sphere", _root, sphereMesh, Colors.White);
    }
}
