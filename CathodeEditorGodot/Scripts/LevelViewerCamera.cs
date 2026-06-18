using Godot;
using System;
using System.Runtime.InteropServices;

/// <summary>
/// Free camera: WASD/QE move; RMB look; MMB pan; LMB select entity; Ctrl+MMB step into composite instance; - step back hierarchy; 0/8/9 set regular/deep/advanced deep select; 1-4 transform/rotate world/local, 5 none; H hide selected; Shift+H unhide all; scroll adjusts speed; Z frames selection.
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
    private static readonly bool EmbeddedInOpenCage = DetectEmbeddedInOpenCage();
    private Vector2? _embeddedLastScreenMousePos;
    private Vector2? _embeddedLookAnchorScreen;
    private bool _embeddedMouseCaptured;
    private bool _embeddedCursorHiddenForLook;

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

        LevelViewerEnvironment.EnsureViewerEnvironment(this);
        LevelViewerRenderIdleThrottle.NotifyUserActivity();

        SyncAnglesFromTransform();
        Callable.From(SetupHud).CallDeferred();

        _alienScene = GetNodeOrNull<AlienScene>(AlienScenePath);
        _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);
        if (_alienScene != null)
            _alienScene.OnLoaded += OnCompositeLoaded;

        if (EmbeddedInOpenCage)
        {
            // Embedded in OpenCAGE: inherit process mode so low_processor_mode can idle.
            // Input wake is handled via _UnhandledInput below and Win32 polling when not suspended.
        }
    }

    public override void _ExitTree()
    {
        ReleaseEmbeddedMouseCapture();
        ReleaseMouse(this);
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
        if (!IsProcessing())
            SetProcess(true);

        LevelViewerRenderIdleThrottle.NotifyUserActivity();

        if (@event is InputEventMouseButton or InputEventKey)
            EnsureWindowFocus();

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
                else if (keyEvent.Keycode == Key.Key1)
                {
                    SetGizmoMode(LevelViewerTransformGizmo.GizmoMode.TranslateWorld);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key2)
                {
                    SetGizmoMode(LevelViewerTransformGizmo.GizmoMode.TranslateLocal);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key3)
                {
                    SetGizmoMode(LevelViewerTransformGizmo.GizmoMode.RotateWorld);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key4)
                {
                    SetGizmoMode(LevelViewerTransformGizmo.GizmoMode.RotateLocal);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key5)
                {
                    SetGizmoMode(LevelViewerTransformGizmo.GizmoMode.None);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key0)
                {
                    SetDeepSelectMode(PreviewVisibilitySettings.DeepSelectModeKind.None);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key8)
                {
                    SetDeepSelectMode(PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Key9)
                {
                    SetDeepSelectMode(PreviewVisibilitySettings.DeepSelectModeKind.AdvancedDeepSelect);
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Minus)
                {
                    TryStepBackHierarchy();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Escape)
                {
                    TryClearEntitySelection();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.H)
                {
                    if (keyEvent.ShiftPressed)
                        _alienScene?.ClearCompositeScopedHides();
                    else if (_alienScene != null && _alienScene.TryHideSelectedEntity())
                        _commandsEditorConnection?.TryClearEntitySelection();

                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseButton mouseButton when mouseButton.Pressed && !IsScrollWheelButton(mouseButton.ButtonIndex):
                HandleMouseButtonPressed(mouseButton);
                break;
            case InputEventMouseButton mouseButtonReleased when !mouseButtonReleased.Pressed && !IsScrollWheelButton(mouseButtonReleased.ButtonIndex):
                HandleMouseButtonReleased(mouseButtonReleased);
                break;
            case InputEventMouseMotion mouseMotion:
                HandleMouseMotionWithGizmo(mouseMotion);
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
        LevelViewerRenderIdleThrottle.Update();
        if (LevelViewerRenderIdleThrottle.IsSuspended)
        {
            Callable.From(() => SetProcess(false)).CallDeferred();
            return;
        }

        float deltaSeconds = (float)delta;
        Vector3 positionBefore = GlobalPosition;

        if (EmbeddedInOpenCage)
            ProcessEmbeddedMouseDrag(deltaSeconds);

        ApplyKeyboardMovement(deltaSeconds);

        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);
        else
            HidePositionHud();

        if (_speedHudTimer > 0f || _positionHudTimer > 0f)
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
        Viewport viewport = GetViewport();
        if (viewport != null)
            viewport.UseOcclusionCulling = ModelReferenceRenderSettings.UseDistanceCulling;

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

            _alienScene.TryResolveInitialFocusPoint(out _, out bool focusResolved);
            if (focusResolved || i >= 2)
                break;
        }

        if (_alienScene.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
            return;

        if (!ShouldAutoFrameLoadedContent())
            return;

        Vector3 positionBefore = GlobalPosition;
        _alienScene.TryResolveInitialFocusPoint(out Vector3 focusPoint, out bool hasExplicitFocus);
        if (hasExplicitFocus)
            _alienScene.RecenterContentOrigin();
        LevelViewerView.FrameRuntimeCameraOnPoint(
            hasExplicitFocus ? Vector3.Zero : focusPoint,
            this,
            distance: FocusDistanceScale * 8f,
            minDistance: FocusMinDistance,
            maxDistance: FocusMaxDistance);
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
        if (IsMovementKeyDown(Key.Shift))
            speed *= FastMoveMultiplier;

        Basis basis = GlobalTransform.Basis;
        Vector3 forward = -basis.Z;
        Vector3 right = basis.X;

        Vector3 move = Vector3.Zero;
        if (IsMovementKeyDown(Key.W))
            move += forward;
        if (IsMovementKeyDown(Key.S))
            move -= forward;
        if (IsMovementKeyDown(Key.D))
            move += right;
        if (IsMovementKeyDown(Key.A))
            move -= right;
        if (IsMovementKeyDown(Key.E))
            move += Vector3.Up;
        if (IsMovementKeyDown(Key.Q))
            move -= Vector3.Up;

        if (move.LengthSquared() > 0f)
        {
            LevelViewerRenderIdleThrottle.NotifyUserActivity();
            GlobalPosition += move.Normalized() * speed;
        }
    }

    private bool IsMovementKeyDown(Key key)
    {
        if (!EmbeddedInOpenCage)
            return Input.IsKeyPressed(key);

        if (!ShouldAcceptEmbeddedKeyboardInput())
            return false;

        return key switch
        {
            Key.W => Win32Input.IsKeyDown(Win32Input.VK_W),
            Key.A => Win32Input.IsKeyDown(Win32Input.VK_A),
            Key.S => Win32Input.IsKeyDown(Win32Input.VK_S),
            Key.D => Win32Input.IsKeyDown(Win32Input.VK_D),
            Key.E => Win32Input.IsKeyDown(Win32Input.VK_E),
            Key.Q => Win32Input.IsKeyDown(Win32Input.VK_Q),
            Key.Shift => Win32Input.IsKeyDown(Win32Input.VK_SHIFT),
            _ => Input.IsKeyPressed(key),
        };
    }

    private bool ShouldAcceptEmbeddedKeyboardInput()
    {
        IntPtr hwnd = GetNativeWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return false;

        if (_embeddedMouseCaptured)
            return true;

        IntPtr focusHwnd = Win32Input.GetFocus();
        if (focusHwnd != IntPtr.Zero && Win32Input.IsSameOrDescendant(hwnd, focusHwnd))
            return true;

        if (Win32Input.IsMouseOverWindow(hwnd))
        {
            if (focusHwnd == IntPtr.Zero)
                return true;

            IntPtr hostHwnd = Win32Input.GetParent(hwnd);
            if (focusHwnd == hostHwnd)
                return true;
        }

        Window window = GetViewport()?.GetWindow();
        return window != null && window.HasFocus();
    }

    private void HandleMouseButtonPressed(InputEventMouseButton mouseButton)
    {
        EnsureWindowFocus();

        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Left:
                // Let the gizmo consume LMB before the pick/select logic.
                if (TryGizmoMouseDown(mouseButton.Position))
                {
                    GetViewport().SetInputAsHandled();
                    break;
                }
                TryPickSelect(mouseButton.Position);
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Right:
                if (EmbeddedInOpenCage)
                    EnsureWindowFocus();
                else
                {
                    _mouseLookActive = true;
                    CaptureMouse(this);
                }
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Middle:
                if (mouseButton.CtrlPressed)
                {
                    TryPickDrillIntoComposite(mouseButton.Position);
                    GetViewport().SetInputAsHandled();
                }
                else if (EmbeddedInOpenCage)
                {
                    EnsureWindowFocus();
                    GetViewport().SetInputAsHandled();
                }
                else
                {
                    _panning = true;
                    CaptureMouse(this);
                    GetViewport().SetInputAsHandled();
                }
                break;
        }
    }

    private void TryPickSelect(Vector2 screenPosition)
    {
        // Gizmo handles always win over scene geometry at the same screen pixel.
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo != null && gizmo.HitsAtScreen(screenPosition))
            return;

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

    private void TryStepBackHierarchy()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryStepBackHierarchy();
    }

    private void TryClearEntitySelection()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryClearEntitySelection();
    }

    private void HandleMouseButtonReleased(InputEventMouseButton mouseButton)
    {
        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Left:
                if (TryGizmoMouseUp(mouseButton.Position))
                    GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Right:
                if (!EmbeddedInOpenCage)
                {
                    _mouseLookActive = false;
                    if (!_panning)
                        ReleaseMouse(this);
                }
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Middle:
                if (!EmbeddedInOpenCage)
                {
                    _panning = false;
                    if (!_mouseLookActive)
                        ReleaseMouse(this);
                }
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (EmbeddedInOpenCage)
            return;

        ApplyLookRelative(motion.Relative);
        ApplyPanRelative(motion.Relative, motion.Velocity, (float)GetProcessDeltaTime());
    }

    private void ApplyLookRelative(Vector2 relative)
    {
        if (!_mouseLookActive || relative.LengthSquared() <= 0f)
            return;

        LevelViewerRenderIdleThrottle.NotifyUserActivity();
        _yaw -= relative.X * LookSensitivity;
        _pitch -= relative.Y * LookSensitivity;
        _pitch = Mathf.Clamp(_pitch, -1.55f, 1.55f);
        Rotation = new Vector3(_pitch, _yaw, 0f);
    }

    private void ApplyPanRelative(Vector2 relative, Vector2 velocity, float deltaSeconds)
    {
        if (!_panning)
            return;

        Vector3 positionBefore = GlobalPosition;
        if (deltaSeconds <= 0f)
            deltaSeconds = 1f / 60f;

        Basis basis = GlobalTransform.Basis;
        Vector3 right = basis.X;
        Vector3 up = basis.Y;
        if (velocity.LengthSquared() < 0.01f)
            velocity = relative / deltaSeconds;

        Vector3 pan = -right * velocity.X + up * velocity.Y;
        GlobalPosition += pan * MoveSpeed * PanSensitivity * deltaSeconds;
        SyncAnglesFromTransform();
        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);
    }

    private void ProcessEmbeddedMouseDrag(float deltaSeconds)
    {
        IntPtr hwnd = GetNativeWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        bool rightDown = Win32Input.IsKeyDown(Win32Input.VK_RBUTTON);
        bool middleDown = Win32Input.IsKeyDown(Win32Input.VK_MBUTTON);
        bool ctrlDown = Win32Input.IsKeyDown(Win32Input.VK_CONTROL);
        bool dragActive = rightDown || middleDown;

        if (!rightDown)
            EndEmbeddedMouseLook();

        if (!dragActive)
        {
            ReleaseEmbeddedMouseCapture();
            _mouseLookActive = false;
            _panning = false;
            _embeddedLastScreenMousePos = null;
            return;
        }

        _mouseLookActive = rightDown;
        _panning = middleDown && !ctrlDown;

        if (_mouseLookActive)
        {
            ProcessEmbeddedMouseLook(hwnd);
            return;
        }

        ProcessEmbeddedMousePan(hwnd, deltaSeconds);
    }

    private void ProcessEmbeddedMouseLook(IntPtr hwnd)
    {
        if (!_embeddedMouseCaptured)
        {
            if (!Win32Input.IsMouseOverWindow(hwnd))
            {
                _embeddedLastScreenMousePos = null;
                return;
            }

            BeginEmbeddedMouseLook(hwnd);
            return;
        }

        if (_embeddedLookAnchorScreen == null)
        {
            BeginEmbeddedMouseLook(hwnd);
            return;
        }

        if (!Win32Input.TryGetScreenCursorPosition(out Vector2 screenPos))
            return;

        Vector2 anchor = _embeddedLookAnchorScreen.Value;
        Vector2 relative = screenPos - anchor;
        if (relative.LengthSquared() > 0f)
        {
            ApplyLookRelative(relative);
            Win32Input.SetCursorScreenPosition(anchor);
        }

        _embeddedLastScreenMousePos = anchor;
    }

    private void ProcessEmbeddedMousePan(IntPtr hwnd, float deltaSeconds)
    {
        if (!_embeddedMouseCaptured)
        {
            if (!Win32Input.IsMouseOverWindow(hwnd))
            {
                _panning = false;
                _embeddedLastScreenMousePos = null;
                return;
            }

            Win32Input.SetCapture(hwnd);
            _embeddedMouseCaptured = true;
            Win32Input.SetFocus(hwnd);
        }

        if (!Win32Input.TryGetScreenCursorPosition(out Vector2 screenPos))
            return;

        if (_embeddedLastScreenMousePos == null)
        {
            _embeddedLastScreenMousePos = screenPos;
            return;
        }

        Vector2 relative = screenPos - _embeddedLastScreenMousePos.Value;
        _embeddedLastScreenMousePos = screenPos;

        ApplyPanRelative(relative, relative / deltaSeconds, deltaSeconds);
    }

    private void BeginEmbeddedMouseLook(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (!Win32Input.TryGetClientCenterScreen(hwnd, out Vector2 anchor))
            return;

        _embeddedLookAnchorScreen = anchor;
        _embeddedLastScreenMousePos = anchor;
        Win32Input.SetCursorScreenPosition(anchor);
        Win32Input.PushHideCursor();
        _embeddedCursorHiddenForLook = true;

        Win32Input.SetCapture(hwnd);
        _embeddedMouseCaptured = true;
        Win32Input.SetFocus(hwnd);
    }

    private void EndEmbeddedMouseLook()
    {
        if (!_embeddedCursorHiddenForLook)
            return;

        Win32Input.PopHideCursor();
        _embeddedCursorHiddenForLook = false;
        _embeddedLookAnchorScreen = null;
    }

    private void ReleaseEmbeddedMouseCapture()
    {
        EndEmbeddedMouseLook();

        if (!_embeddedMouseCaptured)
            return;

        Win32Input.ReleaseCapture();
        _embeddedMouseCaptured = false;
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

    private static void CaptureMouse(Node context)
    {
        if (EmbeddedInOpenCage)
            return;

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private static void ReleaseMouse(Node context)
    {
        if (EmbeddedInOpenCage)
            return;

        if (Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private static bool DetectEmbeddedInOpenCage()
    {
        if (OS.GetEnvironment("OPENCAGE_EMBEDDED") == "1")
            return true;

        foreach (string arg in OS.GetCmdlineArgs())
        {
            if (arg == "--opencage-embedded")
                return true;
        }

        return false;
    }

    private void EnsureWindowFocus()
    {
        if (EmbeddedInOpenCage)
        {
            IntPtr hwnd = GetNativeWindowHandle(this);
            if (hwnd == IntPtr.Zero)
                return;

            if (Win32Input.GetFocus() != hwnd
                && (_embeddedMouseCaptured || Win32Input.IsMouseOverWindow(hwnd)))
                Win32Input.SetFocus(hwnd);
            return;
        }

        Viewport viewport = GetViewport();
        if (viewport == null)
            return;

        Window window = viewport.GetWindow();
        if (window != null && !window.HasFocus())
            window.GrabFocus();
    }

    private static IntPtr GetNativeWindowHandle(Node context)
    {
        Viewport viewport = context?.GetViewport();
        if (viewport == null)
            return IntPtr.Zero;

        Window window = viewport.GetWindow();
        if (window == null)
            return IntPtr.Zero;

        return (IntPtr)DisplayServer.WindowGetNativeHandle(
            DisplayServer.HandleType.WindowHandle,
            window.GetWindowId());
    }

    private static class Win32Input
    {
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_W = 0x57;
        public const int VK_A = 0x41;
        public const int VK_S = 0x53;
        public const int VK_D = 0x44;
        public const int VK_E = 0x45;
        public const int VK_Q = 0x51;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        public static extern int ShowCursor(bool show);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hwnd);

        public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public static bool HasKeyboardFocus(IntPtr hwnd) => hwnd != IntPtr.Zero && GetFocus() == hwnd;

        public static bool IsSameOrDescendant(IntPtr ancestor, IntPtr window)
        {
            while (window != IntPtr.Zero)
            {
                if (window == ancestor)
                    return true;
                window = GetParent(window);
            }

            return false;
        }

        public static bool IsMouseOverWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetCursorPos(out Point screen))
                return false;

            return IsSameOrDescendant(hwnd, WindowFromPoint(screen));
        }

        public static bool IsAnyMouseButtonDown() =>
            IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON) || IsKeyDown(VK_MBUTTON);

        public static bool TryGetScreenCursorPosition(out Vector2 position)
        {
            position = default;
            if (!GetCursorPos(out Point screen))
                return false;

            position = new Vector2(screen.X, screen.Y);
            return true;
        }

        public static void SetCursorScreenPosition(Vector2 screen)
        {
            SetCursorPos((int)Mathf.Round(screen.X), (int)Mathf.Round(screen.Y));
        }

        public static bool TryGetClientCenterScreen(IntPtr hwnd, out Vector2 center)
        {
            center = default;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out Rect rect))
                return false;

            var clientCenter = new Point
            {
                X = (rect.Left + rect.Right) / 2,
                Y = (rect.Top + rect.Bottom) / 2,
            };

            if (!ClientToScreen(hwnd, ref clientCenter))
                return false;

            center = new Vector2(clientCenter.X, clientCenter.Y);
            return true;
        }

        private static int _cursorHideDepth;

        public static void PushHideCursor()
        {
            if (_cursorHideDepth == 0)
            {
                while (ShowCursor(false) >= 0)
                {
                }
            }

            _cursorHideDepth++;
        }

        public static void PopHideCursor()
        {
            if (_cursorHideDepth <= 0)
                return;

            _cursorHideDepth--;
            if (_cursorHideDepth != 0)
                return;

            while (ShowCursor(true) < 0)
            {
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Transform gizmo integration
    // -------------------------------------------------------------------------

    private LevelViewerTransformGizmo GetGizmo()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        if (_commandsEditorConnection?.TransformGizmo == null)
            _commandsEditorConnection?.EnsureTransformGizmo();

        return _commandsEditorConnection?.TransformGizmo;
    }

    private void SetGizmoMode(LevelViewerTransformGizmo.GizmoMode mode)
    {
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo == null)
            return;

        gizmo.SetMode(mode);

        _commandsEditorConnection?.SyncTransformGizmoToSelection(this);
        _commandsEditorConnection?.SendViewportModeToEditor();
    }

    private bool TryGizmoMouseDown(Vector2 pos)
    {
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo == null || !gizmo.Visible)
            return false;
        return gizmo.HandleMouseButtonDown(pos);
    }

    private bool TryGizmoMouseUp(Vector2 pos)
    {
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo == null)
            return false;
        return gizmo.HandleMouseButtonUp(pos);
    }

    private void HandleMouseMotionWithGizmo(InputEventMouseMotion motion)
    {
        // Always forward motion to the gizmo for hover highlighting (even when camera is not looking)
        LevelViewerTransformGizmo gizmo = GetGizmo();
        bool gizmoConsumed = false;
        if (gizmo != null && gizmo.Visible)
        {
            gizmoConsumed = gizmo.HandleMouseMotion(motion.Position);
        }

        if (gizmoConsumed)
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        // Fall through to camera look / pan (embedded mode polls Win32 input in _Process instead).
        if (!EmbeddedInOpenCage && (_mouseLookActive || _panning))
        {
            HandleMouseMotion(motion);
            GetViewport().SetInputAsHandled();
        }
    }

    private void SetDeepSelectMode(PreviewVisibilitySettings.DeepSelectModeKind mode)
    {
        if (PreviewVisibilitySettings.DeepSelectMode == mode)
            return;

        _commandsEditorConnection?.ResetProgressiveDeepSelectPickState();
        PreviewVisibilitySettings.DeepSelectMode = mode;
        _commandsEditorConnection?.SendViewportModeToEditor();
    }
}
