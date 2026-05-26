using CATHODE.Scripting;
using OpenCAGE;

public static class FunctionEntityPreviewSetup
{
    public static bool TryAddPreview(AlienScene scene, FunctionEntity function, UnityEngine.GameObject entityGO, CommandsUtils utils, ShortGuid ownerComposite)
    {
        if (function == null || entityGO == null || !function.function.IsFunctionType)
            return false;

        uint ownerCompositeId = ownerComposite.AsUInt32;
        FunctionType functionType = function.function.AsFunctionType;

        if (!RenderFilterDefinitions.IsSupported(functionType))
        {
            if (functionType == FunctionType.ModelReference)
            {
                entityGO.AddComponent<ModelReferencePreview>().Setup(scene, function, ownerCompositeId);
                return true;
            }
            return false;
        }

        switch (RenderFilterDefinitions.GetPreviewKind(functionType))
        {
            case RenderPreviewKind.Box:
                entityGO.AddComponent<BoxPreview>().Setup(function, utils, ownerCompositeId);
                return true;
            case RenderPreviewKind.Sound:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Sound, ownerCompositeId);
                return true;
            case RenderPreviewKind.PositionMarker:
                entityGO.AddComponent<PositionMarkerPreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.SoundEnvironmentMarker:
                entityGO.AddComponent<SoundEnvironmentMarkerPreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.LightReference:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Light, ownerCompositeId);
                return true;
            case RenderPreviewKind.Character:
                entityGO.AddComponent<CharacterPreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.ParticleEmitter:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Particle, ownerCompositeId);
                return true;
            case RenderPreviewKind.SplinePath:
                entityGO.AddComponent<SplinePathPreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.EnvironmentMap:
                entityGO.AddComponent<EnvironmentMapPreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.SoundNetworkNode:
                entityGO.AddComponent<SoundNetworkNodePreview>().Setup(function, ownerCompositeId);
                return true;
            case RenderPreviewKind.SoundObject:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.SoundObject, ownerCompositeId);
                return true;
            case RenderPreviewKind.CameraResource:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Camera, ownerCompositeId);
                return true;
            default:
                return false;
        }
    }
}
