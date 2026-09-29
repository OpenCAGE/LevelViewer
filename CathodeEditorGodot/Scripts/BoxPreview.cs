using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using OpenCAGE;

/// <summary>
/// Semi-transparent box volume preview for Box-like function entities.
/// </summary>
public partial class BoxPreview : FunctionEntityPreview
{
    private const string HalfDimensionsParameter = "half_dimensions";

    private Node3D _volume;

    public static bool ShouldShowBoxPreview(FunctionEntity entity, CommandsUtils utils)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        return RenderFilterDefinitions.GetPreviewKind(entity.function.AsFunctionType) == RenderPreviewKind.Box;
    }

    public void Setup(FunctionEntity entity, CommandsUtils utils, uint ownerCompositeId = 0)
    {
        base.Setup(entity, ownerCompositeId);
    }

    protected override Node3D GetVisibilityRoot() => _volume;

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (_volume != null)
            _volume.Visible = visible;
        if (!visible)
            return;

        EnsureVolume();
        ApplyPreviewColor();
        ApplyDimensions(GetHalfDimensions(Entity));
    }

    public void RefreshDimensions()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (_volume != null)
            _volume.Visible = visible;
        if (!visible)
            return;

        EnsureVolume();
        ApplyDimensions(GetHalfDimensions(Entity));
    }

    /* Drawn 10 cm across at the least: a box sized zero (the definitions' default) was drawn 2 cm across - invisible, and
       too small to click, so there was no finding it to give it a size. */
    private const float MinHalfDimension = 0.05f;

    private void ApplyDimensions(Vector3 halfDimensions)
    {
        halfDimensions.X = Mathf.Max(halfDimensions.X, MinHalfDimension);
        halfDimensions.Y = Mathf.Max(halfDimensions.Y, MinHalfDimension);
        halfDimensions.Z = Mathf.Max(halfDimensions.Z, MinHalfDimension);

        _volume.Scale = halfDimensions * 2f;
        _volume.Position = new Vector3(0f, halfDimensions.Y, 0f);
        _volume.Rotation = Vector3.Zero;
    }

    private void ApplyPreviewColor()
    {
        if (_volume == null)
            return;

        MeshInstance3D renderer = _volume as MeshInstance3D;
        if (renderer == null)
            return;

        PreviewVisualUtility.ApplyTransparentColor(renderer, PreviewVisualUtility.GetPreviewColor(Entity));
    }

    private Vector3 GetHalfDimensions(FunctionEntity entity)
    {
        //No size is taken as this by the level build too; a size of zero is an empty volume there, drawn at the floor below
        Parameter halfDimensionsParam = entity.GetParameter(HalfDimensionsParameter);
        if (halfDimensionsParam?.content != null && halfDimensionsParam.content.dataType == DataType.VECTOR)
            return ((cVector3)halfDimensionsParam.content).value;

        return new Vector3(0.5f, 1f, 0.5f);
    }

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyNode(_volume);
        _volume = null;
    }

    private void EnsureVolume()
    {
        if (_volume != null)
            return;

        Mesh box = PreviewVisualUtility.GetSharedMesh("box:unit", () => new BoxMesh { Size = Vector3.One });
        _volume = PreviewVisualUtility.CreateMeshPreview("BoxPreview", this, box, Colors.White, opaque: false);
    }
}
