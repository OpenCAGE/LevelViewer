using CATHODE.Scripting;
using Godot;

public partial class PositionMarkerPreview : FunctionEntityPreview
{
    private static readonly Color AxisX = Colors.Red;
    private static readonly Color AxisY = Colors.Green;
    private static readonly Color AxisZ = Colors.Blue;

    protected Node3D _root;
    protected virtual float TorusRadius => 0.24f;
    protected virtual float TubeRadius => 0.028f;
    protected virtual float AxisLength => 0.22f;
    protected virtual float AxisWidth => 0.03f;

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

    protected virtual void ApplyColors()
    {
        Color torusColor = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        foreach (Node child in _root.GetChildren())
        {
            if (child is MeshInstance3D renderer)
            {
                if (child.Name.ToString().StartsWith("Axis"))
                {
                    Color axisColor = GetAxisColor(child.Name);
                    PreviewVisualUtility.ApplyColor(renderer, axisColor);
                }
                else
                {
                    PreviewVisualUtility.ApplyColor(renderer, torusColor);
                }
            }
        }
    }

    private static Color GetAxisColor(StringName axisName)
    {
        switch (axisName.ToString())
        {
            case "AxisX":
                return AxisX;
            case "AxisY":
                return AxisY;
            case "AxisZ":
                return AxisZ;
            default:
                return Colors.White;
        }
    }

    protected virtual void EnsureVisual()
    {
        if (_root != null)
            return;

        // Torus starts white; ApplyColors() (called right after EnsureVisual) recolors it and the
        // RGB axes match the shared marker's axis colours.
        _root = PreviewVisualUtility.CreatePositionStyleMarker(
            "PositionMarkerPreview",
            this,
            Colors.White,
            TorusRadius,
            TubeRadius,
            AxisLength,
            AxisWidth);
    }
}
