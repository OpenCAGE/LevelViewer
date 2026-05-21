using CATHODE.Scripting;
using OpenCAGE;

public static class FunctionEntityPreviewSetup
{
    public static bool TryAddPreview(AlienScene scene, FunctionEntity function, UnityEngine.GameObject entityGO, CommandsUtils utils)
    {
        if (function == null || entityGO == null || !function.function.IsFunctionType)
            return false;

        FunctionType functionType = function.function.AsFunctionType;

        if (!RenderFilterDefinitions.IsSupported(functionType))
        {
            if (functionType == FunctionType.ModelReference)
            {
                entityGO.AddComponent<ModelReferencePreview>().Setup(scene, function);
                return true;
            }
            return false;
        }

        switch (RenderFilterDefinitions.GetPreviewKind(functionType))
        {
            case RenderPreviewKind.Box:
                entityGO.AddComponent<BoxPreview>().Setup(function, utils);
                return true;
            case RenderPreviewKind.Sound:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Sound);
                return true;
            case RenderPreviewKind.PositionMarker:
                entityGO.AddComponent<PositionMarkerPreview>().Setup(function);
                return true;
            case RenderPreviewKind.SoundEnvironmentMarker:
                entityGO.AddComponent<SoundEnvironmentMarkerPreview>().Setup(function);
                return true;
            case RenderPreviewKind.LightReference:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Light);
                return true;
            case RenderPreviewKind.Character:
                entityGO.AddComponent<CharacterPreview>().Setup(function);
                return true;
            case RenderPreviewKind.ParticleEmitter:
                entityGO.AddComponent<IconBillboardPreview>().Setup(function, IconBillboardPreview.IconKind.Particle);
                return true;
            default:
                return false;
        }
    }
}
