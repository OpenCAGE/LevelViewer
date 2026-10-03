using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// In-viewport transform gizmo attached to the currently selected entity.
/// Modes: local/world translate arrows, local/world rotation rings, or hidden.
/// <see cref="OnTransformChanged"/> fires on every drag update so callers can push
/// position / rotation back to OpenCAGE.
/// </summary>
public partial class LevelViewerTransformGizmo : Node3D
{
    public enum GizmoMode { None, TranslateWorld, RotateLocal, RotateWorld, TranslateLocal }

    /// <summary>
    /// Fired for each target a drag has moved when it ends - not for one it left where it was. Args:
    /// its index in the selection (0 = the anchor the gizmo takes its orientation from), local
    /// position, local euler rotation degrees, and the gesture the drag belongs to
    /// (<see cref="NextGesture"/>) - the same for every target it moved.
    /// </summary>
    public Action<int, Vector3, Vector3, uint> OnTransformChanged;

    /// <summary>Fired once when a drag ends — use for pick-cache invalidation (not every mouse-move frame).</summary>
    public Action<Node3D> OnDragCommitted;

    /// <summary>
    /// Shift was held when a handle was pressed, and the press is surely a drag (a shift-click makes no
    /// copies): the caller should duplicate the selection and let the copies replace the drag targets
    /// (3ds Max shift-clone). Fired instead of beginning a real drag. Carries the gesture the drag that
    /// picks the copies up will commit under, so the copies and their move are one gesture.
    /// </summary>
    public Action<uint> OnDuplicateRequested;

    /// <summary>
    /// A shift-clone was called off after its copies were asked for (Escape, or the selection changing
    /// under its drag), whether or not they have arrived: the caller should take back the step that
    /// made them - only if it is still the latest step - rather than leave them stacked unseen on the
    /// originals. Carries the clone's gesture.
    /// </summary>
    public Action<uint> OnCloneCancelled;

    /// <summary>
    /// Nearest mesh vertex to the cursor for vertex snapping: (mousePos, current targets) -> world
    /// point, or null when the ray misses. Set by the connection, which owns the scene and pick data.
    /// </summary>
    public Func<Vector2, IReadOnlyList<Node3D>, Vector3?> VertexSnapProvider;

    /// <summary>Vertex snapping is on for this drag (V held, or the persistent toolbar option).</summary>
    public bool VertexSnapActive { get; set; }

    /// <summary>
    /// A handle is still held on a shift-clone whose copies have been asked for but not arrived: the
    /// drag moves them once they do. (Let go first, the copies still get the drag as it stood - that
    /// wait is not a held handle.)
    /// </summary>
    public bool IsHandoverArmed => _handoverArmed && !_handoverReleased;

    /// <summary>
    /// The latest <see cref="SetTargets"/> handed a shift-clone's drag to its copies: that selection
    /// change was the clone's own, not one to fly the camera to.
    /// </summary>
    public bool TookHandover { get; private set; }

    // ── visual constants ──────────────────────────────────────────────────────
    private const float GizmoScreenSize  = 0.10f;  // desired fraction of viewport height
    private const float ArrowLength      = 1.0f;
    private const float ArrowRadius      = 0.040f;
    private const float ArrowHeadLen     = 0.28f;
    private const float ArrowHeadRadius  = 0.10f;
    private const float PlaneOffset      = 0.30f;  // local-space offset of plane squares
    private const float PlaneSize        = 0.18f;
    private const float RingRadius       = 0.90f;
    private const float RingTube         = 0.032f;
    private const int   RingSegs         = 64;
    private const int   RingHitSamples   = 48;

    // Screen-space pick tolerances (pixels) — depth-independent, beats scene geometry at same pixel.
    private const float AxisScreenPickPx   = 42f;
    private const float PlaneScreenPadPx   = 30f;
    private const float RingScreenPickPx   = 44f;

    // ── colours ───────────────────────────────────────────────────────────────
    private static readonly Color ColX     = new Color(0.95f, 0.18f, 0.18f);
    private static readonly Color ColY     = new Color(0.18f, 0.85f, 0.18f);
    private static readonly Color ColZ     = new Color(0.25f, 0.55f, 0.95f);
    private static readonly Color ColHov   = new Color(1f, 0.85f, 0.15f);
    private static readonly Color ColPlane = new Color(1f, 1f, 0.3f, 0.42f);
    private static readonly Color ColPlaneH= new Color(1f, 1f, 0.3f, 0.72f);

    // ── drag state ────────────────────────────────────────────────────────────
    private enum DragAxis { None, X, Y, Z, XY, XZ, YZ, RotX, RotY, RotZ }

    private GizmoMode  _mode      = GizmoMode.None;
    /* The anchor: it decides the gizmo's orientation in local modes, and a one-entity selection is
       just this. _targets holds the whole selection with the anchor first. */
    private Node3D     _target;
    private readonly List<Node3D> _targets = new List<Node3D>();
    private Camera3D   _camera;
    private DragAxis   _dragAxis  = DragAxis.None;
    private DragAxis   _hovAxis   = DragAxis.None;
    private float      _worldScale= 1f;

    // drag bookkeeping — pivot stays fixed for the whole drag
    private Vector3 _dragPivot;        // world position at drag start (never moves during drag)
    private Vector3 _dragStartPos;
    private Vector3 _dragStartRot;
    private readonly List<Vector3> _dragStartPositions = new List<Vector3>();     // per target, world
    private readonly List<Quaternion> _dragStartQuaternions = new List<Quaternion>(); // per target, world
    private readonly List<Transform3D> _dragStartTransforms = new List<Transform3D>(); // per target, local (what the level holds)
    private Vector3 _dragStartHit;       // ray-plane hit at mouse-down
    private Vector3 _dragAxisDir;      // constrained axis (translate) or rotation axis (rotate)
    private Vector3 _dragPlaneNormal;  // plane used for ray intersection during drag
    private bool    _isDragging;
    private bool    _dragFromPress;    // the live drag grew out of a press on the originals: a quick one is still a click
    private bool    _dragIsClone;      // the live drag is a shift-clone's, on its copies: calling it off takes them back

    /* A handle pressed but not yet dragged. Nothing moves, snaps, duplicates or commits until the mouse has
       gone LevelViewerBoxSelect.DragThresholdPixels from the press - a click on a handle is not a drag
       (issue 718: a press that began the drag at once committed it on the release, snapping an off-grid
       entity onto the grid and leaving an undo step behind for a click). */
    private bool    _pressArmed;
    private Vector2 _pressPos;
    private ulong   _pressMs;          // when it went down
    private float   _pressMaxTravel;   // the furthest the held cursor has been from _pressPos
    private Vector2 _lastHeldPos;      // where the cursor last was with the button known to be down
    private bool    _pressDuplicate;   // shift was held: the drag, once it is one, is a shift-clone

    /* Let go this soon, having been no further than this from the press, a press is a click however it
       moved in between. Clicking while the mouse is still travelling (issue 718's second report) covers
       more than the drag threshold in the ~100 ms the button is down; a drag this short and this quick is
       not one anybody means to make - it is over before it can be seen. A plain drag still shows from the
       threshold on, and is put back if it ends as a click; a shift-clone asks for no copies until it is
       past being one. */
    private const ulong ClickMaxMs       = 250;
    private const float ClickMaxTravelPx = 24f;

