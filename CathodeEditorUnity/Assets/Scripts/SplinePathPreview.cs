using System.Collections.Generic;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using OpenCAGE;
using UnityEngine;

/// <summary>
/// Preview for SplinePath entities: compact position-style point markers, always-visible segment lines, and direction arrows.
/// </summary>
[ExecuteAlways]
public class SplinePathPreview : FunctionEntityPreview
{
    private const string PointsParameter = "points";

    private const float TorusRadius = 0.11f;
    private const float TubeRadius = 0.014f;
    private const float AxisLength = 0.11f;
    private const float AxisWidth = 0.016f;
    private const float LineWidth = 0.025f;
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

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (_root != null)
            _root.SetActive(visible);
        if (!visible)
            return;

        EnsureRoot();
        ReadSplinePoints(_localPoints);
        Color splineColor = PreviewVisualUtility.GetOpaquePreviewColor(Entity);
        SyncPointMarkers(splineColor);
        SyncSegments(splineColor);
    }

    private void EnsureRoot()
    {
        if (_root != null)
            return;

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

    private void SyncPointMarkers(Color splineColor)
    {
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

    private void SyncSegments(Color splineColor)
    {
        int segmentCount = Mathf.Max(0, _localPoints.Count - 1);

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
            Vector3 from = _localPoints[i];
            Vector3 to = _localPoints[i + 1];
            SegmentVisual segment = _segmentVisuals[i];
            segment.Root.name = "Segment_" + i;
            segment.Root.SetActive(true);

            segment.Line.positionCount = 2;
            segment.Line.SetPosition(0, from);
            segment.Line.SetPosition(1, to);
            PreviewVisualUtility.ConfigureLineRenderer(segment.Line, splineColor, LineWidth);

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
                splineColor,
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
        PreviewVisualUtility.ConfigureLineRenderer(line, Color.white, LineWidth);

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
