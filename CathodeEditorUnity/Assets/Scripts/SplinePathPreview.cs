using System.Collections.Generic;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using OpenCAGE;
using UnityEngine;

/// <summary>
/// Preview for SplinePath entities: compact position-style point markers, always-visible segment lines, and direction arrows.
/// </summary>
public class SplinePathPreview : FunctionEntityPreview
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

    private GameObject _root;
    private Transform _pointsRoot;
    private Transform _segmentsRoot;
    private readonly List<GameObject> _pointMarkers = new List<GameObject>();
    private readonly List<SegmentVisual> _segmentVisuals = new List<SegmentVisual>();
    private readonly List<Vector3> _localPoints = new List<Vector3>();
    private MaterialPropertyBlock _propertyBlock;

    private sealed class SegmentVisual
    {
        public GameObject Root;
        public LineRenderer Line;
        public GameObject Arrow;
    }

    protected override GameObject GetVisibilityRoot() => _root;

    public override void CleanupPreviewVisuals()
    {
        for (int i = 0; i < _pointMarkers.Count; i++)
            PreviewVisualUtility.DestroyObject(_pointMarkers[i]);
        _pointMarkers.Clear();

        for (int i = 0; i < _segmentVisuals.Count; i++)
            PreviewVisualUtility.DestroyObject(_segmentVisuals[i].Root);
        _segmentVisuals.Clear();

        PreviewVisualUtility.DestroyObject(_root);
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
                _root.SetActive(false);
            return;
        }

        if (!IsRootHierarchyValid())
            CleanupPreviewVisuals();

        EnsureRoot();
        _root.SetActive(true);
        ReadSplinePoints(_localPoints);
        Color markerColor = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        SyncPointMarkers(markerColor);
        SyncSegments(SplinePathLineColor);
    }

    private bool IsRootHierarchyValid()
    {
        if (_root == null)
            return false;

        return _pointsRoot != null && _segmentsRoot != null;
    }

    private void EnsureRoot()
    {
        if (IsRootHierarchyValid())
            return;

        if (_root != null)
            PreviewVisualUtility.DestroyObject(_root);

        _root = new GameObject("SplinePathPreview");
        _root.transform.SetParent(transform, false);

        GameObject pointsObject = new GameObject("Points");
        pointsObject.transform.SetParent(_root.transform, false);
        _pointsRoot = pointsObject.transform;

        GameObject segmentsObject = new GameObject("Segments");
        segmentsObject.transform.SetParent(_root.transform, false);
        _segmentsRoot = segmentsObject.transform;

#if UNITY_EDITOR && !LOCAL_DEV
        _root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        pointsObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        segmentsObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
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

            destination.Add(point.position);
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
            if (_pointMarkers[i] == null)
                _pointMarkers.RemoveAt(i);
        }

        while (_pointMarkers.Count < _localPoints.Count)
        {
            GameObject marker = PreviewVisualUtility.CreatePositionStyleMarker(
                "SplinePoint",
                _pointsRoot,
                splineColor,
                TorusRadius,
                TubeRadius,
                AxisLength,
                AxisWidth,
                ref _propertyBlock);
            _pointMarkers.Add(marker);
        }

        for (int i = _pointMarkers.Count - 1; i >= _localPoints.Count; i--)
        {
            if (_pointMarkers[i] != null)
                Destroy(_pointMarkers[i]);
            _pointMarkers.RemoveAt(i);
        }

        for (int i = 0; i < _localPoints.Count; i++)
        {
            GameObject marker = _pointMarkers[i];
            marker.SetActive(true);
            marker.transform.localPosition = _localPoints[i];
            marker.transform.localRotation = Quaternion.identity;
            ApplyMarkerColor(marker, splineColor);
        }
    }

    private void ApplyMarkerColor(GameObject marker, Color torusColor)
    {
        if (marker == null)
            return;

        for (int i = 0; i < marker.transform.childCount; i++)
        {
            Transform child = marker.transform.GetChild(i);
            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            if (renderer == null)
                continue;

            if (child.name.StartsWith("Axis"))
            {
                Color axisColor = GetAxisColor(child.name);
                PreviewVisualUtility.ApplyColor(renderer, axisColor, ref _propertyBlock, opaque: true);
            }
            else
            {
                PreviewVisualUtility.ApplyColor(renderer, torusColor, ref _propertyBlock, opaque: true);
            }
        }
    }

    private static Color GetAxisColor(string axisName)
    {
        switch (axisName)
        {
            case "AxisX":
                return Color.red;
            case "AxisY":
                return Color.green;
            case "AxisZ":
                return Color.blue;
            default:
                return Color.white;
        }
    }

    private void SyncSegments(Color lineColor)
    {
        for (int i = _segmentVisuals.Count - 1; i >= 0; i--)
        {
            if (_segmentVisuals[i].Root == null)
                _segmentVisuals.RemoveAt(i);
        }

        int segmentCount = GetSegmentCount();

        while (_segmentVisuals.Count < segmentCount)
            _segmentVisuals.Add(CreateSegmentVisual());

        for (int i = _segmentVisuals.Count - 1; i >= segmentCount; i--)
        {
            if (_segmentVisuals[i].Root != null)
                Destroy(_segmentVisuals[i].Root);
            _segmentVisuals.RemoveAt(i);
        }

        for (int i = 0; i < segmentCount; i++)
        {
            GetSegmentEndpoints(i, out Vector3 from, out Vector3 to);
            SegmentVisual segment = _segmentVisuals[i];
            segment.Root.name = IsLoopClosed() && i == segmentCount - 1 ? "Segment_LoopClose" : "Segment_" + i;
            segment.Root.SetActive(true);

            segment.Line.positionCount = 2;
            segment.Line.SetPosition(0, from);
            segment.Line.SetPosition(1, to);
            PreviewVisualUtility.ConfigureOverlayLineRenderer(segment.Line, lineColor, LineWidth);

            Vector3 direction = to - from;
            float length = direction.magnitude;
            if (length < 0.001f)
            {
                if (segment.Arrow != null)
                    segment.Arrow.SetActive(false);
                continue;
            }

            Vector3 anchor = from + direction.normalized * (length * ArrowAlongSegment);
            float headLength = Mathf.Min(ArrowHeadLength, length * 0.35f);
            float headWidth = Mathf.Min(ArrowHeadWidth, length * 0.2f);

            if (segment.Arrow != null)
                Destroy(segment.Arrow);

            segment.Arrow = PreviewVisualUtility.CreateDirectionArrow(
                "Arrow",
                segment.Root.transform,
                anchor,
                direction,
                lineColor,
                headLength,
                headWidth,
                ref _propertyBlock);
        }
    }

    private SegmentVisual CreateSegmentVisual()
    {
        GameObject segmentRoot = new GameObject("Segment");
        segmentRoot.transform.SetParent(_segmentsRoot, false);

        LineRenderer line = segmentRoot.AddComponent<LineRenderer>();
        PreviewVisualUtility.ConfigureOverlayLineRenderer(line, SplinePathLineColor, LineWidth);

#if UNITY_EDITOR && !LOCAL_DEV
        segmentRoot.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif

        return new SegmentVisual()
        {
            Root = segmentRoot,
            Line = line,
            Arrow = null,
        };
    }
}