    //Closer than this to where the drag found it (metres, or a basis column's length) is not a change
    private const float UnchangedEpsilon = 1e-4f;

    // rotation-only: incremental angle tracking (avoids acos wrap at ±180°)
    private Quaternion _dragStartGlobalQuat;
    private Vector3    _dragRotRefDir;
    private float      _dragAccumAngleRad;

    private bool    _vertexSnappedThisMove;

    /* Shift-clone handover: armed once a shift-press is surely a drag (the copies are asked for then),
       disarmed once the copies replace the targets. The drag is all kept here rather than in the live
       drag state, so a release - or a new press - before the copies arrive does not lose it. */
    private bool          _handoverArmed;
    private bool          _handoverReleased;   // let go before the copies came: they get the drag as it stood then
    private Vector2       _handoverPressPos;
    private Vector2       _handoverReleasePos;
    private ulong         _handoverArmedMs;
    private GizmoMode     _handoverMode;
    private DragAxis      _handoverAxis;
    private uint          _handoverGesture;
    private bool          _handoverVertexSnap;
    private readonly List<Node3D> _handoverOriginals = new List<Node3D>();
    private readonly List<Vector3> _handoverOriginalPositions = new List<Vector3>(); // world, where the copies will be made
    private const ulong HandoverTimeoutMs = 2000;
    private const float CopyMatchEpsilon  = 0.01f;

    /* The gesture the drag being made (or armed, for a shift-clone) belongs to. Taken at the press, so a
       shift-clone's duplicate request and the drag that carries its copies share it. */
    private uint _gesture;

    /* Seeded at random, so a viewer restarted mid-session does not hand out an id that is still on top
       of the editor's history from before. */
    private static uint _lastGesture = (uint)Random.Shared.Next(1, int.MaxValue);

    /// <summary>
    /// An id for one gesture. Everything a single drag sends goes out under it - a packet per entity it
    /// moved - which is what lets the editor undo the whole of it as one step. Never 0.
    /// </summary>
    public static uint NextGesture()
    {
        _lastGesture = unchecked(_lastGesture + 1);
        if (_lastGesture == 0)
            _lastGesture = 1;
        return _lastGesture;
    }

    // mesh children
    private StandardMaterial3D[] _axisMats;  // 0=X 1=Y 2=Z  (shared by shaft+head)
    private StandardMaterial3D[] _planeMats; // 0=XY 1=XZ 2=YZ
    private StandardMaterial3D[] _ringMats;  // 0=RotX 1=RotY 2=RotZ

    /// <summary>
    /// A handle is held: a drag under way, or a press on one that turns into a drag once the mouse
    /// moves far enough (until then it moves nothing).
    /// </summary>
    public bool IsDragging => _isDragging || _pressArmed;

    /// <summary>True when a visible gizmo handle would be hit at this screen position (ignores depth).</summary>
    public bool HitsAtScreen(Vector2 mousePos)
        => Visible && HitTest(mousePos) != DragAxis.None;

