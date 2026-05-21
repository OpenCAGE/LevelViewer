using CATHODE.Scripting;
using UnityEngine;

public class PositionMarkerPreview : FunctionEntityPreview
{
    private static readonly Color AxisX = Color.red;
    private static readonly Color AxisY = Color.green;
    private static readonly Color AxisZ = Color.blue;

    protected GameObject _root;
    protected MaterialPropertyBlock _propertyBlock;
    protected virtual float TorusRadius => 0.24f;
    protected virtual float TubeRadius => 0.028f;
    protected virtual float AxisLength => 0.22f;
    protected virtual float AxisWidth => 0.03f;

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsVisible(Entity);
        if (_root != null)
            _root.SetActive(visible);
        if (!visible)
            return;

        EnsureVisual();
        ApplyColors(PreviewVisualUtility.GetOpaquePreviewColor(Entity));
    }

    protected virtual void ApplyColors(Color torusColor)
    {
        torusColor.a = 1f;
        for (int i = 0; i < _root.transform.childCount; i++)
        {
            Transform child = _root.transform.GetChild(i);
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
                return AxisX;
            case "AxisY":
                return AxisY;
            case "AxisZ":
                return AxisZ;
            default:
                return Color.white;
        }
    }

    protected virtual void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new GameObject("PositionMarkerPreview");
        _root.transform.SetParent(transform, false);

        Mesh torusMesh = PreviewVisualUtility.CreateTorusMesh(TorusRadius, TubeRadius);
        GameObject torus = PreviewVisualUtility.CreateMeshPreview("Torus", _root.transform, torusMesh, Color.white, ref _propertyBlock, opaque: true);

        CreateAxisLine("AxisX", Vector3.right, AxisLength, AxisX);
        CreateAxisLine("AxisY", Vector3.up, AxisLength, AxisY);
        CreateAxisLine("AxisZ", Vector3.forward, AxisLength, AxisZ);
    }

    protected void CreateAxisLine(string name, Vector3 direction, float length, Color color)
    {
        Vector3 axis = direction.normalized;
        GameObject axisObject = PreviewVisualUtility.CreatePrimitivePreview(name, _root.transform, PrimitiveType.Cylinder, color, ref _propertyBlock, opaque: true);
        axisObject.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        axisObject.transform.localPosition = axis * (length * 0.5f);
        axisObject.transform.localScale = new Vector3(AxisWidth, length * 0.5f, AxisWidth);
    }
}
