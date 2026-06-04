using Godot;

/// <summary>
/// Free camera: WASD/QE move; RMB look; MMB pan; LMB select entity; Ctrl+MMB step into composite instance; scroll adjusts speed; Z frames selection.
/// MoveSpeed is world units per second (framerate-independent via delta).
/// </summary>
public partial class LevelViewerCamera : Camera3D
{
    [Export]
    public NodePath AlienScenePath = new NodePath("../AlienScene");

    [Export]
    public bool FrameEditorViewport = true;

    /// <summary>Movement speed in world units per second.</summary>
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

    [Export]
    public float FocusDistanceScale = 1.35f;

    [Export]
    public float FocusMinDistance = 1.5f;

    [Export]
    public float FocusMaxDistance = 64f;

    [Export]
    public float HudFadeSeconds = 2.5f;

    [Export]
    public float PositionDisplayEpsilon = 0.01f;

    [Export]
    public NodePath CommandsEditorConnectionPath = new NodePath("../CommandsEditorConnection");

    private AlienScene _alienScene;
    private CommandsEditorConnection _commandsEditorConnection;
    private float _yaw;
    private float _pitch;
    private bool _mouseLookActive;
    private bool _panning;

    private CanvasLayer _hudLayer;
    private PanelContainer _speedPanel;
    private PanelContainer _positionPanel;
    private Label _speedLabel;
    private Label _positionLabel;
    private float _speedHudTimer;
    private float _positionHudTimer;
    private Vector3 _lastDisplayedPosition;
    private bool _hasDisplayedPosition;

