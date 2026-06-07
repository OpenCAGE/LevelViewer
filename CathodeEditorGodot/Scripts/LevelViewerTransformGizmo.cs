using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// In-viewport transform gizmo attached to the currently selected entity.
/// Modes: Translate, local/world rotation rings, or hidden.
/// <see cref="OnTransformChanged"/> fires on every drag update so callers can push
/// position / rotation back to OpenCAGE.
/// </summary>
public partial class LevelViewerTransformGizmo : Node3D
{
    public enum GizmoMode { None, Translate, RotateLocal, RotateWorld }

    /// <summary>Fired when dragging moves/rotates the target. Args: local position, local euler rotation degrees.</summary>
    public Action<Vector3, Vector3> OnTransformChanged;

    /// <summary>Fired once when a drag ends — use for pick-cache invalidation (not every mouse-move frame).</summary>
    public Action<Node3D> OnDragCommitted;

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
    private Node3D     _target;
    private Camera3D   _camera;
    private DragAxis   _dragAxis  = DragAxis.None;
    private DragAxis   _hovAxis   = DragAxis.None;
    private float      _worldScale= 1f;

    // drag bookkeeping — pivot stays fixed for the whole drag
    private Vector3 _dragPivot;        // world position at drag start (never moves during drag)
    private Vector3 _dragStartPos;
    private Vector3 _dragStartRot;
    private Vector3 _dragStartHit;       // ray-plane hit at mouse-down
    private Vector3 _dragAxisDir;      // constrained axis (translate) or rotation axis (rotate)
    private Vector3 _dragPlaneNormal;  // plane used for ray intersection during drag
    private bool    _isDragging;

    // rotation-only: incremental angle tracking (avoids acos wrap at ±180°)
    private Quaternion _dragStartGlobalQuat;
    private Vector3    _dragRotRefDir;
    private float      _dragAccumAngleRad;

    // mesh children
    private StandardMaterial3D[] _axisMats;  // 0=X 1=Y 2=Z  (shared by shaft+head)
    private StandardMaterial3D[] _planeMats; // 0=XY 1=XZ 2=YZ
    private StandardMaterial3D[] _ringMats;  // 0=RotX 1=RotY 2=RotZ

    public bool IsDragging => _isDragging;

    /// <summary>True when a visible gizmo handle would be hit at this screen position (ignores depth).</summary>
    public bool HitsAtScreen(Vector2 mousePos)
        => Visible && HitTest(mousePos) != DragAxis.None;

