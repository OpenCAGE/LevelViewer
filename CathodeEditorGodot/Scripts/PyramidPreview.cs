using Godot;

/// <summary>
/// Small opaque pyramid gizmo for point-like effect/trigger entities
/// (EFFECT_EntityGenerator, EFFECT_ImpactGenerator, Trigger_AudioOccluded).
/// Coloured via the entity's render filter definition.
/// </summary>
public partial class PyramidPreview : FunctionEntityPreview
{
    private const float BaseHalfWidth = 0.16f;
    private const float Height = 0.32f;

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
        ApplyColors();
    }

    private void ApplyColors()
    {
        Color color = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        foreach (Node child in _root.GetChildren())
        {
            if (child is MeshInstance3D renderer)
                PreviewVisualUtility.ApplyColor(renderer, color);
        }
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new Node3D { Name = "PyramidPreview" };
        AddChild(_root);

        //4-segment cone = square-based pyramid, apex up, base resting on the entity position.
        CylinderMesh pyramid = new CylinderMesh
        {
            TopRadius = 0f,
            BottomRadius = BaseHalfWidth,
            Height = Height,
            RadialSegments = 4,
        };
        Node3D mesh = PreviewVisualUtility.CreatePrimitivePreview("Pyramid", _root, pyramid, Colors.White);
        mesh.Position = new Vector3(0f, Height * 0.5f, 0f);
    }
}
