using System.Collections.Generic;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using OpenCAGE;

/// <summary>
/// Preview for SplinePath entities: position-style point markers, segment lines, and direction arrows.
/// </summary>
public partial class SplinePathPreview : FunctionEntityPreview
{
    private const string PointsParameter = "points";
    private const string LoopParameter = "loop";

    private static readonly Color SplinePathLineColor = new Color(1f, 0.55f, 0.1f, 1f);

    private const float TorusRadius = 0.11f;
    private const float TubeRadius = 0.014f;
    private const float AxisLength = 0.11f;
    private const float AxisWidth = 0.016f;
    private const float LineWidth = 0.012f;
    private const float ArrowHeadLength = 0.12f;
    private const float ArrowHeadWidth = 0.07f;
    private const float ArrowAlongSegment = 0.65f;

    private Node3D _root;
    private Node3D _pointsRoot;
    private Node3D _segmentsRoot;
    private readonly List<Node3D> _pointMarkers = new List<Node3D>();
    private readonly List<SegmentVisual> _segmentVisuals = new List<SegmentVisual>();
    private readonly List<Vector3> _localPoints = new List<Vector3>();

    private sealed class SegmentVisual
    {
        public Node3D Root;
        public Node3D LineStart;
        public Node3D LineEnd;
        public Node3D Arrow;
    }

    protected override Node3D GetVisibilityRoot() => _root;

    public override void CleanupPreviewVisuals()
    {
        for (int i = 0; i < _pointMarkers.Count; i++)
            PreviewVisualUtility.DestroyNode(_pointMarkers[i]);
        _pointMarkers.Clear();

        for (int i = 0; i < _segmentVisuals.Count; i++)
            PreviewVisualUtility.DestroyNode(_segmentVisuals[i].Root);
        _segmentVisuals.Clear();

        PreviewVisualUtility.DestroyNode(_root);
        _root = null;
        _pointsRoot = null;
        _segmentsRoot = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (!visible)
        {
            if (_root != null)
                _root.Visible = false;
            return;
        }

        if (!IsRootHierarchyValid())
            CleanupPreviewVisuals();

        EnsureRoot();
        _root.Visible = true;
        ReadSplinePoints(_localPoints);
        Color markerColor = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        SyncPointMarkers(markerColor);
        SyncSegments(SplinePathLineColor);
    }

    private bool IsRootHierarchyValid()
    {
        return _root != null && _pointsRoot != null && _segmentsRoot != null;
    }

    private void EnsureRoot()
    {
        if (IsRootHierarchyValid())
            return;

        if (_root != null)
            PreviewVisualUtility.DestroyNode(_root);

        _root = new Node3D { Name = "SplinePathPreview" };
        AddChild(_root);

        _pointsRoot = new Node3D { Name = "Points" };
        _root.AddChild(_pointsRoot);

        _segmentsRoot = new Node3D { Name = "Segments" };
        _root.AddChild(_segmentsRoot);
    }

    private void ReadSplinePoints(List<Vector3> destination)
    {
        destination.Clear();
        Parameter pointsParam = Entity.GetParameter(PointsParameter);
        if (pointsParam?.content == null || pointsParam.content.dataType != DataType.SPLINE)
            return;

        cSpline spline = (cSpline)pointsParam.content;
        if (spline.splinePoints == null)
            return;

        for (int i = 0; i < spline.splinePoints.Count; i++)
        {
            cTransform point = spline.splinePoints[i];
            if (point == null)
                continue;

            destination.Add(CathodeCoordinates.PositionToGodot(point.position));
        }
    }

    private bool IsLoopClosed()
    {
        Parameter loopParam = Entity.GetParameter(LoopParameter);
        return loopParam?.content is cBool loopValue && loopValue.value;
    }

    private int GetSegmentCount()
    {
        if (_localPoints.Count < 2)
            return 0;

        int segmentCount = _localPoints.Count - 1;
        if (IsLoopClosed())
            segmentCount++;
        return segmentCount;
    }

    private void GetSegmentEndpoints(int segmentIndex, out Vector3 from, out Vector3 to)
    {
        int lastPointIndex = _localPoints.Count - 1;
        if (segmentIndex < lastPointIndex)
        {
            from = _localPoints[segmentIndex];
            to = _localPoints[segmentIndex + 1];
            return;
        }

        from = _localPoints[lastPointIndex];
        to = _localPoints[0];
    }

