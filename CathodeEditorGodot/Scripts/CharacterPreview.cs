using CATHODE.Scripting;
using Godot;

public partial class CharacterPreview : FunctionEntityPreview
{
    private static readonly Color CharacterColor = new Color(0.25f, 0.85f, 0.35f, 1f);

    private const float BodyRadius = 0.28f;
    private const float BodyHeight = 1.55f;
    private const float HeadRadius = 0.18f;
    private const float HeadOffset = 1.65f;

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
        if (SyncVisibility(visible, _root))
            return;

        EnsureVisual();

        foreach (Node child in _root.GetChildren())
        {
            if (child is MeshInstance3D renderer)
                PreviewVisualUtility.ApplyColor(renderer, CharacterColor, opaque: true);
        }
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new Node3D { Name = "CharacterPreview" };
        AddChild(_root);

        CapsuleMesh bodyMesh = new CapsuleMesh
        {
            Radius = BodyRadius,
            Height = BodyHeight,
        };
        Node3D body = PreviewVisualUtility.CreatePrimitivePreview("Body", _root, bodyMesh, CharacterColor, opaque: true);
        body.Position = new Vector3(0f, BodyHeight * 0.5f, 0f);

        SphereMesh headMesh = new SphereMesh
        {
            Radius = HeadRadius,
            Height = HeadRadius * 2f,
        };
        Node3D head = PreviewVisualUtility.CreatePrimitivePreview("Head", _root, headMesh, CharacterColor, opaque: true);
        head.Position = new Vector3(0f, HeadOffset, 0f);
    }
}
