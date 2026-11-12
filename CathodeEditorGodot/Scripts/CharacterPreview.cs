using CATHODE.Scripting;
using Godot;

public partial class CharacterPreview : FunctionEntityPreview
{
    private static readonly Color CharacterColor = new Color(0.25f, 0.85f, 0.35f, 1f);
    //The facing cue: a dark visor across the front of the head, and a lighter arrow on the ground pointing ahead
    private static readonly Color VisorColor = new Color(0.05f, 0.22f, 0.09f, 1f);
    private static readonly Color ArrowColor = new Color(0.6f, 1f, 0.65f, 1f);

    private const float BodyRadius = 0.28f;
    private const float BodyHeight = 1.55f;
    private const float HeadRadius = 0.18f;
    private const float HeadOffset = 1.65f;

    private const float VisorWidth = 0.26f;
    private const float VisorHeight = 0.07f;
    private const float VisorDepth = 0.12f;
    private const float ArrowLift = 0.02f;          //off the ground, so the floor it stands on doesn't cover it
    private const float ArrowThickness = 0.03f;
    private const float ArrowShaftStart = 0.15f;    //from the character's centre, along the way it faces
    private const float ArrowShaftEnd = 0.55f;
    private const float ArrowShaftWidth = 0.08f;
    private const float ArrowHeadLength = 0.25f;
    private const float ArrowHeadWidth = 0.28f;

    /* Which way a character faces: the entity's own +Z, as the game spawns it (0 yaw faces +Z, 90 faces +X - the convention
       OpenCAGE's character tools use). The entity node carries the entity's rotation converted to Godot, where the Z axis is
       flipped, so in this node's own space the character faces -Z (Godot's Vector3.Forward). */
    private static readonly Vector3 Facing = Vector3.Forward;

    private Node3D _root;
    private MeshInstance3D[] _bodyParts;
    private MeshInstance3D _visor;
    private MeshInstance3D[] _arrowParts;

    protected override Node3D GetVisibilityRoot() => _root;

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyNode(_root);
        _root = null;
        _bodyParts = null;
        _visor = null;
        _arrowParts = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (SyncVisibility(visible, _root))
            return;

        EnsureVisual();

        foreach (MeshInstance3D part in _bodyParts)
            PreviewVisualUtility.ApplyColor(part, CharacterColor);
        PreviewVisualUtility.ApplyColor(_visor, VisorColor);
        foreach (MeshInstance3D part in _arrowParts)
            PreviewVisualUtility.ApplyColor(part, ArrowColor);
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new Node3D { Name = "CharacterPreview" };
        AddChild(_root);

        PrimitiveMesh bodyMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("character:body", () => new CapsuleMesh
        {
            Radius = BodyRadius,
            Height = BodyHeight,
        });
        Node3D body = PreviewVisualUtility.CreatePrimitivePreview("Body", _root, bodyMesh, CharacterColor);
        body.Position = new Vector3(0f, BodyHeight * 0.5f, 0f);

        PrimitiveMesh headMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("character:head", () => new SphereMesh
        {
            Radius = HeadRadius,
            Height = HeadRadius * 2f,
        });
        Node3D head = PreviewVisualUtility.CreatePrimitivePreview("Head", _root, headMesh, CharacterColor);
        head.Position = new Vector3(0f, HeadOffset, 0f);
        _bodyParts = new[] { (MeshInstance3D)body, (MeshInstance3D)head };

        //A visor across the face, standing out of the front of the head: which way it looks, from the side and from above
        PrimitiveMesh visorMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("character:visor", () => new BoxMesh
        {
            Size = new Vector3(VisorWidth, VisorHeight, VisorDepth),
        });
        Node3D visor = PreviewVisualUtility.CreatePrimitivePreview("Visor", _root, visorMesh, VisorColor);
        visor.Position = new Vector3(0f, HeadOffset + 0.02f, 0f) + Facing * (HeadRadius - VisorDepth * 0.5f + 0.03f);
        _visor = (MeshInstance3D)visor;

        //An arrow flat on the ground in front of the feet, pointing the way it faces: readable from above and at a distance
        Node3D arrow = new Node3D { Name = "FacingArrow" };
        _root.AddChild(arrow);
        PrimitiveMesh shaftMesh = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("character:arrow_shaft", () => new BoxMesh
        {
            Size = new Vector3(ArrowShaftWidth, ArrowThickness, ArrowShaftEnd - ArrowShaftStart),
        });
        Node3D shaft = PreviewVisualUtility.CreatePrimitivePreview("Shaft", arrow, shaftMesh, ArrowColor);
        shaft.Position = new Vector3(0f, ArrowLift + ArrowThickness * 0.5f, 0f) + Facing * ((ArrowShaftStart + ArrowShaftEnd) * 0.5f);

        //A prism's triangle stands in its XY plane, point up (+Y), drawn out along Z: laid flat (-90 degrees about X) it
        //points along -Z, the facing, with its depth upright
        PrimitiveMesh headPrism = (PrimitiveMesh)PreviewVisualUtility.GetSharedMesh("character:arrow_head", () => new PrismMesh
        {
            LeftToRight = 0.5f,
            Size = new Vector3(ArrowHeadWidth, ArrowHeadLength, ArrowThickness),
        });
        Node3D tip = PreviewVisualUtility.CreatePrimitivePreview("Tip", arrow, headPrism, ArrowColor);
        tip.Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f);
        tip.Position = new Vector3(0f, ArrowLift + ArrowThickness * 0.5f, 0f) + Facing * (ArrowShaftEnd + ArrowHeadLength * 0.5f);
        _arrowParts = new[] { (MeshInstance3D)shaft, (MeshInstance3D)tip };
    }
}