    private void SyncPointMarkers(Color splineColor)
    {
        for (int i = _pointMarkers.Count - 1; i >= 0; i--)
        {
            if (_pointMarkers[i] == null || !GodotObject.IsInstanceValid(_pointMarkers[i]))
                _pointMarkers.RemoveAt(i);
        }

        while (_pointMarkers.Count < _localPoints.Count)
        {
            Node3D marker = PreviewVisualUtility.CreatePositionStyleMarker(
                "SplinePoint",
                _pointsRoot,
                splineColor,
                TorusRadius,
                TubeRadius,
                AxisLength,
                AxisWidth);
            _pointMarkers.Add(marker);
        }

        for (int i = _pointMarkers.Count - 1; i >= _localPoints.Count; i--)
        {
            PreviewVisualUtility.DestroyNode(_pointMarkers[i]);
            _pointMarkers.RemoveAt(i);
        }

        for (int i = 0; i < _localPoints.Count; i++)
        {
            Node3D marker = _pointMarkers[i];
            marker.Visible = true;
            marker.Position = _localPoints[i];
            marker.Rotation = Vector3.Zero;
            ApplyMarkerColor(marker, splineColor);
        }
    }

    private void ApplyMarkerColor(Node3D marker, Color torusColor)
    {
        if (marker == null)
            return;

        foreach (Node child in marker.GetChildren())
        {
            if (child is MeshInstance3D renderer)
            {
                if (child.Name.ToString().StartsWith("Axis"))
                {
                    Color axisColor = GetAxisColor(child.Name);
                    PreviewVisualUtility.ApplyColor(renderer, axisColor, opaque: true);
                }
                else
                {
                    PreviewVisualUtility.ApplyColor(renderer, torusColor, opaque: true);
                }
            }
        }
    }

    private static Color GetAxisColor(StringName axisName)
    {
        string name = axisName.ToString();
        switch (name)
        {
            case "AxisX":
                return Colors.Red;
            case "AxisY":
                return Colors.Green;
            case "AxisZ":
                return Colors.Blue;
            default:
                return Colors.White;
        }
    }

    private void SyncSegments(Color lineColor)
    {
        for (int i = _segmentVisuals.Count - 1; i >= 0; i--)
        {
            if (_segmentVisuals[i].Root == null || !GodotObject.IsInstanceValid(_segmentVisuals[i].Root))
                _segmentVisuals.RemoveAt(i);
        }

        int segmentCount = GetSegmentCount();

        while (_segmentVisuals.Count < segmentCount)
            _segmentVisuals.Add(CreateSegmentVisual());

        for (int i = _segmentVisuals.Count - 1; i >= segmentCount; i--)
        {
            PreviewVisualUtility.DestroyNode(_segmentVisuals[i].Root);
            _segmentVisuals.RemoveAt(i);
        }

        for (int i = 0; i < segmentCount; i++)
        {
            GetSegmentEndpoints(i, out Vector3 from, out Vector3 to);
            SegmentVisual segment = _segmentVisuals[i];
            segment.Root.Name = IsLoopClosed() && i == segmentCount - 1 ? "Segment_LoopClose" : "Segment_" + i;
            segment.Root.Visible = true;

            if (segment.LineStart != null)
                PreviewVisualUtility.DestroyNode(segment.LineStart);
            segment.LineStart = PreviewVisualUtility.CreateLineSegment("Line", segment.Root, from, to, LineWidth, lineColor);

            Vector3 direction = to - from;
            float length = direction.Length();
            if (length < 0.001f)
            {
                if (segment.Arrow != null)
                    segment.Arrow.Visible = false;
                continue;
            }

            Vector3 anchor = from + direction.Normalized() * (length * ArrowAlongSegment);
            float headLength = Mathf.Min(ArrowHeadLength, length * 0.35f);
            float headWidth = Mathf.Min(ArrowHeadWidth, length * 0.2f);

            if (segment.Arrow != null)
                PreviewVisualUtility.DestroyNode(segment.Arrow);

            segment.Arrow = PreviewVisualUtility.CreateDirectionArrow(
                "Arrow",
                segment.Root,
                anchor,
                direction,
                lineColor,
                headLength,
                headWidth);
        }
    }

    private SegmentVisual CreateSegmentVisual()
    {
        Node3D segmentRoot = new Node3D { Name = "Segment" };
        _segmentsRoot.AddChild(segmentRoot);

        return new SegmentVisual
        {
            Root = segmentRoot,
            LineStart = null,
            Arrow = null,
        };
    }
}
