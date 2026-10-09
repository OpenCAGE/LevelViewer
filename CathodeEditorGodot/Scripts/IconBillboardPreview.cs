using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;

/// <summary>
/// Lightweight preview using Godot editor (or fallback) icons on camera-facing billboards. A camera's icon also has a small
/// frustum, so which way it looks (and how wide) shows.
/// </summary>
public partial class IconBillboardPreview : FunctionEntityPreview
{
    public enum IconKind
    {
        Sound,
        SoundObject,
        Light,
        Particle,
        Camera,
        UIIcon,
    }

    private IconKind _iconKind;
    private Node3D _billboard;

    //A CameraResource's frustum: drawn out to FrustumDepth along the way it looks, as wide as its 'fov' (vertical degrees)
    private const string FovParameter = "fov";
    private const float DefaultFov = 45f;
    private const float FrustumDepth = 0.6f;
    private const float FrustumAspect = 16f / 9f;
    private const float FrustumLineWidth = 0.012f;
    private Node3D _frustum;
    private float _frustumFov = -1f;

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
        PreviewVisualUtility.DestroyNode(_frustum);
        _frustum = null;
        _frustumFov = -1f;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (!visible)
        {
            SyncVisibility(false, _billboard);
            if (_frustum != null && GodotObject.IsInstanceValid(_frustum))
                _frustum.Visible = false;
            return;
        }

        EnsureBillboard();
        SyncVisibility(true, _billboard);
        if (_iconKind == IconKind.Camera)
            EnsureFrustum();
    }

    public override void RefreshVisibility()
    {
        base.RefreshVisibility();
        //The frustum is not under the billboard (which is scaled to the icon's size): it follows it here
        if (_frustum != null && GodotObject.IsInstanceValid(_frustum))
            _frustum.Visible = _billboard != null && GodotObject.IsInstanceValid(_billboard) && _billboard.Visible;
    }

    private void EnsureBillboard()
    {
        if (_billboard != null && GodotObject.IsInstanceValid(_billboard))
        {
            _billboard.Scale = Vector3.One * PreviewVisualUtility.IconBillboardWorldSize;
            return;
        }

        Texture2D icon = EditorIconTextures.Get(_iconKind);
        if (icon == null)
            return;

        // Unity uses full-color editor icons (white material tint), not the render-filter colour.
        _billboard = PreviewVisualUtility.CreateIconBillboard(
            "IconBillboard",
            this,
            icon,
            Colors.White,
            PreviewVisualUtility.IconBillboardWorldSize);
    }

    /* Lines from the camera out to the four corners of a 16:9 frame FrustumDepth ahead, and round that frame. The entity looks
       along its +Z in CATHODE's axes, which is -Z here (Z is negated between the two). Built again when the fov changes. */
    private void EnsureFrustum()
    {
        float fov = GetFov(Entity);
        if (_frustum != null && GodotObject.IsInstanceValid(_frustum))
        {
            if (Mathf.IsEqualApprox(fov, _frustumFov))
            {
                _frustum.Visible = true;
                return;
            }
            PreviewVisualUtility.DestroyNode(_frustum);
        }

        _frustumFov = fov;
        _frustum = new Node3D { Name = "CameraFrustum" };
        AddChild(_frustum);

        float halfHeight = FrustumDepth * Mathf.Tan(Mathf.DegToRad(fov * 0.5f));
        float halfWidth = halfHeight * FrustumAspect;
        Vector3[] corners =
        {
            new Vector3(-halfWidth, halfHeight, -FrustumDepth),
            new Vector3(halfWidth, halfHeight, -FrustumDepth),
            new Vector3(halfWidth, -halfHeight, -FrustumDepth),
            new Vector3(-halfWidth, -halfHeight, -FrustumDepth),
        };
        Color color = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        for (int i = 0; i < corners.Length; i++)
        {
            PreviewVisualUtility.CreateLineSegment("Ray" + i, _frustum, Vector3.Zero, corners[i], FrustumLineWidth, color);
            PreviewVisualUtility.CreateLineSegment("Edge" + i, _frustum, corners[i], corners[(i + 1) % corners.Length], FrustumLineWidth, color);
        }
        //Which way is up in the picture: a tick on the top edge
        PreviewVisualUtility.CreateLineSegment("Up", _frustum, new Vector3(0f, halfHeight, -FrustumDepth), new Vector3(0f, halfHeight * 1.35f, -FrustumDepth), FrustumLineWidth, color);
        RegisterPickablesWithOwner();
    }

    private static float GetFov(FunctionEntity entity)
    {
        Parameter fovParam = entity?.GetParameter(FovParameter);
        if (fovParam?.content != null && fovParam.content.dataType == DataType.FLOAT)
        {
            float fov = ((cFloat)fovParam.content).value;
            if (fov > 1f && fov < 179f)
                return fov;
        }

        return DefaultFov;
    }
}