    // ─────────────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        TopLevel = true;   // position ourselves in world space, not relative to parent
        Visible  = false;
        SetProcess(false);
    }

    public void SetMode(GizmoMode mode)
    {
        if (_mode == mode)
            return;

        /* Mid gesture (a mode key pressed with the button still down): finish it where it was last held, as
           letting go there would, rather than leave what it moved shown somewhere the level doesn't have it.
           A shift-clone still waiting on its copies is let go the same way - they get the drag, in the mode
           it was made in, when they come - rather than handed a drag in a mode it was never made in. */
        if (_isDragging)
            FinishDrag(_lastHeldPos);
        if (IsHandoverArmed)
            ReleaseHandover(_lastHeldPos);

        _mode = mode;
        _pressArmed = false;
        _dragAxis = DragAxis.None;
        RebuildMeshes();
        RefreshVisibility();
    }

    public GizmoMode Mode => _mode;

    public void SetTarget(Node3D target, Camera3D camera)
    {
        SetTargets(target == null ? null : new List<Node3D> { target }, camera);
    }

    /// <summary>
    /// Drive several entities at once. The first is the anchor; the gizmo sits at the centre of them
    /// all and a drag moves the group rigidly, so what it does to one it does to every one.
    /// </summary>
    public void SetTargets(IReadOnlyList<Node3D> targets, Camera3D camera)
    {
        List<Node3D> incoming = new List<Node3D>();
        if (targets != null)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                Node3D target = targets[i];
                if (target != null && GodotObject.IsInstanceValid(target)
                    && PreviewVisualUtility.HasValidWorldAnchor(target))
                {
                    incoming.Add(target);
                }
            }
        }

        /* The same selection again (a render filter refresh, an echo): whatever the handle is doing carries on.
           A different one takes the targets away from under it. A drag is put back, not committed - by now the
           entity ids and path it would be sent under are the new selection's, so it wrote the old nodes'
           transforms into the new entities - and a press not yet a drag is let go: the handle it was held on
           belongs to what was selected before. */
        if (!SameNodes(incoming, _targets))
        {
            AbandonDrag();
            _pressArmed = false;
        }

        _camera  = camera;
        TookHandover = false;
        _targets.Clear();
        _targets.AddRange(incoming);

        _target = _targets.Count > 0 ? _targets[0] : null;
        if (_target == null)
        {
            Visible = false;
            return;
        }

        if (!_isDragging)
            GlobalPosition = GetTargetsCentre();
        RefreshVisibility();

        TryBeginHandover();
    }

    private static bool SameNodes(List<Node3D> a, List<Node3D> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    /// <summary>
    /// A shift-clone's copies may just have been selected (this is what SetTargets is handing us). If the
    /// left button is still down, pick the drag up on them from where it was pressed and jump to where
    /// the cursor is now, so the copies follow the cursor as one continuous gesture; let go already, give
    /// them the drag as it stood when it was.
    /// </summary>
    private void TryBeginHandover()
    {
        ExpireHandover();
        if (!_handoverArmed)
            return;

        //Not the copies (still the originals - the ENTITY_ADDED echo - or something unrelated): keep waiting
        if (!AreTheCopies(_targets))
            return;

        _handoverArmed = false;
        TookHandover = true;

        //Let go before they came - or the release never reached this window - so they go where it was let go
        if (_handoverReleased || !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            ApplyHandoverDrag(_handoverReleased ? _handoverReleasePos : _lastHeldPos);
            return;
        }

        _dragAxis      = _handoverAxis;
        _gesture       = _handoverGesture;
        _isDragging    = true;
        _dragIsClone   = true;
        _dragFromPress = false;
        ApplyHighlight();
        BeginDrag(_handoverPressPos);
        DragUpdate(_lastHeldPos);
    }

    /// <summary>
    /// The copies arrived after the button came up: make the drag the press and release describe on them
    /// in one go, in the mode it was made in, and commit it under the clone's gesture - the step that made
    /// the copies - so the copies land where they were dragged to rather than stacked on the originals.
    /// </summary>
    private void ApplyHandoverDrag(Vector2 releasePos)
    {
        if (_camera == null || !GodotObject.IsInstanceValid(_camera))
            return;

        GizmoMode mode = _mode;
        bool vertexSnap = VertexSnapActive;
        _mode            = _handoverMode;
        VertexSnapActive = _handoverVertexSnap;
        _dragAxis        = _handoverAxis;
        _gesture         = _handoverGesture;
        _isDragging      = true;
        try
        {
            BeginDrag(_handoverPressPos);
            DragUpdate(releasePos);
            CommitDrag();
        }
        finally
        {
            _mode = mode;
            VertexSnapActive = vertexSnap;
            EndDragState();
            ApplyHighlight();
        }
    }

    /// <summary>
    /// The shift-clone's copies: none of them an original, and each where an original stood when they were
    /// asked for (they are made in place). A selection of anything else made meanwhile is not handed the drag.
    /// </summary>
    private bool AreTheCopies(List<Node3D> targets)
    {
        if (targets.Count == 0 || SharesAnyNode(targets, _handoverOriginals))
            return false;

        for (int i = 0; i < targets.Count; i++)
        {
            if (!GodotObject.IsInstanceValid(targets[i]))
                return false;

            bool onAnOriginal = false;
            for (int j = 0; j < _handoverOriginalPositions.Count && !onAnOriginal; j++)
                onAnOriginal = targets[i].GlobalPosition.DistanceTo(_handoverOriginalPositions[j]) <= CopyMatchEpsilon;
            if (!onAnOriginal)
                return false;
        }
        return true;
    }

    //Timed out (the editor refused the duplicate, say, or is still making it): stop waiting, so a held
    //handle is never left swallowing motion; late copies stay where they were made.
    private void ExpireHandover()
    {
        if (_handoverArmed && Time.GetTicksMsec() - _handoverArmedMs > HandoverTimeoutMs)
            _handoverArmed = false;
    }

    /// <summary>The button came up on a shift-clone still waiting on its copies: they get the drag as it stands.</summary>
    private void ReleaseHandover(Vector2 mousePos)
    {
        _handoverReleased   = true;
        _handoverReleasePos = mousePos;
        _handoverVertexSnap = VertexSnapActive;
    }

    private static bool SharesAnyNode(List<Node3D> a, List<Node3D> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (a[i] == b[j])
                    return true;
        return false;
    }

    public void ClearTarget()
    {
        //The selection is gone from under the handle: put back what its drag moved, which the level never had.
        //A shift-clone still waiting on its copies keeps waiting - they arrive selected.
        AbandonDrag();

        _target = null;
        _targets.Clear();
        _pressArmed = false;
        TookHandover = false;
        Visible = false;
    }

    /// <summary>
    /// Call off what the held handle is doing (Escape). A drag puts every target back where the press
    /// found it and sends nothing; a press that isn't a drag yet is let go. A shift-clone's copies - asked
    /// for, whether or not they have arrived - are taken back too (<see cref="OnCloneCancelled"/>), rather
    /// than left stacked unseen on the originals.
    /// </summary>
    public void CancelDrag()
    {
        AbandonDrag();

        if (IsHandoverArmed)
        {
            _handoverArmed = false;
            OnCloneCancelled?.Invoke(_handoverGesture);
        }

        _pressArmed = false;
        _dragAxis   = DragAxis.None;
        ApplyHighlight();
    }

    /// <summary>
    /// Another button went down while a handle is held but not yet dragged: that press is let go, so the
    /// camera moving under it - a look puts the cursor back in the middle of the view - cannot make a
    /// drag of it.
    /// </summary>
    public void DropPendingPress()
    {
        if (!_pressArmed)
            return;

        _pressArmed = false;
        _dragAxis   = DragAxis.None;
        ApplyHighlight();
    }

    /// <summary>A live drag is called off: every target goes back where it found them, and a clone's copies are taken back.</summary>
    private void AbandonDrag()
    {
        if (!_isDragging)
            return;

        RevertDrag();
        if (_dragIsClone)
            OnCloneCancelled?.Invoke(_gesture);
        EndDragState();
    }

    /// <summary>Put every target back exactly where the drag found it. Nothing is sent: the level never had it moved.</summary>
    private void RevertDrag()
    {
        for (int i = 0; i < _targets.Count && i < _dragStartTransforms.Count; i++)
        {
            if (GodotObject.IsInstanceValid(_targets[i]))
                _targets[i].Transform = _dragStartTransforms[i];
        }
    }

    private void EndDragState()
    {
        _isDragging    = false;
        _dragFromPress = false;
        _dragIsClone   = false;
        _dragAxis      = DragAxis.None;
    }

    /// <summary>Where the handles sit: on the entity for one, in the middle of them for several.</summary>
    private Vector3 GetTargetsCentre()
    {
        Vector3 total = Vector3.Zero;
        int count = 0;
        for (int i = 0; i < _targets.Count; i++)
        {
            Node3D target = _targets[i];
            if (target == null || !GodotObject.IsInstanceValid(target))
                continue;

            total += target.GlobalPosition;
            count++;
        }

        return count == 0 ? GlobalPosition : total / count;
    }

    // ─────────────────────────────────────────────────────────────────────────
    public override void _Process(double delta)
    {
        try
        {
            ProcessInternal();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] Gizmo _Process failed: " + ex);
        }
    }

    private void ProcessInternal()
    {
        if (_mode == GizmoMode.None
            || _target == null || !GodotObject.IsInstanceValid(_target)
            || !PreviewVisualUtility.HasValidWorldAnchor(_target))
        {
            Visible = false;
            return;
        }

        // If camera wasn't available when SetTarget was called, look it up now.
        if (_camera == null || !GodotObject.IsInstanceValid(_camera))
        {
            _camera = GetTree()?.Root?.FindChild("Camera3D", true, false) as Camera3D;
            if (_camera == null)
            {
                Visible = false;
                return;
            }
        }

        /* A shift-press that moved past the drag threshold and is then held still stops being a click once
           the click time is up: ask for the copies now, rather than at the next motion or the release. */
        ExpireHandover();
        if (_pressArmed && _pressDuplicate && Input.IsMouseButtonPressed(MouseButton.Left) && PressIsDrag())
            BeginPressedDrag(_lastHeldPos);

        if (!_isDragging)
            GlobalPosition = GetTargetsCentre();

        GlobalBasis = GetOrientationBasis();

        UpdateWorldScale();
        Scale   = Vector3.One * _worldScale;
        Visible = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input forwarding (called by LevelViewerCamera)
    // ─────────────────────────────────────────────────────────────────────────

    public bool HandleMouseMotion(Vector2 mousePos) => HandleMouseMotion(mousePos, leftHeld: true);

    /// <param name="leftHeld">The left button is down, as far as this motion knows.</param>
    public bool HandleMouseMotion(Vector2 mousePos, bool leftHeld)
    {
        if (_mode == GizmoMode.None || !Visible)
            return false;

        //The button came up somewhere this window never heard about (let go over another window): whatever the
        //handle held ended there, as a release where it was last held - not following the cursor with no
        //button down until the next click commits it wherever it has got to.
        if (!leftHeld)
            EndHeldGesture(_lastHeldPos);
        else if (IsHandleHeld)
            TrackHeld(mousePos);

        ExpireHandover();

        //Waiting on a shift-clone's copies: hold the gesture, but move nothing until they arrive.
        if (IsHandoverArmed)
            return true;

        if (_pressArmed)
        {
            if (PressIsDrag())
                BeginPressedDrag(mousePos);
            return true;
        }

        if (_isDragging)
        {
            DragUpdate(mousePos);
            return true;
        }

        DragAxis newHov = HitTest(mousePos);
        if (newHov != _hovAxis)
        {
            _hovAxis = newHov;
            ApplyHighlight();
        }
        return false;
    }

    public bool HandleMouseButtonDown(Vector2 mousePos) => HandleMouseButtonDown(mousePos, duplicate: false);

    public bool HandleMouseButtonDown(Vector2 mousePos, bool duplicate)
    {
        //Still holding from a press whose release never arrived: that ended where it was last held, and
        //must not carry on under this press
        EndHeldGesture(_lastHeldPos);

        if (_mode == GizmoMode.None || !Visible || _target == null)
            return false;

        DragAxis hit = HitTest(mousePos);
        if (hit == DragAxis.None)
            return false;

        _dragAxis    = hit;
        _hovAxis     = hit;
        _lastHeldPos = mousePos;
        _gesture     = NextGesture();
        ApplyHighlight();

        /* Only armed: the drag waits for the mouse to move (see _pressArmed). The press is the gizmo's
           either way, so a click on a handle still never selects what is behind it. */
        _pressArmed     = true;
        _pressPos       = mousePos;
        _pressMs        = Time.GetTicksMsec();
        _pressMaxTravel = 0f;
        _pressDuplicate = duplicate && IsTranslateMode;
        return true;
    }

    public bool HandleMouseButtonUp(Vector2 mousePos)
    {
        if (!IsHandleHeld)
            return false;

        EndHeldGesture(mousePos);
        _hovAxis = HitTest(mousePos);
        ApplyHighlight();
        return true;
    }

    //The button is down on a handle: a press not yet a drag, a drag, or a shift-clone waiting on its copies
    private bool IsHandleHeld => _pressArmed || _isDragging || IsHandoverArmed;

    private void TrackHeld(Vector2 mousePos)
    {
        _lastHeldPos = mousePos;
        _pressMaxTravel = Mathf.Max(_pressMaxTravel, mousePos.DistanceTo(_pressPos));
    }

    //Let go now, the press would still be a click: quick, and never far from where it went down
    private bool PressIsClickSoFar()
        => Time.GetTicksMsec() - _pressMs < ClickMaxMs && _pressMaxTravel < ClickMaxTravelPx;

    /// <summary>
    /// The held press has gone far enough to be a drag. A plain one shows it from the threshold on (and is
    /// put back if it ends as a click); a shift-clone asks for copies only once it cannot be a click.
    /// </summary>
    private bool PressIsDrag()
    {
        if (_pressMaxTravel < LevelViewerBoxSelect.DragThresholdPixels)
            return false;
        return !_pressDuplicate || !PressIsClickSoFar();
    }

    /// <summary>
    /// The button is up - let go at <paramref name="mousePos"/>, or found up and taken as let go there. A press
    /// never a drag was a click and changes nothing; a drag ends (<see cref="FinishDrag"/>); a shift-clone
    /// still waiting on its copies hands them the drag as it stands, when they come.
    /// </summary>
    private void EndHeldGesture(Vector2 mousePos)
    {
        if (_pressArmed)
        {
            TrackHeld(mousePos);
            //Surely a drag though never begun (a shift-clone moved, then held still until it was let go)
            if (PressIsDrag())
            {
                BeginPressedDrag(mousePos);
            }
            else
            {
                _pressArmed = false;
                _dragAxis   = DragAxis.None;
            }
        }

        if (_isDragging)
            FinishDrag(mousePos);

        if (IsHandoverArmed)
            ReleaseHandover(mousePos);
    }

    /// <summary>
    /// The drag ends, let go at <paramref name="mousePos"/>. One that grew out of a press and ends as a
    /// click - let go quickly, having hardly moved - is put back: nothing is sent and no undo step made. The
    /// rest are committed as they stand (snapped to the grid and steps).
    /// </summary>
    private void FinishDrag(Vector2 mousePos)
    {
        if (!_isDragging)
            return;

        if (_dragFromPress)
            TrackHeld(mousePos);

        if (_dragFromPress && PressIsClickSoFar())
            RevertDrag();
        else
            CommitDrag();

        EndDragState();
    }

    /// <summary>
    /// The held handle is a drag now. It begins from the press, so the pixels it took to get here aren't
    /// lost, and catches up to the cursor.
    /// </summary>
    private void BeginPressedDrag(Vector2 mousePos)
    {
        _pressArmed = false;

        //Shift-clone (translate only): don't touch the originals - arm the handover, ask the caller to
        //duplicate, and pick the drag up on the copies once they are selected in.
        if (_pressDuplicate)
        {
            _handoverArmed      = true;
            _handoverReleased   = false;
            _handoverPressPos   = _pressPos;
            _handoverArmedMs    = Time.GetTicksMsec();
            _handoverMode       = _mode;
            _handoverAxis       = _dragAxis;
            _handoverGesture    = _gesture;
            _handoverVertexSnap = VertexSnapActive;
            _handoverOriginals.Clear();
            _handoverOriginalPositions.Clear();
            for (int i = 0; i < _targets.Count; i++)
            {
                if (!GodotObject.IsInstanceValid(_targets[i]))
                    continue;
                _handoverOriginals.Add(_targets[i]);
                _handoverOriginalPositions.Add(_targets[i].GlobalPosition);
            }
            OnDuplicateRequested?.Invoke(_gesture);
            return;
        }

        _isDragging    = true;
        _dragFromPress = true;
        _dragIsClone   = false;
        BeginDrag(_pressPos);
        DragUpdate(mousePos);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Drag mechanics  (camera-facing plane for axis; constraint plane for squares)
    // ─────────────────────────────────────────────────────────────────────────
    private void BeginDrag(Vector2 mousePos)
    {
        //The whole group turns about where the handles are, which for one entity is the entity itself
        _dragPivot    = GetTargetsCentre();
        _dragStartPos = _target.GlobalPosition;
        _dragStartRot = _target.RotationDegrees;

        _dragStartPositions.Clear();
        _dragStartQuaternions.Clear();
        _dragStartTransforms.Clear();
        for (int i = 0; i < _targets.Count; i++)
        {
            _dragStartPositions.Add(_targets[i].GlobalPosition);
            _dragStartQuaternions.Add(GetGlobalQuaternion(_targets[i]));
            _dragStartTransforms.Add(_targets[i].Transform);
        }

        if (IsTranslateMode)
        {
            if (IsPlane(_dragAxis))
            {
                // Constrained to the picked plane (XY / XZ / YZ).
                _dragPlaneNormal = GetPlaneNormal(_dragAxis);
                _dragAxisDir     = Vector3.Zero;
            }
            else
            {
                // Single axis: intersect rays with the camera-facing plane, then
                // project movement onto the axis (standard editor gizmo behaviour).
                _dragAxisDir     = AxisToWorldDir(_dragAxis);
                _dragPlaneNormal = GetCameraForward();
            }
        }
        else if (IsRotateMode)
        {
            // Rotation happens in the plane perpendicular to the ring axis.
            _dragAxisDir     = AxisToWorldDir(_dragAxis);
            _dragPlaneNormal = _dragAxisDir;
            _dragStartGlobalQuat = GetGlobalQuaternion(_target);
            _dragAccumAngleRad   = 0f;

            _dragStartHit = IntersectDragPlane(mousePos) ?? _dragPivot;
            _dragRotRefDir = (_dragStartHit - _dragPivot);
            if (_dragRotRefDir.LengthSquared() < 1e-8f)
            {
                GetRingBasis(_dragAxisDir, out Vector3 basisU, out _);
                _dragRotRefDir = basisU;
            }
            else
            {
                _dragRotRefDir = _dragRotRefDir.Normalized();
            }
            return;
        }

        _dragStartHit = IntersectDragPlane(mousePos) ?? _dragPivot;
    }

    private void DragUpdate(Vector2 mousePos)
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target))
        {
            _isDragging = false;
            return;
        }

        Vector3? currentHit = IntersectDragPlane(mousePos);
        if (currentHit == null)
            return;

        if (IsTranslateMode)
        {
            Vector3 worldDelta = currentHit.Value - _dragStartHit;

            // Free movement within the picked plane, or camera-plane movement projected onto one axis.
            Vector3 delta = IsPlane(_dragAxis)
                ? worldDelta
                : _dragAxisDir * worldDelta.Dot(_dragAxisDir);

            //Vertex snap: put the anchor's pivot on the nearest vertex under the cursor, still held to
            //the handle's axis or plane so the gizmo behaves as it looks. The others keep their offset.
            _vertexSnappedThisMove = false;
            if (VertexSnapActive && VertexSnapProvider != null)
            {
                Vector3? vertex = VertexSnapProvider(mousePos, _targets);
                if (vertex.HasValue)
                {
                    Vector3 want = vertex.Value - _dragStartPositions[0];
                    delta = IsPlane(_dragAxis)
                        ? want - _dragPlaneNormal * want.Dot(_dragPlaneNormal)
                        : _dragAxisDir * want.Dot(_dragAxisDir);
                    _vertexSnappedThisMove = true;
                }
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                if (GodotObject.IsInstanceValid(_targets[i]))
                    _targets[i].GlobalPosition = _dragStartPositions[i] + delta;
            }

            GlobalPosition = _dragPivot + delta;
        }
        else if (IsRotateMode)
        {
            Vector3 to = currentHit.Value - _dragPivot;
            if (to.LengthSquared() < 1e-8f)
                return;

            to = to.Normalized();
            float deltaRad = _dragRotRefDir.SignedAngleTo(to, _dragAxisDir);
            _dragRotRefDir      = to;
            _dragAccumAngleRad += deltaRad;

            /* A group rotates by whole steps of the snap, rather than each entity being snapped to
               its own angle afterwards - that would pull the group apart. */
            float angleRad = _dragAccumAngleRad;
            float rotationStep = LevelViewerTransformSnap.RotationDegrees;
            if (_targets.Count > 1 && rotationStep > 0f)
            {
                angleRad = Mathf.DegToRad(
                    LevelViewerTransformSnap.SnapValue(Mathf.RadToDeg(angleRad), rotationStep));
            }

            if (_mode == GizmoMode.RotateWorld)
            {
                Vector3 axis = _dragAxisDir.Normalized();
                ApplyGroupRotation(new Quaternion(axis, angleRad));
            }
            else
            {
                /* The ring is the anchor's own axis as it stood at the press. Turning about it is, in
                   world terms, q0 * delta * q0^-1 - one world rotation, which the whole group makes. */
                Vector3 localAxis = AxisToLocalDir(_dragAxis).Normalized();
                Quaternion localDelta = new Quaternion(localAxis, angleRad);
                Quaternion anchorRotation = _dragStartGlobalQuat * localDelta;
                ApplyGroupRotation(anchorRotation * _dragStartGlobalQuat.Inverse());
                GlobalBasis = _target.GlobalBasis;
            }
        }

        ApplyDragSnap();

        // Sync to OpenCAGE / all entity instances only on CommitDrag — not every mouse-move frame.
    }

    /// <summary>
    /// Turn the group rigidly by one world rotation about the pivot - where the rings are. Every
    /// target, the anchor included, turns by it and swings round the pivot by it, so the group keeps
    /// its shape; the anchor turning on the spot while the rest orbited (issue 694) left the pivot
    /// nowhere in particular. One entity is its own pivot, so it turns where it stands.
    /// </summary>
    private void ApplyGroupRotation(Quaternion worldDelta)
    {
        for (int i = 0; i < _targets.Count; i++)
        {
            Node3D target = _targets[i];
            if (target == null || !GodotObject.IsInstanceValid(target))
                continue;

            SetGlobalQuaternion(target, worldDelta * _dragStartQuaternions[i]);
            //One entity is its own pivot: its position is left alone, as it always was (no round trip through its parent)
            if (_targets.Count > 1)
                target.GlobalPosition = _dragPivot + worldDelta * (_dragStartPositions[i] - _dragPivot);
        }
    }

    private void ApplyDragSnap()
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target))
            return;

        //A vertex-snapped move is already exactly where it should be; the grid would pull it back off.
        float grid = _vertexSnappedThisMove ? 0f : LevelViewerTransformSnap.GridSize;
        if (grid > 0f && IsTranslateMode)
        {
            Vector3 before = _target.GlobalPosition;
            Vector3 pos = _target.Position;
            _target.Position = new Vector3(
                LevelViewerTransformSnap.SnapValue(pos.X, grid),
                LevelViewerTransformSnap.SnapValue(pos.Y, grid),
                LevelViewerTransformSnap.SnapValue(pos.Z, grid));

            /* Snapping each entity to the grid on its own would pull a group apart, so the anchor is
               the one that lands on the grid and the rest move with it. */
            Vector3 correction = _target.GlobalPosition - before;
            if (correction.LengthSquared() > 0f)
            {
                for (int i = 1; i < _targets.Count; i++)
                {
                    if (GodotObject.IsInstanceValid(_targets[i]))
                        _targets[i].GlobalPosition += correction;
                }
            }

            GlobalPosition = GetTargetsCentre();
        }

        //A group's rotation is snapped as one angle while it turns, not per entity afterwards
        float rotationStep = _targets.Count > 1 ? 0f : LevelViewerTransformSnap.RotationDegrees;
        if (rotationStep > 0f && IsRotateMode)
        {
            Vector3 rot = _target.RotationDegrees;
            switch (_dragAxis)
            {
                case DragAxis.RotX:
                    rot.X = LevelViewerTransformSnap.SnapValue(rot.X, rotationStep);
                    break;
                case DragAxis.RotY:
                    rot.Y = LevelViewerTransformSnap.SnapValue(rot.Y, rotationStep);
                    break;
                case DragAxis.RotZ:
                    rot.Z = LevelViewerTransformSnap.SnapValue(rot.Z, rotationStep);
                    break;
            }

            _target.RotationDegrees = rot;
            if (_mode == GizmoMode.RotateLocal)
                GlobalBasis = _target.GlobalBasis;
        }
    }

    /// <summary>Intersect the mouse ray with the active drag plane through <see cref="_dragPivot"/>.</summary>
    private Vector3? IntersectDragPlane(Vector2 mousePos)
    {
        GizmoRay ray = MakeRay(mousePos);
        Vector3? hit = RayPlaneIntersectInfinite(ray, _dragPivot, _dragPlaneNormal);
        if (hit != null)
            return hit;

        // Fallback when the primary plane is edge-on to the ray (e.g. axis pointing at camera).
        if (IsTranslateMode && !IsPlane(_dragAxis) && _dragAxisDir.LengthSquared() > 1e-8f)
        {
            Vector3 altNormal = _dragAxisDir.Cross(GetCameraForward());
            if (altNormal.LengthSquared() > 1e-8f)
                return RayPlaneIntersectInfinite(ray, _dragPivot, altNormal.Normalized());
        }

        return null;
    }

    private Vector3 GetCameraForward()
    {
        // Camera looks down local -Z; world forward is -Basis.Z.
        return -_camera.GlobalTransform.Basis.Z.Normalized();
    }

    private static Quaternion GetGlobalQuaternion(Node3D node)
        => node.GlobalBasis.GetRotationQuaternion();

    private static void SetGlobalQuaternion(Node3D node, Quaternion quat)
    {
        if (!IsFiniteQuaternion(quat))
            return;

        Transform3D global = node.GlobalTransform;
        global.Basis = new Basis(quat);
        node.GlobalTransform = global;
    }

    private static bool IsFiniteQuaternion(Quaternion quat)
    {
        return float.IsFinite(quat.X) && float.IsFinite(quat.Y)
            && float.IsFinite(quat.Z) && float.IsFinite(quat.W);
    }

    private void CommitDrag()
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target))
            return;

        ApplyDragSnap();

        for (int i = 0; i < _targets.Count; i++)
        {
            Node3D target = _targets[i];
            if (target == null || !GodotObject.IsInstanceValid(target))
                continue;

            /* Back where the drag found it (dragged there and back, or snapped onto where it started): there
               is nothing to record, so nothing is sent and no undo step made. It is put exactly back, so what
               the view shows is still what the level holds. */
            if (i < _dragStartTransforms.Count && IsSameTransform(target.Transform, _dragStartTransforms[i]))
            {
                target.Transform = _dragStartTransforms[i];
                continue;
            }

            OnTransformChanged?.Invoke(i, target.Position, target.RotationDegrees, _gesture);
            OnDragCommitted?.Invoke(target);
        }
    }

    private static bool IsSameTransform(Transform3D a, Transform3D b)
    {
        const float epsilonSq = UnchangedEpsilon * UnchangedEpsilon;
        return a.Origin.DistanceSquaredTo(b.Origin) <= epsilonSq
            && (a.Basis.X - b.Basis.X).LengthSquared() <= epsilonSq
            && (a.Basis.Y - b.Basis.Y).LengthSquared() <= epsilonSq
            && (a.Basis.Z - b.Basis.Z).LengthSquared() <= epsilonSq;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Hit testing (screen-space — ignores depth so handles win over scene geometry)
    // ─────────────────────────────────────────────────────────────────────────
    private DragAxis HitTest(Vector2 mousePos)
    {
        if (_camera == null || !Visible)
            return DragAxis.None;

        /* Behind the camera nothing of the gizmo is drawn, but a point behind it still projects - mirrored
           through the middle of the view. Turned away from a selection, its handles sat unseen in the middle of
           the screen and took the press, and the click there selected nothing. */
        if (IsBehindCamera(GlobalPosition))
            return DragAxis.None;

        DragAxis bestAxis = DragAxis.None;
        float bestDist    = float.MaxValue;

        if (IsTranslateMode)
        {
            float half = PlaneSize * 0.5f * _worldScale;
            float po   = PlaneOffset * _worldScale;
            float alen = ArrowLength * _worldScale;

            TestPlaneScreen(mousePos, Vector3.Right, Vector3.Up,   po, half, DragAxis.XY, ref bestAxis, ref bestDist);
            TestPlaneScreen(mousePos, Vector3.Right, Vector3.Back, po, half, DragAxis.XZ, ref bestAxis, ref bestDist);
            TestPlaneScreen(mousePos, Vector3.Up,    Vector3.Back, po, half, DragAxis.YZ, ref bestAxis, ref bestDist);

            TestAxisScreen(mousePos, Vector3.Right, alen, DragAxis.X, ref bestAxis, ref bestDist);
            TestAxisScreen(mousePos, Vector3.Up,    alen, DragAxis.Y, ref bestAxis, ref bestDist);
            TestAxisScreen(mousePos, Vector3.Back,  alen, DragAxis.Z, ref bestAxis, ref bestDist);
        }
        else if (IsRotateMode)
        {
            float rr = RingRadius * _worldScale;
            TestRingScreen(mousePos, Vector3.Right, rr, DragAxis.RotX, ref bestAxis, ref bestDist);
            TestRingScreen(mousePos, Vector3.Up,    rr, DragAxis.RotY, ref bestAxis, ref bestDist);
            TestRingScreen(mousePos, Vector3.Back,  rr, DragAxis.RotZ, ref bestAxis, ref bestDist);
        }

        return bestAxis;
    }

    private void TestAxisScreen(Vector2 mouse, Vector3 localAxisDir, float axisLength, DragAxis axis,
        ref DragAxis bestAxis, ref float bestDist)
    {
        Vector3 worldAxis = GetOrientationBasis() * localAxisDir;
        Vector3 worldEnd  = GlobalPosition + worldAxis * axisLength;
        //Only what is in front of the camera can be hit (see HitTest); the centre already is
        if (IsBehindCamera(worldEnd))
            return;

        Vector2 centre = WorldToScreen(GlobalPosition);
        Vector2 end    = WorldToScreen(worldEnd);
        float dist     = ScreenDistToSegment(mouse, centre, end);
        if (dist < AxisScreenPickPx && dist < bestDist)
        {
            bestDist = dist;
            bestAxis = axis;
        }
    }

    private void TestPlaneScreen(Vector2 mouse, Vector3 localU, Vector3 localV, float offset, float halfSize,
        DragAxis axis, ref DragAxis bestAxis, ref float bestDist)
    {
        Basis basis = GetOrientationBasis();
        Vector3 u = basis * localU;
        Vector3 v = basis * localV;
        Vector3 centre = GlobalPosition + (u + v) * offset;
        // Quad corners in world space (matches visual square).
        Vector3 c00 = centre + (-u - v) * halfSize;
        Vector3 c10 = centre + ( u - v) * halfSize;
        Vector3 c11 = centre + ( u + v) * halfSize;
        Vector3 c01 = centre + (-u + v) * halfSize;
        if (IsBehindCamera(c00) || IsBehindCamera(c10) || IsBehindCamera(c11) || IsBehindCamera(c01))
            return;

        Vector2 s00 = WorldToScreen(c00);
        Vector2 s10 = WorldToScreen(c10);
        Vector2 s11 = WorldToScreen(c11);
        Vector2 s01 = WorldToScreen(c01);

        float minX = Mathf.Min(Mathf.Min(s00.X, s10.X), Mathf.Min(s11.X, s01.X)) - PlaneScreenPadPx;
        float maxX = Mathf.Max(Mathf.Max(s00.X, s10.X), Mathf.Max(s11.X, s01.X)) + PlaneScreenPadPx;
        float minY = Mathf.Min(Mathf.Min(s00.Y, s10.Y), Mathf.Min(s11.Y, s01.Y)) - PlaneScreenPadPx;
        float maxY = Mathf.Max(Mathf.Max(s00.Y, s10.Y), Mathf.Max(s11.Y, s01.Y)) + PlaneScreenPadPx;

        if (mouse.X < minX || mouse.X > maxX || mouse.Y < minY || mouse.Y > maxY)
            return;

        // Prefer the closest plane when several overlap — use distance to centre.
        Vector2 sCentre = WorldToScreen(centre);
        float dist = mouse.DistanceTo(sCentre);
        if (dist < bestDist)
        {
            bestDist = dist;
            bestAxis = axis;
        }
    }

    private void TestRingScreen(Vector2 mouse, Vector3 localRingNormal, float radius, DragAxis axis,
        ref DragAxis bestAxis, ref float bestDist)
    {
        Vector3 ringNormal = (GetOrientationBasis() * localRingNormal).Normalized();
        GetRingBasis(ringNormal, out Vector3 basisU, out Vector3 basisV);
        Vector2 prevScreen = WorldToScreen(GlobalPosition);
        bool prevBehind = false;
        float minSegDist = float.MaxValue;

        for (int i = 0; i <= RingHitSamples; i++)
        {
            float angle = i * Mathf.Tau / RingHitSamples;
            Vector3 worldPoint = GlobalPosition
                + (basisU * Mathf.Cos(angle) + basisV * Mathf.Sin(angle)) * radius;
            Vector2 screenPoint = WorldToScreen(worldPoint);
            bool behind = IsBehindCamera(worldPoint);

            //A stretch of the ring that runs behind the camera is not drawn, so not hit either
            if (i > 0 && !behind && !prevBehind)
                minSegDist = Mathf.Min(minSegDist, ScreenDistToSegment(mouse, prevScreen, screenPoint));

            prevScreen = screenPoint;
            prevBehind = behind;
        }

        if (minSegDist < RingScreenPickPx && minSegDist < bestDist)
        {
            bestDist = minSegDist;
            bestAxis = axis;
        }
    }

    private Vector2 WorldToScreen(Vector3 world)
        => _camera.UnprojectPosition(world);

    private bool IsBehindCamera(Vector3 world)
        => _camera.IsPositionBehind(world);

    private static float ScreenDistToSegment(Vector2 point, Vector2 segA, Vector2 segB)
    {
        Vector2 ab = segB - segA;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f)
            return point.DistanceTo(segA);

        float t = Mathf.Clamp((point - segA).Dot(ab) / lenSq, 0f, 1f);
        return point.DistanceTo(segA + ab * t);
    }

    private static void GetRingBasis(Vector3 axis, out Vector3 u, out Vector3 v)
    {
        axis = axis.Normalized();
        Vector3 fallback = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
        u = axis.Cross(fallback).Normalized();
        v = axis.Cross(u).Normalized();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Mesh building
    // ─────────────────────────────────────────────────────────────────────────
    private void RebuildMeshes()
    {
        foreach (Node child in GetChildren())
            child.Free();
        _axisMats  = null;
        _planeMats = null;
        _ringMats  = null;

        if (IsTranslateMode)
            BuildTranslateMeshes();
        else if (IsRotateMode)
            BuildRotateMeshes();
    }

    private void BuildTranslateMeshes()
    {
        Color[] axisColors = { ColX, ColY, ColZ };
        // X = +X, Y = +Y, Z = -Z (Godot +Z is toward viewer, -Z is "forward" into scene)
        Vector3[] axisDirs = { Vector3.Right, Vector3.Up, Vector3.Back };

        _axisMats  = new StandardMaterial3D[3];
        _planeMats = new StandardMaterial3D[3];

        for (int i = 0; i < 3; i++)
        {
            _axisMats[i] = UnlitMat(axisColors[i]);

            float shaftLen = ArrowLength - ArrowHeadLen;

            // Shaft
            var shaft = new CylinderMesh { TopRadius = ArrowRadius, BottomRadius = ArrowRadius, Height = shaftLen };
            var shaftNode = new MeshInstance3D { Mesh = shaft, MaterialOverride = _axisMats[i] };
            OrientAlongAxis(shaftNode, axisDirs[i], axisDirs[i] * (shaftLen * 0.5f));
            AddChild(shaftNode);

            // Head (cone)
            var head = new CylinderMesh { TopRadius = 0f, BottomRadius = ArrowHeadRadius, Height = ArrowHeadLen };
            var headNode = new MeshInstance3D { Mesh = head, MaterialOverride = _axisMats[i] };
            OrientAlongAxis(headNode, axisDirs[i], axisDirs[i] * (ArrowLength - ArrowHeadLen * 0.5f));
            AddChild(headNode);
        }

        // Plane squares
        (Vector3 u, Vector3 v)[] planes = {
            (Vector3.Right, Vector3.Up),
            (Vector3.Right, Vector3.Back),
            (Vector3.Up,    Vector3.Back),
        };
        for (int i = 0; i < 3; i++)
        {
            _planeMats[i] = UnlitMat(ColPlane);
            _planeMats[i].CullMode     = BaseMaterial3D.CullModeEnum.Disabled;
            _planeMats[i].Transparency = BaseMaterial3D.TransparencyEnum.Alpha;

            var quad = new QuadMesh { Size = Vector2.One * PlaneSize };
            var qnode = new MeshInstance3D { Mesh = quad, MaterialOverride = _planeMats[i] };

            Vector3 pos    = (planes[i].u + planes[i].v) * PlaneOffset;
            Vector3 normal = planes[i].u.Cross(planes[i].v).Normalized();
            // Orient so the quad faces along the plane normal
            qnode.Transform = new Transform3D(new Basis(planes[i].u, planes[i].v, normal), pos);
            AddChild(qnode);
        }
    }

    private void BuildRotateMeshes()
    {
        Color[] cols    = { ColX, ColY, ColZ };
        Vector3[] norms = { Vector3.Right, Vector3.Up, Vector3.Back };
        _ringMats = new StandardMaterial3D[3];

        for (int i = 0; i < 3; i++)
        {
            _ringMats[i] = UnlitMat(cols[i]);
            ArrayMesh mesh = BuildTorusMesh(norms[i], RingRadius, RingTube, RingSegs, cols[i]);
            var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = _ringMats[i] };
            AddChild(node);
        }
    }

    private static ArrayMesh BuildTorusMesh(Vector3 ringNormal, float radius, float tubeRadius, int ringSegs, Color color)
    {
        // Pick two vectors perpendicular to ringNormal for the ring centre-line
        Vector3 n   = ringNormal.Normalized();
        Vector3 tmp = Mathf.Abs(n.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        Vector3 t1  = n.Cross(tmp).Normalized();
        Vector3 t2  = n.Cross(t1).Normalized();

        int tubeSegs = 8;
        var verts    = new List<Vector3>();
        var cols     = new List<Color>();
        var idxs     = new List<int>();

        for (int r = 0; r <= ringSegs; r++)
        {
            float ra = r * Mathf.Tau / ringSegs;
            Vector3 ringCentre = (t1 * Mathf.Cos(ra) + t2 * Mathf.Sin(ra)) * radius;
            // Build tube cross-section: radial outward and ringNormal
            Vector3 radial = ringCentre.Normalized();

            for (int t = 0; t < tubeSegs; t++)
            {
                float ta = t * Mathf.Tau / tubeSegs;
                verts.Add(ringCentre + (radial * Mathf.Cos(ta) + n * Mathf.Sin(ta)) * tubeRadius);
                cols.Add(color);
            }
        }

        for (int r = 0; r < ringSegs; r++)
        {
            for (int t = 0; t < tubeSegs; t++)
            {
                int a = r       * tubeSegs + t;
                int b = r       * tubeSegs + (t + 1) % tubeSegs;
                int c = (r + 1) * tubeSegs + t;
                int d = (r + 1) * tubeSegs + (t + 1) % tubeSegs;
                idxs.Add(a); idxs.Add(c); idxs.Add(b);
                idxs.Add(b); idxs.Add(c); idxs.Add(d);
            }
        }

        using var arrays = new Godot.Collections.Array(); //alive across the native call - see CollisionMeshOverlay.BuildMesh
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color]  = cols.ToArray();
        arrays[(int)Mesh.ArrayType.Index]  = idxs.ToArray();

        var mesh = new ArrayMesh();
        LevelViewerMeshUtil.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Highlight
    // ─────────────────────────────────────────────────────────────────────────
    private void ApplyHighlight()
    {
        Color[] base3 = { ColX, ColY, ColZ };
        DragAxis[] ta = { DragAxis.X,    DragAxis.Y,    DragAxis.Z    };
        DragAxis[] ra = { DragAxis.RotX, DragAxis.RotY, DragAxis.RotZ };

        for (int i = 0; i < 3; i++)
        {
            bool hov = _hovAxis == ta[i] || _hovAxis == ra[i];
            Color c  = hov ? ColHov : base3[i];
            if (_axisMats  != null) _axisMats[i].AlbedoColor  = c;
            if (_ringMats  != null) _ringMats[i].AlbedoColor  = c;
        }

        if (_planeMats != null)
        {
            DragAxis[] pa = { DragAxis.XY, DragAxis.XZ, DragAxis.YZ };
            for (int i = 0; i < 3; i++)
                _planeMats[i].AlbedoColor = _hovAxis == pa[i] ? ColPlaneH : ColPlane;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private void UpdateWorldScale()
    {
        float dist = (_camera.GlobalPosition - GlobalPosition).Length();
        if (dist < 0.001f) dist = 0.001f;
        float fovRad = Mathf.DegToRad(_camera.Fov);
        _worldScale = Mathf.Tan(fovRad * 0.5f) * 2f * dist * GizmoScreenSize;
        _worldScale = Mathf.Clamp(_worldScale, 0.001f, 100000f);
    }

    private void RefreshVisibility()
    {
        Visible = _mode != GizmoMode.None
               && _target != null && GodotObject.IsInstanceValid(_target);
        SetProcess(Visible);
    }

    /// <summary>Orient a CylinderMesh node so its +Y axis points along <paramref name="dir"/>.</summary>
    private static void OrientAlongAxis(Node3D node, Vector3 dir, Vector3 localPos)
    {
        dir = dir.Normalized();
        Basis b;
        if (dir.IsEqualApprox(Vector3.Up))
        {
            b = Basis.Identity;
        }
        else if (dir.IsEqualApprox(Vector3.Down))
        {
            b = new Basis(Vector3.Right, Mathf.Pi); // 180° around X
        }
        else
        {
            Vector3 axis = Vector3.Up.Cross(dir).Normalized();
            float   ang  = Vector3.Up.AngleTo(dir);
            b = new Basis(axis, ang);
        }
        node.Transform = new Transform3D(b, localPos);
    }

    private static StandardMaterial3D UnlitMat(Color color)
    {
        return new StandardMaterial3D
        {
            ShadingMode    = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor    = color,
            NoDepthTest    = true,    // always drawn on top
            RenderPriority = 10,
            Transparency   = color.A < 1f
                ? BaseMaterial3D.TransparencyEnum.Alpha
                : BaseMaterial3D.TransparencyEnum.Disabled,
        };
    }

    // ── ray math ─────────────────────────────────────────────────────────────
    private struct GizmoRay { public Vector3 Origin, Dir; }

    private GizmoRay MakeRay(Vector2 pos)
        => new GizmoRay { Origin = _camera.ProjectRayOrigin(pos), Dir = _camera.ProjectRayNormal(pos) };

    /// <summary>Intersect ray with infinite plane (no minimum-t guard — needed for drag tracking).</summary>
    private static Vector3? RayPlaneIntersectInfinite(GizmoRay ray, Vector3 planeOrigin, Vector3 planeNormal)
    {
        planeNormal = planeNormal.Normalized();
        float denom = ray.Dir.Dot(planeNormal);
        if (Mathf.Abs(denom) < 1e-8f)
            return null;
        float t = (planeOrigin - ray.Origin).Dot(planeNormal) / denom;
        return ray.Origin + ray.Dir * t;
    }

    private bool IsTranslateMode
        => _mode == GizmoMode.TranslateLocal || _mode == GizmoMode.TranslateWorld;

    private bool IsRotateMode
        => _mode == GizmoMode.RotateLocal || _mode == GizmoMode.RotateWorld;

    private Basis GetOrientationBasis()
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target))
            return Basis.Identity;

        if (_mode == GizmoMode.RotateLocal || _mode == GizmoMode.TranslateLocal)
            return _target.GlobalBasis;

        return Basis.Identity;
    }

    private Vector3 AxisToWorldDir(DragAxis axis)
    {
        Vector3 local = AxisToLocalDir(axis);
        if (local.LengthSquared() < 1e-8f)
            return Vector3.Zero;

        return GetOrientationBasis() * local;
    }

    private static Vector3 AxisToLocalDir(DragAxis axis) => axis switch
    {
        DragAxis.X   or DragAxis.RotX => Vector3.Right,
        DragAxis.Y   or DragAxis.RotY => Vector3.Up,
        DragAxis.Z   or DragAxis.RotZ => Vector3.Back,
        _                             => Vector3.Zero,
    };

    /// <summary>Plane normal for XY/XZ/YZ constraint squares (matches mesh orientation).</summary>
    private static Vector3 GetPlaneNormal(DragAxis axis) => axis switch
    {
        DragAxis.XY => Vector3.Right.Cross(Vector3.Up).Normalized(),   // +Z
        DragAxis.XZ => Vector3.Right.Cross(Vector3.Back).Normalized(), // +Y
        DragAxis.YZ => Vector3.Up.Cross(Vector3.Back).Normalized(),    // +X
        _           => Vector3.Zero,
    };

    private static bool IsPlane(DragAxis a)
        => a == DragAxis.XY || a == DragAxis.XZ || a == DragAxis.YZ;
}
