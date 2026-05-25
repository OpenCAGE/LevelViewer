using CATHODE.Scripting;
using Godot;

/// <summary>
/// Lightweight preview using Godot editor (or fallback) icons on camera-facing billboards.
/// </summary>
public partial class IconBillboardPreview : FunctionEntityPreview
{
    public enum IconKind
    {
        Sound,
        Light,
        Particle,
        Camera,
    }

    private IconKind _iconKind;
    private IconBillboardBehaviour _billboard;

    public void Setup(FunctionEntity entity, IconKind iconKind, uint ownerCompositeId = 0)
    {
        _iconKind = iconKind;
        base.Setup(entity, ownerCompositeId);
    }

    protected override Node3D GetVisibilityRoot() => _billboard;

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyNode(_billboard);
        _billboard = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (!visible)
        {
            SyncVisibility(false, _billboard);
            return;
        }

        EnsureBillboard();
        SyncVisibility(true, _billboard);
    }

    private void EnsureBillboard()
    {
        if (_billboard != null)
            return;

        Texture2D icon = EditorIconTextures.Get(_iconKind);
        if (icon == null)
            return;

        // Unity uses full-color editor icons (white material tint), not the render-filter colour.
        _billboard = PreviewVisualUtility.CreateIconBillboard("IconBillboard", this, icon, Colors.White);
    }
}
