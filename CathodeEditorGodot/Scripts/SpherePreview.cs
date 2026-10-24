using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;

/// <summary>
/// Semi-transparent sphere volume preview for Sphere-like function entities (radius float parameter).
/// </summary>
public partial class SpherePreview : FunctionEntityPreview
{
    private const string RadiusParameter = "radius";
    private const float DefaultRadius = 0.5f;
    private const float MinRadius = 0.1f;

    private Node3D _volume;

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
        ApplyRadius(GetRadius(Entity));
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
        ApplyRadius(GetRadius(Entity));
    }

    private void ApplyRadius(float radius)
    {
        radius = Mathf.Max(radius, MinRadius);

        //Mesh is unit-diameter; spheres are centred on the entity position.
        _volume.Scale = Vector3.One * (radius * 2f);
        _volume.Position = Vector3.Zero;
        _volume.Rotation = Vector3.Zero;
    }

    private void ApplyPreviewColor()
    {
        MeshInstance3D renderer = _volume as MeshInstance3D;
        if (renderer == null)
            return;

        PreviewVisualUtility.ApplyTransparentColor(renderer, PreviewVisualUtility.GetPreviewColor(Entity));
    }

    private float GetRadius(FunctionEntity entity)
    {
        Parameter radiusParam = entity.GetParameter(RadiusParameter);
        if (radiusParam?.content != null && radiusParam.content.dataType == DataType.FLOAT)
        {
            float radius = ((cFloat)radiusParam.content).value;
            if (radius > 0f)
                return radius;
        }

        return DefaultRadius;
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

        Mesh sphere = PreviewVisualUtility.GetSharedMesh("sphere:unit:32:16", () => new SphereMesh
        {
            Radius = 0.5f,
            Height = 1f,
            RadialSegments = 32,
            Rings = 16,
        });
        _volume = PreviewVisualUtility.CreateMeshPreview("SpherePreview", this, sphere, Colors.White, opaque: false);
    }
}
