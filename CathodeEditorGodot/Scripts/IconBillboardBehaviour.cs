using Godot;

/// <summary>
/// Screen-aligned billboard: matches Unity IconBillboardBehaviour (camera rotation, not LookAt position).
/// </summary>
public partial class IconBillboardBehaviour : Node3D
{
    public override void _Process(double delta)
    {
        Camera3D camera = ResolveViewerCamera();
        if (camera == null)
            return;

        GlobalRotation = camera.GlobalRotation;
    }

    internal static Camera3D ResolveViewerCamera(Node fromNode = null)
    {
        Viewport viewport = fromNode?.GetViewport();
        Camera3D camera = viewport?.GetCamera3D();
        if (camera != null)
            return camera;

        SceneTree tree = fromNode?.GetTree() ?? Engine.GetMainLoop() as SceneTree;
        return tree?.Root?.GetNodeOrNull<Camera3D>("Connection/Camera3D");
    }
}
