using Godot;

/// <summary>
/// Godot editor-style free camera: hold right mouse to look, WASD + Q/E move along view axes.
/// </summary>
public partial class LevelViewerCamera : Camera3D
{
    [Export]
    public NodePath AlienScenePath = new NodePath("../AlienScene");

    [Export]
    public bool FrameEditorViewport = true;

    [Export]
    public float MoveSpeed = 16f;

    [Export]
    public float FastMoveMultiplier = 3f;

    [Export]
    public float LookSensitivity = 0.002f;

    [Export]
    public float PanSensitivity = 0.003f;

    [Export]
    public float MinMoveSpeed = 1f;

    [Export]
    public float MaxMoveSpeed = 5000f;

    [Export]
    public float ScrollSpeedScale = 1.12f;

    private AlienScene _alienScene;
    private float _yaw;
    private float _pitch;
    private bool _mouseLookActive;
    private bool _panning;

    public override void _Ready()
    {
        Current = true;

        Viewport viewport = GetViewport();
        if (viewport != null)
            viewport.UseOcclusionCulling = false;

        SyncAnglesFromTransform();

        _alienScene = GetNodeOrNull<AlienScene>(AlienScenePath);
        if (_alienScene != null)
            _alienScene.OnLoaded += OnCompositeLoaded;
    }

    public override void _ExitTree()
    {
        ReleaseMouse();
        if (_alienScene != null)
            _alienScene.OnLoaded -= OnCompositeLoaded;
        base._ExitTree();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mouseButton when mouseButton.Pressed:
                HandleMouseButtonPressed(mouseButton);
                break;
            case InputEventMouseButton mouseButtonReleased when !mouseButtonReleased.Pressed:
                HandleMouseButtonReleased(mouseButtonReleased);
                break;
            case InputEventMouseMotion mouseMotion when _mouseLookActive || _panning:
                HandleMouseMotion(mouseMotion);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton wheel when wheel.Pressed
                && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown):
                AdjustMoveSpeed(wheel.ButtonIndex == MouseButton.WheelUp);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (!_mouseLookActive)
            return;

        float speed = MoveSpeed * (float)delta;
        if (Input.IsKeyPressed(Key.Shift))
            speed *= FastMoveMultiplier;

        Basis basis = GlobalTransform.Basis;
        Vector3 forward = -basis.Z;
        Vector3 right = basis.X;

        Vector3 move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W))
            move += forward;
        if (Input.IsKeyPressed(Key.S))
            move -= forward;
        if (Input.IsKeyPressed(Key.D))
            move += right;
        if (Input.IsKeyPressed(Key.A))
            move -= right;
        if (Input.IsKeyPressed(Key.E))
            move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q))
            move -= Vector3.Up;

        if (move.LengthSquared() > 0f)
            GlobalPosition += move.Normalized() * speed;
    }

    [Export]
    public float FocusDistanceScale = 1.35f;

    [Export]
    public float FocusMinDistance = 1.5f;

    [Export]
    public float FocusMaxDistance = 64f;

    /// <summary>Sync internal yaw/pitch after external framing (LookAt, etc.).</summary>
    public void SyncAnglesFromTransform()
    {
        Vector3 euler = GlobalRotation;
        _pitch = euler.X;
        _yaw = euler.Y;
    }

    public void FocusOnTarget(Node3D target)
    {
        if (target == null || !GodotObject.IsInstanceValid(target))
            return;

        LevelViewerView.FrameRuntimeCameraClose(
            target,
            this,
            FocusDistanceScale,
            FocusMinDistance,
            FocusMaxDistance);
    }

    private void OnCompositeLoaded()
    {
        if (_alienScene?.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
            return;

        Callable.From(() =>
        {
            _alienScene.RecenterContentOrigin();
            LevelViewerView.FrameAll(_alienScene.ParentNode, this, FrameEditorViewport);
            SyncAnglesFromTransform();
        }).CallDeferred();
    }

    private void HandleMouseButtonPressed(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Right:
                _mouseLookActive = true;
                CaptureMouse();
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Middle:
                _panning = true;
                CaptureMouse();
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void HandleMouseButtonReleased(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Right:
                _mouseLookActive = false;
                if (!_panning)
                    ReleaseMouse();
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Middle:
                _panning = false;
                if (!_mouseLookActive)
                    ReleaseMouse();
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (_mouseLookActive)
        {
            _yaw -= motion.Relative.X * LookSensitivity;
            _pitch -= motion.Relative.Y * LookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -1.55f, 1.55f);
            Rotation = new Vector3(_pitch, _yaw, 0f);
        }

        if (_panning)
        {
            Basis basis = GlobalTransform.Basis;
            Vector3 right = basis.X;
            Vector3 up = basis.Y;
            float scale = Mathf.Max(MoveSpeed * PanSensitivity, 0.05f);
            GlobalPosition += (-right * motion.Relative.X + up * motion.Relative.Y) * scale;
            SyncAnglesFromTransform();
        }
    }

    private void AdjustMoveSpeed(bool faster)
    {
        float scale = faster ? ScrollSpeedScale : 1f / ScrollSpeedScale;
        MoveSpeed = Mathf.Clamp(MoveSpeed * scale, MinMoveSpeed, MaxMoveSpeed);
    }

    private static void CaptureMouse()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private static void ReleaseMouse()
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }
}
