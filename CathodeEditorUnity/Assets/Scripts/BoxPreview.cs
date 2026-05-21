using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Semi-transparent box volume preview for Box-like function entities.
/// half_dimensions: X/Z are half-extents each side; Y is half-height above the origin (full size = half_dimensions * 2).
/// position on the parent entity defines the transform.
/// </summary>
public class BoxPreview : FunctionEntityPreview
{
    private const string HalfDimensionsParameter = "half_dimensions";
    private const float PreviewAlpha = 0.24f;

    private static readonly HashSet<FunctionType> BoxLikeFunctionTypes = new HashSet<FunctionType>()
    {
        FunctionType.Box,
        FunctionType.PlayerTriggerBox,
        FunctionType.PlayerUseTriggerBox,
        FunctionType.SoundBarrier,
        FunctionType.SoundEnvironmentZone,
        FunctionType.SpottingExclusionArea,
        FunctionType.CoverExclusionArea,
        FunctionType.NavMeshArea,
        FunctionType.NavMeshExclusionArea,
        FunctionType.NavMeshWalkablePlatform,
        FunctionType.NPC_AreaBox,
    };

    private static readonly Dictionary<FunctionType, Color> BoxLikeColors = new Dictionary<FunctionType, Color>()
    {
        { FunctionType.Box, new Color(0.85f, 0.85f, 0.85f, PreviewAlpha) },
        { FunctionType.PlayerTriggerBox, new Color(0.25f, 0.85f, 1f, PreviewAlpha) },
        { FunctionType.PlayerUseTriggerBox, new Color(0.3f, 1f, 0.45f, PreviewAlpha) },
        { FunctionType.SoundBarrier, new Color(0.75f, 0.35f, 1f, PreviewAlpha) },
        { FunctionType.SoundEnvironmentZone, new Color(0.55f, 0.45f, 0.95f, PreviewAlpha) },
        { FunctionType.SpottingExclusionArea, new Color(1f, 0.35f, 0.35f, PreviewAlpha) },
        { FunctionType.CoverExclusionArea, new Color(1f, 0.6f, 0.2f, PreviewAlpha) },
        { FunctionType.NavMeshArea, new Color(0.25f, 0.55f, 1f, PreviewAlpha) },
        { FunctionType.NavMeshExclusionArea, new Color(0.85f, 0.25f, 0.35f, PreviewAlpha) },
        { FunctionType.NavMeshWalkablePlatform, new Color(0.45f, 1f, 0.55f, PreviewAlpha) },
        { FunctionType.NPC_AreaBox, new Color(1f, 0.95f, 0.3f, PreviewAlpha) },
    };

    private static Material _sharedMaterial;
    private static readonly Dictionary<FunctionType, Color> GeneratedColorCache = new Dictionary<FunctionType, Color>();
    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");

    private CommandsUtils _utils;
    private MaterialPropertyBlock _propertyBlock;
    private GameObject _volume;

    /// <summary>
    /// True when the function type (or any type in its inheritance chain) is Box or a known box-like type.
    /// </summary>
    public static bool ShouldShowBoxPreview(FunctionEntity entity, CommandsUtils utils)
    {
        if (entity == null || utils == null || !entity.function.IsFunctionType)
            return false;

        FunctionType? functionType = entity.function.AsFunctionType;
        while (functionType != null)
        {
            if (BoxLikeFunctionTypes.Contains(functionType.Value))
                return true;

            functionType = utils.GetInheritedFunction(functionType.Value);
        }

        return false;
    }

    public void Setup(FunctionEntity entity, CommandsUtils utils)
    {
        _utils = utils;
        base.Setup(entity);
    }

    public override void Refresh()
    {
        if (Entity == null)
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

        EnsureSharedMaterial();
        renderer.sharedMaterial = _sharedMaterial;

        if (_propertyBlock == null)
            _propertyBlock = new MaterialPropertyBlock();

        _propertyBlock.SetColor(ColorPropertyId, GetPreviewColor(Entity));
        renderer.SetPropertyBlock(_propertyBlock);
    }

    /// <summary>
    /// Colour is based on the entity's concrete function type. Listed types use fixed colours;
    /// other box-like inherited types get a stable unique colour from their FunctionType.
    /// </summary>
    private static Color GetPreviewColor(FunctionEntity entity)
    {
        FunctionType functionType = entity.function.AsFunctionType;
        if (BoxLikeColors.TryGetValue(functionType, out Color color))
            return color;

        if (!GeneratedColorCache.TryGetValue(functionType, out color))
        {
            color = CreateColorForFunctionType(functionType);
            GeneratedColorCache[functionType] = color;
        }
        return color;
    }

    private static Color CreateColorForFunctionType(FunctionType type)
    {
        uint hash = (uint)type;
        float hue = (hash * 0.6180339887f) % 1f;
        Color rgb = Color.HSVToRGB(hue, 0.65f, 0.92f);
        rgb.a = PreviewAlpha;
        return rgb;
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

        EnsureSharedMaterial();

        _volume = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _volume.name = "BoxPreview";
        _volume.transform.SetParent(transform, false);

        Collider collider = _volume.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        MeshRenderer renderer = _volume.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = _sharedMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

#if UNITY_EDITOR && !LOCAL_DEV
        _volume.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
    }

    private static void EnsureSharedMaterial()
    {
        if (_sharedMaterial != null)
            return;

        Shader shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
        if (shader == null)
            shader = Shader.Find("Standard");

        _sharedMaterial = new Material(shader);
        _sharedMaterial.renderQueue = 3000;
    }
}
