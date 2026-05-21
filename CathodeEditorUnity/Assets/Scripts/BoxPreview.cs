using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using OpenCAGE;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Semi-transparent box volume preview for Box-like function entities.
/// half_dimensions: X/Z are half-extents each side; Y is half-height above the origin (full size = half_dimensions * 2).
/// position on the parent entity defines the transform.
/// </summary>
public class BoxPreview : FunctionEntityPreview
{
    private const string HalfDimensionsParameter = "half_dimensions";

    private CommandsUtils _utils;
    private MaterialPropertyBlock _propertyBlock;
    private GameObject _volume;

    public static bool ShouldShowBoxPreview(FunctionEntity entity, CommandsUtils utils)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        return RenderFilterDefinitions.GetPreviewKind(entity.function.AsFunctionType) == RenderPreviewKind.Box;
    }

    public void Setup(FunctionEntity entity, CommandsUtils utils, uint ownerCompositeId = 0)
    {
        _utils = utils;
        base.Setup(entity, ownerCompositeId);
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (_volume != null)
            _volume.SetActive(visible);
        if (!visible)
            return;

        EnsureVolume();
        ApplyPreviewColor();

        Vector3 halfDimensions = GetHalfDimensions(Entity);
        halfDimensions.x = Mathf.Max(halfDimensions.x, 0.01f);
        halfDimensions.y = Mathf.Max(halfDimensions.y, 0.01f);
        halfDimensions.z = Mathf.Max(halfDimensions.z, 0.01f);

        _volume.transform.localScale = halfDimensions * 2f;
        _volume.transform.localPosition = new Vector3(0f, halfDimensions.y, 0f);
        _volume.transform.localRotation = Quaternion.identity;
    }

    private void ApplyPreviewColor()
    {
        if (_volume == null)
            return;

        MeshRenderer renderer = _volume.GetComponent<MeshRenderer>();
        if (renderer == null)
            return;

        PreviewVisualUtility.ApplyTransparentColor(renderer, PreviewVisualUtility.GetPreviewColor(Entity), ref _propertyBlock);
    }

    private Vector3 GetHalfDimensions(FunctionEntity entity)
    {
        Parameter halfDimensionsParam = entity.GetParameter(HalfDimensionsParameter);
        if (halfDimensionsParam?.content != null && halfDimensionsParam.content.dataType == DataType.VECTOR)
            return ((cVector3)halfDimensionsParam.content).value;

        return new Vector3(0.5f, 1f, 0.5f);
    }

    private void EnsureVolume()
    {
        if (_volume != null)
            return;

        _volume = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _volume.name = "BoxPreview";
        _volume.transform.SetParent(transform, false);

        Collider collider = _volume.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        MeshRenderer renderer = _volume.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = PreviewVisualUtility.SharedBoxMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

#if UNITY_EDITOR && !LOCAL_DEV
        _volume.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
    }
}