    // ─────────────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        TopLevel = true;   // position ourselves in world space, not relative to parent
        Visible  = false;
    }

    public void SetMode(GizmoMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        _isDragging = false;
        RebuildMeshes();
        RefreshVisibility();
    }

    public GizmoMode Mode => _mode;

    public void SetTarget(Node3D target, Camera3D camera)
    {
        if (_isDragging)
            CommitDrag();

        _target  = target;
        _camera  = camera;
        _isDragging = false;
        if (!PreviewVisualUtility.HasValidWorldAnchor(_target))
        {
            _target = null;
            Visible = false;
            return;
        }

        GlobalPosition = _target.GlobalPosition;
        RefreshVisibility();
    }

    public void ClearTarget()
    {
        if (_isDragging)
            CommitDrag();

        _target = null;
        _isDragging = false;
        Visible = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    public override void _Process(double delta)
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

        if (!_isDragging)
            GlobalPosition = _target.GlobalPosition;

        GlobalBasis = GetOrientationBasis();

        UpdateWorldScale();
        Scale   = Vector3.One * _worldScale;
        Visible = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input forwarding (called by LevelViewerCamera)
    // ─────────────────────────────────────────────────────────────────────────

    public bool HandleMouseMotion(Vector2 mousePos)
    {
        if (_mode == GizmoMode.None || !Visible)
            return false;

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

    public bool HandleMouseButtonDown(Vector2 mousePos)
    {
        if (_mode == GizmoMode.None || !Visible || _target == null)
            return false;

        DragAxis hit = HitTest(mousePos);
        if (hit == DragAxis.None)
            return false;

        _dragAxis   = hit;
        _hovAxis    = hit;
        _isDragging = true;
        ApplyHighlight();
        BeginDrag(mousePos);
        return true;
    }

    public bool HandleMouseButtonUp(Vector2 mousePos)
    {
        if (!_isDragging)
            return false;

        CommitDrag();
        _isDragging = false;
        _dragAxis   = DragAxis.None;
        _hovAxis    = HitTest(mousePos);
        ApplyHighlight();
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Drag mechanics  (camera-facing plane for axis; constraint plane for squares)
    // ─────────────────────────────────────────────────────────────────────────
    private void BeginDrag(Vector2 mousePos)
    {
        _dragPivot    = _target.GlobalPosition;
        _dragStartPos = _dragPivot;
        _dragStartRot = _target.RotationDegrees;

        if (_mode == GizmoMode.Translate)
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

        if (_mode == GizmoMode.Translate)
        {
            Vector3 worldDelta = currentHit.Value - _dragStartHit;

            if (IsPlane(_dragAxis))
            {
                // Free movement within the picked plane.
                _target.GlobalPosition = _dragStartPos + worldDelta;
            }
            else
            {
                // Project camera-plane movement onto the constrained axis.
                float axisDelta = worldDelta.Dot(_dragAxisDir);
                _target.GlobalPosition = _dragStartPos + _dragAxisDir * axisDelta;
            }

            GlobalPosition = _target.GlobalPosition;
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

            if (_mode == GizmoMode.RotateWorld)
            {
                Vector3 axis = _dragAxisDir.Normalized();
                Quaternion worldDelta = new Quaternion(axis, _dragAccumAngleRad);
                SetGlobalQuaternion(_target, worldDelta * _dragStartGlobalQuat);
            }
            else
            {
                Vector3 localAxis = AxisToLocalDir(_dragAxis).Normalized();
                Quaternion localDelta = new Quaternion(localAxis, _dragAccumAngleRad);
                SetGlobalQuaternion(_target, _dragStartGlobalQuat * localDelta);
                GlobalBasis = _target.GlobalBasis;
            }
        }

        ApplyDragSnap();

        // Sync to OpenCAGE / all entity instances only on CommitDrag — not every mouse-move frame.
    }

    private void ApplyDragSnap()
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target))
            return;

        float grid = LevelViewerTransformSnap.GridSize;
        if (grid > 0f && _mode == GizmoMode.Translate)
        {
            Vector3 pos = _target.Position;
            _target.Position = new Vector3(
                LevelViewerTransformSnap.SnapValue(pos.X, grid),
                LevelViewerTransformSnap.SnapValue(pos.Y, grid),
                LevelViewerTransformSnap.SnapValue(pos.Z, grid));
            GlobalPosition = _target.GlobalPosition;
        }

        float rotationStep = LevelViewerTransformSnap.RotationDegrees;
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
        if (_mode == GizmoMode.Translate && !IsPlane(_dragAxis) && _dragAxisDir.LengthSquared() > 1e-8f)
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
        OnTransformChanged?.Invoke(_target.Position, _target.RotationDegrees);
        OnDragCommitted?.Invoke(_target);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Hit testing (screen-space — ignores depth so handles win over scene geometry)
    // ─────────────────────────────────────────────────────────────────────────
    private DragAxis HitTest(Vector2 mousePos)
    {
        if (_camera == null || !Visible)
            return DragAxis.None;

        DragAxis bestAxis = DragAxis.None;
        float bestDist    = float.MaxValue;

        if (_mode == GizmoMode.Translate)
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
        Vector2 centre = WorldToScreen(GlobalPosition);
        Vector2 end    = WorldToScreen(GlobalPosition + worldAxis * axisLength);
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
        float minSegDist = float.MaxValue;

        for (int i = 0; i <= RingHitSamples; i++)
        {
            float angle = i * Mathf.Tau / RingHitSamples;
            Vector3 worldPoint = GlobalPosition
                + (basisU * Mathf.Cos(angle) + basisV * Mathf.Sin(angle)) * radius;
            Vector2 screenPoint = WorldToScreen(worldPoint);

            if (i > 0)
                minSegDist = Mathf.Min(minSegDist, ScreenDistToSegment(mouse, prevScreen, screenPoint));

            prevScreen = screenPoint;
        }

        if (minSegDist < RingScreenPickPx && minSegDist < bestDist)
        {
            bestDist = minSegDist;
            bestAxis = axis;
        }
    }

    private Vector2 WorldToScreen(Vector3 world)
        => _camera.UnprojectPosition(world);

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

        if (_mode == GizmoMode.Translate)
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

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color]  = cols.ToArray();
        arrays[(int)Mesh.ArrayType.Index]  = idxs.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
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

    private bool IsRotateMode
        => _mode == GizmoMode.RotateLocal || _mode == GizmoMode.RotateWorld;

    private Basis GetOrientationBasis()
    {
        if (_mode == GizmoMode.RotateLocal
            && _target != null && GodotObject.IsInstanceValid(_target))
        {
            return _target.GlobalBasis;
        }

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
