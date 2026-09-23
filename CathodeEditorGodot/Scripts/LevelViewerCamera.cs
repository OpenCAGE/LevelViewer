using Godot;
using System;
using System.Runtime.InteropServices;

/// <summary>
/// Free camera: WASD/QE move; RMB drag look, RMB click context menu; MMB pan; LMB select entity, LMB drag box-select (Ctrl adds, Shift toggles); Ctrl+MMB step into composite instance; - step back hierarchy; 0/8/9 set regular/deep/advanced deep select; 1-4 transform/rotate world/local, 5 none; Alt+1-4 set the selection highlight mode; H hide selected; Shift+H unhide all; scroll adjusts speed; Z frames selection; Ctrl+D duplicates the selection; Ctrl+Z/Ctrl+Y undo/redo in OpenCAGE.
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

    private Node3D _followTarget;
    private Vector3 _followLastTargetPosition;
    private bool _followActive;

    //Left press off the gizmo: a click, or a box once the mouse moves far enough (issue 703)
    private readonly LevelViewerBoxSelect _boxSelect = new LevelViewerBoxSelect();

    /* Right press: a click opens the context menu, a drag looks about (issue 704). The look waits until the
       mouse has moved DragThresholdPixels from the press, or the camera has flown, so a click never turns
       the view - and embedded, never hides the cursor and throws it to the middle of the window either.
       The menu itself is OpenCAGE's, drawn over this window (CommandsEditorConnection.IsEditorContextMenuUp):
       this side says where and what for, and while it is up hands its next click or key to it. */
    private bool _rightPressed;       //this window saw the right button go down, and not yet come up
    private bool _rightClickPending;  //and letting it go opens the context menu: no look, no other button since
    private bool _rightDragStarted;   //the right button held has moved or flown far enough to be a look
    private Vector2 _rightPressPosition;
    private Vector2? _rightDownScreen; //embedded: the cursor on screen when the right button went down, for the polled look
    private Vector2 _contextMenuPosition; //where the menu was asked for: Step Into Composite drills there

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
        try
        {
            UnhandledInputInternal(@event);
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] Camera _UnhandledInput failed: " + ex);
        }
    }

    private void UnhandledInputInternal(InputEvent @event)
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

        /* OpenCAGE's context menu is up over this window, and this is the click or key that closes it - the
           menu itself never sees input that lands here. It does nothing else: no select, no box, no look, no
           shortcut, and no second menu for a right click. (A right press held and dragged from here still
           becomes a look: embedded, the polled look waits for the drag on its own.) */
        if (IsEditorContextMenuUp
            && ((@event is InputEventKey menuKey && menuKey.Pressed && !menuKey.Echo)
                || (@event is InputEventMouseButton menuButton && menuButton.Pressed && !IsScrollWheelButton(menuButton.ButtonIndex))))
        {
            _commandsEditorConnection?.DismissEditorContextMenu();
            GetViewport().SetInputAsHandled();
            return;
        }

        switch (@event)
        {
            case InputEventKey keyEvent when keyEvent.Pressed && !keyEvent.Echo:
                if (keyEvent.Keycode == Key.C && keyEvent.CtrlPressed)
                {
                    _commandsEditorConnection?.SendEntityClipboardCopy();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.V && keyEvent.CtrlPressed)
                {
                    _commandsEditorConnection?.SendEntityClipboardPaste();
                    GetViewport().SetInputAsHandled();
                }
                /* Duplicate in place, as the context menu's Duplicate does: the copies land on the originals and
                   come back selected, for the gizmo to move (a Shift-drag on a handle is this with the drag
                   already under way). D is a movement key, but flying stops while Ctrl is held. */
                else if (keyEvent.Keycode == Key.D && keyEvent.CtrlPressed
                    && !keyEvent.AltPressed && !keyEvent.ShiftPressed)
                {
                    _commandsEditorConnection?.SendEntityDuplicateRequest();
                    GetViewport().SetInputAsHandled();
                }
                /* Same chords, and the same Alt exclusion, as OpenCAGE.Undo.UndoKeys - a step undone from
                   the viewport and one undone from a panel should take the same keys. */
                else if (keyEvent.CtrlPressed && !keyEvent.AltPressed
                    && keyEvent.Keycode == Key.S && !keyEvent.ShiftPressed)
                {
                    _commandsEditorConnection?.SendSaveRequest();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.CtrlPressed && !keyEvent.AltPressed
                    && keyEvent.Keycode == Key.S && keyEvent.ShiftPressed)
                {
                    _commandsEditorConnection?.SendSaveAndBuildRequest();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.CtrlPressed && !keyEvent.AltPressed
                    && keyEvent.Keycode == Key.Z && !keyEvent.ShiftPressed)
                {
                    _commandsEditorConnection?.SendUndoRequest();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.CtrlPressed && !keyEvent.AltPressed
                    && (keyEvent.Keycode == Key.Y || (keyEvent.Keycode == Key.Z && keyEvent.ShiftPressed)))
                {
                    _commandsEditorConnection?.SendRedoRequest();
                    GetViewport().SetInputAsHandled();
                }
                /* The four selection highlight modes, in the order they sit in OpenCAGE's menu. Alt and a
                   number because the bare numbers are taken by the gizmo and selection modes, and Shift
                   is the camera's speed modifier (issue 673). */
                else if (keyEvent.AltPressed && !keyEvent.CtrlPressed && !keyEvent.MetaPressed
                    && keyEvent.Keycode >= Key.Key1 && keyEvent.Keycode <= Key.Key4)
                {
                    SetHighlightMode((OpenCAGE.UnityConnection.LevelViewerHighlightMode)(keyEvent.Keycode - Key.Key1));
                    GetViewport().SetInputAsHandled();
                }
                /* Shift+End rests the selected entities on the floor (Unreal's End). A Shift-only chord,
                   so it sits above the bare-key guard - Shift is the camera speed modifier, not a lone
                   binding, so it never collides with one. */
                else if (keyEvent.Keycode == Key.End && keyEvent.ShiftPressed
                    && !keyEvent.CtrlPressed && !keyEvent.AltPressed && !keyEvent.MetaPressed)
                {
                    _commandsEditorConnection?.SnapSelectionToFloor();
                    GetViewport().SetInputAsHandled();
                }
                /* Everything below is a bare key, so a chord must not fall into it. Ctrl+Z used to land
                   on Z and fly the camera at the selection instead of undoing (issue 667), and the same
                   trap sits under Ctrl+Delete and the rest of them. */
                else if (keyEvent.CtrlPressed || keyEvent.AltPressed || keyEvent.MetaPressed)
                {
                    break;
                }
                else if (keyEvent.Keycode == Key.Z)
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
                    // Escape leaves creation mode; with no mode to leave, it clears the selection. Mid-press
                    // (a box being drawn, or a click not yet let go) it abandons the press instead.
                    if (_boxSelect.IsArmed)
                        _boxSelect.End();
                    else if (ExitCreateModeIfActive())
                        _commandsEditorConnection?.SendViewportModeToEditor();
                    else
                        TryClearEntitySelection();

                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.Delete)
                {
                    TryDeleteEntitySelection();
                    GetViewport().SetInputAsHandled();
                }
                else if (keyEvent.Keycode == Key.H)
                {
                    if (keyEvent.ShiftPressed)
                        UnhideAllEntities();
                    else
                        HideSelectedEntity();

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
        try
        {
            ProcessInternal(delta);
        }
        catch (Exception ex)
        {
            // A per-frame exception (e.g. a disposed node surfacing as ObjectDisposedException)
            // must never escape the engine callback and terminate the process.
            ViewerLog.PrintErr("[Viewer] Camera _Process failed: " + ex);
        }
    }

    private void ProcessInternal(double delta)
    {
        /* An open context menu is the user busy in the viewport, though the menu has the mouse rather than
           this. Left to idle, the embedded viewer would hand keyboard focus back to OpenCAGE, and the key
           that is meant to close the menu would land there instead. */
        if (IsEditorContextMenuUp)
            LevelViewerRenderIdleThrottle.NotifyUserActivity();

        LevelViewerRenderIdleThrottle.Update();
        if (LevelViewerRenderIdleThrottle.IsSuspended)
        {
            Callable.From(() => SetProcess(false)).CallDeferred();
            return;
        }

        if (LevelViewerRenderIdleThrottle.IsRenderIdle)
        {
            ReleaseEmbeddedMouseCapture();
            return;
        }

        float deltaSeconds = (float)delta;
        Vector3 positionBefore = GlobalPosition;

        if (EmbeddedInOpenCage)
            ProcessEmbeddedMouseDrag(deltaSeconds);

        ApplyKeyboardMovement(deltaSeconds);
        UpdateSelectionFollow();

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

    /// <param name="framePositionOnly">
    /// Go to where the target stands instead of fitting its bounds, for something too big to frame
    /// without backing out to a view of the level.
    /// </param>
    public void FocusOnTarget(Node3D target, bool framePositionOnly = false)
    {
        if (target == null || !GodotObject.IsInstanceValid(target))
            return;

        Vector3 positionBefore = GlobalPosition;
        if (framePositionOnly)
            LevelViewerView.FrameRuntimeCameraOnPoint(
                target.GlobalPosition,
                this,
                distance: Mathf.Max(FocusMinDistance, 12f),
                minDistance: FocusMinDistance,
                maxDistance: FocusMaxDistance);
        else
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

    /// <summary>
    /// Frame the selection; optionally stick the camera so it tracks entity translation until the user moves it.
    /// </summary>
    public void HandleSelectionFocus(Node3D target, bool fixCamera, bool framePositionOnly = false)
    {
        if (target == null || !GodotObject.IsInstanceValid(target))
        {
            ClearSelectionFollow();
            return;
        }

        FocusOnTarget(target, framePositionOnly);
        if (fixCamera)
            BeginSelectionFollow(target);
        else
            ClearSelectionFollow();
    }

    /// <summary>Track the target's translation from wherever the camera is now, without reframing.</summary>
    public void FollowSelectionWithoutFraming(Node3D target)
    {
        BeginSelectionFollow(target);
    }

    public void ClearSelectionFollow()
    {
        _followActive = false;
        _followTarget = null;
    }

    private void BeginSelectionFollow(Node3D target)
    {
        if (target == null || !GodotObject.IsInstanceValid(target))
        {
            ClearSelectionFollow();
            return;
        }

        _followTarget = target;
        _followLastTargetPosition = target.GlobalPosition;
        _followActive = true;
    }

    private void UpdateSelectionFollow()
    {
        if (!_followActive)
            return;

        if (_followTarget == null || !GodotObject.IsInstanceValid(_followTarget))
        {
            ClearSelectionFollow();
            return;
        }

        Vector3 targetPosition = _followTarget.GlobalPosition;
        Vector3 delta = targetPosition - _followLastTargetPosition;
        if (delta.LengthSquared() > 0.0000001f)
            GlobalPosition += delta;

        _followLastTargetPosition = targetPosition;
    }

    private void ReleaseFollowOnUserCameraMove()
    {
        if (_followActive)
            ClearSelectionFollow();
    }

    /// <summary>Explicit focus (the Z key). Asked for directly, so there is no size limit - an instance
    /// frames its whole subtree; an alias or proxy still frames what it points at rather than its
    /// reference point.</summary>
    private void FocusSelectedEntity()
    {
        if (_alienScene == null || !_alienScene.TryGetSelectedEntity(out Node3D selected))
            return;

        if (!_alienScene.TryResolveFocusTarget(selected, maxExtent: 0f, out Node3D target, out bool framePositionOnly))
            return;

        bool fixCamera = _commandsEditorConnection != null
            && GodotObject.IsInstanceValid(_commandsEditorConnection)
            && _commandsEditorConnection.FixCameraToSelected;
        HandleSelectionFocus(target, fixCamera, framePositionOnly);
    }

    private void OnCompositeLoaded()
    {
        // Occlusion culling stays off (set in _Ready): the scene has no OccluderInstance3D, so turning it on for a big
        // level could hide nothing and only paid for the CPU depth pyramid - which is where the engine crashed
        // (crash dashboard 0.18.0.41 #405, HZBuffer::update_mips on a 62k-mesh level). Distance culling is separate.

        FrameLoadedContentWhenReadyAsync();
    }

    private async void FrameLoadedContentWhenReadyAsync()
    {
        // async void: an escaping exception becomes unobserved and can terminate the process.
        try
        {
            await FrameLoadedContentWhenReadyCoreAsync();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] FrameLoadedContentWhenReady failed: " + ex);
        }
    }

    private async System.Threading.Tasks.Task FrameLoadedContentWhenReadyCoreAsync()
    {
        if (_alienScene == null)
            return;

        int contentGeneration = _alienScene.ContentGeneration;

        const int maxFrames = 60;
        for (int i = 0; i < maxFrames; i++)
        {
            SceneTree tree = GetTree();
            if (tree == null)
                return;

            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);

            if (_alienScene == null || _alienScene.ContentGeneration != contentGeneration)
                return;

            if (_alienScene.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
                return;

            _alienScene.TryResolveInitialFocusPoint(out _, out bool focusResolved);
            if (focusResolved || i >= 2)
                break;
        }

        if (_alienScene == null || _alienScene.ContentGeneration != contentGeneration)
            return;

        if (_alienScene.ParentNode == null || !GodotObject.IsInstanceValid(_alienScene.ParentNode))
            return;

        Vector3 positionBefore = GlobalPosition;
        _alienScene.TryResolveInitialFocusPoint(out Vector3 focusPoint, out bool hasExplicitFocus);
        if (hasExplicitFocus)
            _alienScene.RecenterContentOrigin();
        Vector3 framePoint = hasExplicitFocus ? Vector3.Zero : focusPoint;
        float framingDistance = ResolveContentFramingDistance(framePoint);
        LevelViewerView.FrameRuntimeCameraOnPoint(
            framePoint,
            this,
            distance: framingDistance,
            minDistance: Mathf.Min(FocusMinDistance, framingDistance),
            maxDistance: FocusMaxDistance);
        if (ShouldShowCameraPosition())
            UpdatePositionHud(positionBefore);

#if TOOLS
        if (FrameEditorViewport && Engine.IsEditorHint())
            LevelViewerView.TryFrameEditorOn(_alienScene.ParentNode);
#endif
    }

    /// <summary>
    /// Composites whose meshes all sit within a few metres of the focus point (pickups, single
    /// props) are unreadable at the default framing distance - move in so they fill the view.
    /// Larger scenes keep the default: the full bounds of a room or level would push the camera
    /// out, not in.
    /// </summary>
    private float ResolveContentFramingDistance(Vector3 framePoint)
    {
        float defaultDistance = FocusDistanceScale * 8f;
        const float compactContentRadius = 3.5f;
        const float minCompactDistance = 0.5f;
        if (_alienScene == null
            || !_alienScene.TryGetModelReferenceBoundsRadius(framePoint, out float contentRadius)
            || contentRadius >= compactContentRadius)
        {
            return defaultDistance;
        }

        return Mathf.Max(minCompactDistance, contentRadius * 2.5f + 0.4f);
    }

    private void ApplyKeyboardMovement(float deltaSeconds)
    {
        bool ctrlDown = EmbeddedInOpenCage ? Win32Input.IsKeyDown(Win32Input.VK_CONTROL) : Input.IsKeyPressed(Key.Ctrl);
        if (ctrlDown) return;

        //OpenCAGE's menu is up: the next key closes it, it doesn't fly
        if (IsEditorContextMenuUp) return;

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
            //Flying with the right button held makes it a look: letting go opens no menu
            if (_rightPressed || (EmbeddedInOpenCage && Win32Input.IsKeyDown(Win32Input.VK_RBUTTON)))
                StartRightDrag();

            LevelViewerRenderIdleThrottle.NotifyUserActivity();
            ReleaseFollowOnUserCameraMove();
            GlobalPosition += move.Normalized() * speed;
        }
    }

    /// <summary>
    /// V held for vertex snapping. Polled like the movement keys: while embedded, Godot never sees a
    /// key whose event went to the WinForms host, so Win32 GetAsyncKeyState is the reliable source.
    /// </summary>
    private bool IsVertexSnapKeyDown()
    {
        if (!EmbeddedInOpenCage)
            return Input.IsKeyPressed(Key.V);

        return ShouldAcceptEmbeddedKeyboardInput() && Win32Input.IsKeyDown(Win32Input.VK_V);
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

        //Another button while the right one is down: that right click was for something else, not the menu
        if (mouseButton.ButtonIndex != MouseButton.Right)
            _rightClickPending = false;

        switch (mouseButton.ButtonIndex)
        {
            case MouseButton.Left:
                if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
                    _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

                // Creation mode: clicks place a new entity instead of selecting.
                if (_commandsEditorConnection != null && _commandsEditorConnection.CreateModeActive)
                {
                    _commandsEditorConnection.TryCreateEntityAtScreen(this, mouseButton.Position);
                    GetViewport().SetInputAsHandled();
                    break;
                }
                // Let the gizmo consume LMB before the pick/select logic. Shift on a handle means
                // shift-clone (duplicate then drag the copy) - except in Animation Mode, where a drag
                // is a keyframe and Shift stays a plain drag.
                bool duplicateDrag = mouseButton.ShiftPressed && !mouseButton.CtrlPressed
                    && !mouseButton.AltPressed && !AnimationPreview.Active;
                if (TryGizmoMouseDown(mouseButton.Position, duplicateDrag))
                {
                    GetViewport().SetInputAsHandled();
                    break;
                }
                /* Ctrl adds what you click to the selection; shift takes it back out again. Neither
                   drills, so what they pick is always something in the composite on screen. Nothing is
                   picked yet: a drag from here is a box, so the click waits for the button to come
                   back up (FinishBoxSelectOrClick). */
                ArmBoxSelect(
                    mouseButton.Position,
                    mouseButton.CtrlPressed
                        ? CommandsEditorConnection.SelectionChange.Add
                        : mouseButton.ShiftPressed
                            ? CommandsEditorConnection.SelectionChange.Toggle
                            : CommandsEditorConnection.SelectionChange.Replace);
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Right:
            {
                /* A click (the context menu) until the mouse moves or the camera flies; then the look it always
                   was. Pressed while a left press is still waiting to become a click or a box, or mid gizmo
                   drag, it is there to call that off and opens nothing. */
                bool interrupts = _boxSelect.IsArmed
                    || (_commandsEditorConnection != null && GodotObject.IsInstanceValid(_commandsEditorConnection)
                        && _commandsEditorConnection.TransformGizmo != null
                        && _commandsEditorConnection.TransformGizmo.IsDragging);
                _boxSelect.End();
                _rightPressed = true;
                _rightClickPending = !interrupts;
                _rightDragStarted = false;
                _rightPressPosition = mouseButton.Position;
                if (EmbeddedInOpenCage)
                {
                    _rightDownScreen = Win32Input.TryGetScreenCursorPosition(out Vector2 cursor) ? cursor : null;
                    EnsureWindowFocus();
                }
                GetViewport().SetInputAsHandled();
                break;
            }
            case MouseButton.Middle:
                _boxSelect.End();
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

    private void TryPickSelect(Vector2 screenPosition,
        CommandsEditorConnection.SelectionChange change = CommandsEditorConnection.SelectionChange.Replace)
    {
        // Gizmo handles always win over scene geometry at the same screen pixel.
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo != null && gizmo.HitsAtScreen(screenPosition))
            return;

        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryPickSelectAtScreen(this, screenPosition, change);
    }

    /// <summary>
    /// The left button went down off the gizmo. Whether that is a click or the corner of a box is known
    /// once the mouse moves far enough or the button comes back up. A press on a handle the gizmo
    /// didn't take stays what it always was - nothing.
    /// </summary>
    private void ArmBoxSelect(Vector2 screenPosition, CommandsEditorConnection.SelectionChange change)
    {
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo != null && gizmo.HitsAtScreen(screenPosition))
            return;

        _boxSelect.Arm(screenPosition, change);
    }

    /// <summary>Drag the box's far corner. False when no press is waiting to become a box.</summary>
    private bool TryUpdateBoxSelect(InputEventMouseMotion motion)
    {
        if (!_boxSelect.IsArmed)
            return false;

        //The button came up somewhere this window never heard about: drop the press rather than leave a box stuck to the mouse
        if ((motion.ButtonMask & MouseButtonMask.Left) == 0)
        {
            _boxSelect.End();
            return false;
        }

        _boxSelect.Update(motion.Position, GetViewport(), _hudLayer);
        return true;
    }

    /// <summary>The left button came back up: select what the box holds, or pick at the press if it never became one.</summary>
    private void FinishBoxSelectOrClick(Vector2 releasePosition)
    {
        _boxSelect.Update(releasePosition, GetViewport(), _hudLayer);
        bool isBox = _boxSelect.IsActive;
        Vector2 pressPosition = _boxSelect.PressPosition;
        CommandsEditorConnection.SelectionChange change = _boxSelect.Change;
        Rect2 box = _boxSelect.GetBox(GetViewport());
        _boxSelect.End();

        if (!isBox)
        {
            TryPickSelect(pressPosition, change);
            return;
        }

        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.TryBoxSelectAtScreen(this, box, change);
    }

    /// <summary>
    /// The right button has moved or flown far enough from its press to be a look rather than a click. Not
    /// embedded, the look starts here; embedded, the Win32 poll starts it (<see cref="HasRightDragStarted"/>).
    /// </summary>
    private void StartRightDrag()
    {
        _rightDragStarted = true;
        _rightClickPending = false;
        if (!EmbeddedInOpenCage && _rightPressed && !_mouseLookActive)
        {
            _mouseLookActive = true;
            CaptureMouse(this);
        }
    }

    /// <summary>
    /// A right click: select what it landed on (unless that is selected already), then ask OpenCAGE for the
    /// context menu there, saying which entries have anything to act on so it can grey out the rest. The
    /// menu is drawn on that side; what it chooses comes back through <see cref="RunViewportAction"/>, or
    /// is done over there. With no OpenCAGE connected (the standalone viewer) there is no menu.
    /// </summary>
    private void RequestContextMenu(Vector2 screenPosition)
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        CommandsEditorConnection connection = _commandsEditorConnection;
        if (connection == null)
            return;

        connection.SelectForContextMenuAtScreen(this, screenPosition);

        _contextMenuPosition = screenPosition;
        connection.SendViewportContextMenuRequest(
            this,
            screenPosition,
            canFocus: CanFocusSelectedEntity(),
            canHide: _alienScene != null && _alienScene.CanHideSelectedEntity(),
            canUnhideAll: LevelViewerEntityHide.HasAny);
    }

    /// <summary>OpenCAGE's context menu is up over this window: the next click or key here closes it and does nothing else.</summary>
    private bool IsEditorContextMenuUp =>
        _commandsEditorConnection != null
        && GodotObject.IsInstanceValid(_commandsEditorConnection)
        && _commandsEditorConnection.IsEditorContextMenuUp;

    /// <summary>
    /// A context menu entry chosen in OpenCAGE whose action lives here (VIEWPORT_ACTION): each runs exactly
    /// what its shortcut runs. The entries OpenCAGE owns - Copy, Paste, Duplicate, Delete - never come here.
    /// </summary>
    public void RunViewportAction(OpenCAGE.UnityConnection.ViewportAction action)
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        switch (action)
        {
            case OpenCAGE.UnityConnection.ViewportAction.FocusOnSelection: //Z
                FocusSelectedEntity();
                break;
            case OpenCAGE.UnityConnection.ViewportAction.SnapToFloor: //Shift+End
                _commandsEditorConnection?.SnapSelectionToFloor();
                break;
            case OpenCAGE.UnityConnection.ViewportAction.Hide: //H
                HideSelectedEntity();
                break;
            case OpenCAGE.UnityConnection.ViewportAction.UnhideAll: //Shift+H
                UnhideAllEntities();
                break;
            case OpenCAGE.UnityConnection.ViewportAction.StepIntoComposite: //Ctrl+middle click, where the right click was
                TryPickDrillIntoComposite(_contextMenuPosition);
                break;
            case OpenCAGE.UnityConnection.ViewportAction.SelectParentComposite:
                _commandsEditorConnection?.TrySelectParentComposite();
                break;
            case OpenCAGE.UnityConnection.ViewportAction.DeselectAll: //Escape, outside creation mode
                TryClearEntitySelection();
                break;
        }
    }

    private bool CanFocusSelectedEntity()
    {
        return _alienScene != null
            && _alienScene.TryGetSelectedEntity(out Node3D selected)
            && _alienScene.TryResolveFocusTarget(selected, maxExtent: 0f, out _, out _);
    }

    /// <summary>H: hide the selected entity until Shift+H (or the composite changes), and let go of it.</summary>
    private void HideSelectedEntity()
    {
        if (_alienScene != null && _alienScene.TryHideSelectedEntity())
            _commandsEditorConnection?.TryClearEntitySelection();
    }

    /// <summary>Shift+H: bring back everything H hid.</summary>
    private void UnhideAllEntities()
    {
        _alienScene?.ClearCompositeScopedHides();
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

    private void TryDeleteEntitySelection()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        _commandsEditorConnection?.SendEntityDeleteRequest();
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
                if (_boxSelect.IsArmed)
                {
                    FinishBoxSelectOrClick(mouseButton.Position);
                    GetViewport().SetInputAsHandled();
                }
                else if (TryGizmoMouseUp(mouseButton.Position))
                    GetViewport().SetInputAsHandled();
                break;
            case MouseButton.Right:
            {
                bool openMenu = _rightClickPending;
                _rightClickPending = false;
                _rightPressed = false;
                if (!EmbeddedInOpenCage)
                {
                    _mouseLookActive = false;
                    if (!_panning)
                        ReleaseMouse(this);
                }
                GetViewport().SetInputAsHandled();

                //Where it went down: the release is within a few pixels of it, and the press is what was aimed
                if (openMenu)
                    RequestContextMenu(_rightPressPosition);
                break;
            }
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
        ReleaseFollowOnUserCameraMove();
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
        if (pan.LengthSquared() <= 0.0001f)
            return;

        ReleaseFollowOnUserCameraMove();
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
        {
            EndEmbeddedMouseLook();
            _rightDownScreen = null;
            _rightDragStarted = false;
        }

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

            //Held still, the right button is a click for the context menu: no look, and no cursor thrown to the middle, yet
            if (!HasRightDragStarted())
                return;

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

    /// <summary>
    /// Embedded: whether the right button held now has moved far enough from where it went down (or flown the
    /// camera) to be a look. Measured on the polled cursor rather than on events, so a press this window's
    /// input never saw - the one that closed the context menu - waits for a drag too.
    /// </summary>
    private bool HasRightDragStarted()
    {
        if (_rightDragStarted)
            return true;

        if (!Win32Input.TryGetScreenCursorPosition(out Vector2 cursor))
            return true;

        if (_rightDownScreen == null)
        {
            _rightDownScreen = cursor;
            return false;
        }

        if (cursor.DistanceTo(_rightDownScreen.Value) < LevelViewerBoxSelect.DragThresholdPixels)
            return false;

        StartRightDrag();
        return true;
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
        public const int VK_V = 0x56;

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

        // Choosing a gizmo mode exits entity creation mode.
        ExitCreateModeIfActive();

        gizmo.SetMode(mode);

        _commandsEditorConnection?.SyncTransformGizmoToSelection(this);
        _commandsEditorConnection?.SendViewportModeToEditor();
    }

    private bool TryGizmoMouseDown(Vector2 pos, bool duplicate)
    {
        LevelViewerTransformGizmo gizmo = GetGizmo();
        if (gizmo == null || !gizmo.Visible)
            return false;
        gizmo.VertexSnapActive = LevelViewerTransformSnap.VertexAlways || IsVertexSnapKeyDown();
        return gizmo.HandleMouseButtonDown(pos, duplicate);
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
        //A right press moved far enough is a look, not a click for the context menu
        if (_rightPressed && !_rightDragStarted && (motion.ButtonMask & MouseButtonMask.Right) != 0
            && motion.Position.DistanceTo(_rightPressPosition) >= LevelViewerBoxSelect.DragThresholdPixels)
        {
            StartRightDrag();
        }

        //A left press waiting to become a box, or being one, has the motion; the gizmo's hover stays as it was
        bool boxConsumed = TryUpdateBoxSelect(motion);

        // Always forward motion to the gizmo for hover highlighting (even when camera is not looking)
        LevelViewerTransformGizmo gizmo = GetGizmo();
        bool gizmoConsumed = false;
        if (!boxConsumed && gizmo != null && gizmo.Visible)
        {
            gizmo.VertexSnapActive = LevelViewerTransformSnap.VertexAlways || IsVertexSnapKeyDown();
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
        else if (boxConsumed)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Mark the selection a different way from the viewport. OpenCAGE owns the setting, so the change
    /// goes back to it the way the gizmo and selection modes do - it stores it and moves its own tick.
    /// </summary>
    private void SetHighlightMode(OpenCAGE.UnityConnection.LevelViewerHighlightMode mode)
    {
        if (PreviewVisibilitySettings.SelectionHighlightMode == mode)
            return;

        //Already on the main thread here (input), so the re-marking SetMode does needs no deferral
        LevelViewerSelection.SetMode(mode);
        _commandsEditorConnection?.SendViewportModeToEditor();
    }

    private void SetDeepSelectMode(PreviewVisibilitySettings.DeepSelectModeKind mode)
    {
        // Choosing a selection mode exits entity creation mode.
        bool changed = ExitCreateModeIfActive();

        if (PreviewVisibilitySettings.DeepSelectMode != mode)
        {
            _commandsEditorConnection?.ResetProgressiveDeepSelectPickState();
            PreviewVisibilitySettings.DeepSelectMode = mode;
            changed = true;
        }

        if (changed)
            _commandsEditorConnection?.SendViewportModeToEditor();
    }

    /// <summary>Leave entity creation mode. False if it wasn't active, so callers can fall through.</summary>
    private bool ExitCreateModeIfActive()
    {
        if (_commandsEditorConnection == null || !GodotObject.IsInstanceValid(_commandsEditorConnection))
            _commandsEditorConnection = GetNodeOrNull<CommandsEditorConnection>(CommandsEditorConnectionPath);

        if (_commandsEditorConnection == null || !_commandsEditorConnection.CreateModeActive)
            return false;

        _commandsEditorConnection.ExitCreateMode();
        return true;
    }
}