    public override void _Ready()
    {
        Current = true;

        Viewport viewport = GetViewport();
        if (viewport != null)
            viewport.UseOcclusionCulling = false;

        SyncAnglesFromTransform();
        Callable.From(SetupHud).CallDeferred();

        _alienScene = GetNodeOrNull<AlienScene>(AlienScenePath);
        _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);
        if (_alienScene != null)
            _alienScene.OnLoaded += OnCompositeLoaded;
    }

    public override void _ExitTree()
    {
        ReleaseMouse();
        if (_alienScene != null)
            _alienScene.OnLoaded -= OnCompositeLoaded;
        if (_hudLayer != null && GodotObject.IsInstanceValid(_hudLayer))
            _hudLayer.QueueFree();
        base._ExitTree();
    }

    public override void _Input(InputEvent @event)
    {
        if (TryHandleScrollWheel(@event))
            GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (TryHandleScrollWheel(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        switch (@event)
        {
            case InputEventKey keyEvent when keyEvent.Pressed && !keyEvent.Echo:
                if (keyEvent.Keycode == Key.Z)
                {
                    FocusSelectedEntity();
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseButton mouseButton when mouseButton.Pressed && !IsScrollWheelButton(mouseButton.ButtonIndex):
                HandleMouseButtonPressed(mouseButton);
                break;
            case InputEventMouseButton mouseButtonReleased when !mouseButtonReleased.Pressed && !IsScrollWheelButton(mouseButtonReleased.ButtonIndex):
                HandleMouseButtonReleased(mouseButtonReleased);
                break;
            case InputEventMouseMotion mouseMotion when _mouseLookActive || _panning:
                HandleMouseMotion(mouseMotion);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private static bool IsScrollWheelButton(MouseButton button)
    {
        return button == MouseButton.WheelUp || button == MouseButton.WheelDown;
    }

    private bool TryHandleScrollWheel(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton)
            return false;

        if (!IsScrollWheelButton(mouseButton.ButtonIndex))
            return false;

        // Godot emits press+release for the wheel; only act on press.
        if (!mouseButton.Pressed)
            return false;

        AdjustMoveSpeed(mouseButton.ButtonIndex == MouseButton.WheelUp);
        return true;
    }

    public override void _Process(double delta)
    {
        float deltaSeconds = (float)delta;
        Vector3 positionBefore = GlobalPosition;

        ApplyKeyboardMovement(deltaSeconds);
        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);
        else
            HidePositionHud();
        UpdateHudFade(deltaSeconds);
    }

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

        Vector3 positionBefore = GlobalPosition;
        LevelViewerView.FrameRuntimeCameraClose(
            target,
            this,
            FocusDistanceScale,
            FocusMinDistance,
            FocusMaxDistance);
        SyncAnglesFromTransform();
        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);
        _positionHudTimer = ShouldShowCameraPosition() ? HudFadeSeconds : 0f;
    }

    private void FocusSelectedEntity()
    {
        if (_alienScene == null || !_alienScene.TryGetSelectedEntity(out Node3D selected))
            return;

        FocusOnTarget(selected);
    }

    private void OnCompositeLoaded()
    {
        FrameLoadedContentWhenReadyAsync();
    }

    private async void FrameLoadedContentWhenReadyAsync()
    {
        if (_alienScene == null)
            return;

        const int maxFrames = 60;
        for (int i = 0; i < maxFrames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            if (_alienScene.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
                return;

            if (!ShouldAutoFrameLoadedContent())
                return;

            if (LevelViewerView.TryComputeGlobalAabb(_alienScene.ParentNode, out Aabb bounds) && bounds.HasVolume())
                break;
        }

        if (_alienScene.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
            return;

        if (!ShouldAutoFrameLoadedContent())
            return;

        if (!LevelViewerView.TryComputeGlobalAabb(_alienScene.ParentNode, out Aabb contentBounds) || !contentBounds.HasVolume())
            return;

        Vector3 positionBefore = GlobalPosition;
        _alienScene.RecenterContentOrigin();
        LevelViewerView.FrameRuntimeCamera(_alienScene.ParentNode, this);
        SyncAnglesFromTransform();
        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);

#if TOOLS
        if (FrameEditorViewport && Engine.IsEditorHint())
            LevelViewerView.TryFrameEditorOn(_alienScene.ParentNode);
#endif
    }

    /// <summary>
    /// Frame the full loaded composite when focus-on-selected will not drive the camera
    /// (no nested composite path, or focus disabled; entity focus is handled separately).
    /// </summary>
    private bool ShouldAutoFrameLoadedContent()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        if (_commandsEditorConnection == null)
            return true;

        if (!_commandsEditorConnection.FocusSelected)
            return true;

        if (_commandsEditorConnection.HasEntitySelection)
            return false;

        if (_commandsEditorConnection.HasChildCompositeInPath)
            return false;

        return true;
    }

    private void ApplyKeyboardMovement(float deltaSeconds)
    {
        float speed = MoveSpeed * deltaSeconds;
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

    private void HandleMouseButtonPressed(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Left:
                TryPickSelect(mouseButton.Position);
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Right:
                _mouseLookActive = true;
                CaptureMouse();
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Middle:
                if (mouseButton.CtrlPressed)
                {
                    TryPickDrillIntoComposite(mouseButton.Position);
                    GetViewport().SetInputAsHandled();
                }
                else
                {
                    _panning = true;
                    CaptureMouse();
                    GetViewport().SetInputAsHandled();
                }
                break;
        }
    }

    private void TryPickSelect(Vector2 screenPosition)
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryPickSelectAtScreen(this, screenPosition);
    }

    private void TryPickDrillIntoComposite(Vector2 screenPosition)
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryPickDrillIntoCompositeAtScreen(this, screenPosition);
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
            Vector3 positionBefore = GlobalPosition;
            float deltaSeconds = (float)GetProcessDeltaTime();
            if (deltaSeconds <= 0f)
                deltaSeconds = 1f / 60f;

            Basis basis = GlobalTransform.Basis;
            Vector3 right = basis.X;
            Vector3 up = basis.Y;
            Vector2 velocity = motion.Velocity;
            if (velocity.LengthSquared() < 0.01f)
                velocity = motion.Relative / deltaSeconds;

            Vector3 pan = -right * velocity.X + up * velocity.Y;
            GlobalPosition += pan * MoveSpeed * PanSensitivity * deltaSeconds;
            SyncAnglesFromTransform();
            if (ShouldShowCameraPosition())
                UpdatePositionHud(positionBefore);
        }
    }

    private void AdjustMoveSpeed(bool faster)
    {
        float scale = faster ? ScrollSpeedScale : 1f / ScrollSpeedScale;
        MoveSpeed = Mathf.Clamp(MoveSpeed * scale, MinMoveSpeed, MaxMoveSpeed);
        ShowSpeedHud();
    }

    private void SetupHud()
    {
        if (_hudLayer != null && GodotObject.IsInstanceValid(_hudLayer))
            return;

        _hudLayer = new CanvasLayer
        {
            Name = "CameraHud",
            Layer = 100,
        };

        // Parent to scene root so the HUD always uses the main viewport (not tied to Camera3D node).
        Node hudHost = GetTree().CurrentScene ?? GetParent() ?? this;
        if (hudHost == null || !GodotObject.IsInstanceValid(hudHost))
            return;

        hudHost.AddChild(_hudLayer);

        var hudRoot = new Control
        {
            Name = "HudRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hudRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hudLayer.AddChild(hudRoot);

        _speedLabel = CreateHudLabel();
        _speedPanel = WrapHudPanel(_speedLabel);
        _speedPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _speedPanel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _speedPanel.OffsetRight = -12f;
        _speedPanel.OffsetBottom = -12f;
        _speedPanel.OffsetLeft = -280f;
        _speedPanel.OffsetTop = -48f;
        _speedPanel.Visible = false;
        hudRoot.AddChild(_speedPanel);

        _positionLabel = CreateHudLabel();
        _positionPanel = WrapHudPanel(_positionLabel);
        _positionPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _positionPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _positionPanel.OffsetLeft = 12f;
        _positionPanel.OffsetBottom = -12f;
        _positionPanel.OffsetTop = -48f;
        _positionPanel.OffsetRight = 280f;
        _positionPanel.Visible = false;
        hudRoot.AddChild(_positionPanel);
    }

    private static Label CreateHudLabel()
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
        label.AddThemeFontSizeOverride("font_size", 14);
        return label;
    }

    private static PanelContainer WrapHudPanel(Label label)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.1f, 0.85f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(label);
        return panel;
    }

    private void ShowSpeedHud()
    {
        if (_speedLabel == null || _speedPanel == null)
            return;

        _speedLabel.Text = $"Camera speed: {MoveSpeed:0.##} u/s";
        _speedHudTimer = HudFadeSeconds;
        _speedPanel.Visible = true;
        _speedPanel.Modulate = Colors.White;
    }

    private bool ShouldShowCameraPosition()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        return _commandsEditorConnection == null || _commandsEditorConnection.ShowCameraPosition;
    }

    private void HidePositionHud()
    {
        _positionHudTimer = 0f;
        if (_positionPanel != null)
            _positionPanel.Visible = false;
    }

    private void UpdatePositionHud(Vector3 positionBefore)
    {
        if (!ShouldShowCameraPosition() || _positionLabel == null)
            return;

        Vector3 position = GlobalPosition;
        if (_hasDisplayedPosition && position.DistanceSquaredTo(_lastDisplayedPosition) < PositionDisplayEpsilon * PositionDisplayEpsilon
            && position.DistanceSquaredTo(positionBefore) < PositionDisplayEpsilon * PositionDisplayEpsilon)
            return;

        _lastDisplayedPosition = position;
        _hasDisplayedPosition = true;
        _positionLabel.Text = $"X {position.X:0.##}   Y {position.Y:0.##}   Z {position.Z:0.##}";
        _positionHudTimer = HudFadeSeconds;
        if (_positionPanel != null)
        {
            _positionPanel.Visible = true;
            _positionPanel.Modulate = Colors.White;
        }
    }

    private void UpdateHudFade(float deltaSeconds)
    {
        if (_speedHudTimer > 0f && _speedPanel != null)
        {
            _speedHudTimer -= deltaSeconds;
            _speedPanel.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_speedHudTimer / HudFadeSeconds, 0f, 1f));
            if (_speedHudTimer <= 0f)
                _speedPanel.Visible = false;
        }

        if (!ShouldShowCameraPosition())
        {
            HidePositionHud();
            return;
        }

        if (_positionHudTimer > 0f && _positionPanel != null)
        {
            _positionHudTimer -= deltaSeconds;
            _positionPanel.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_positionHudTimer / HudFadeSeconds, 0f, 1f));
            if (_positionHudTimer <= 0f)
                _positionPanel.Visible = false;
        }
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
