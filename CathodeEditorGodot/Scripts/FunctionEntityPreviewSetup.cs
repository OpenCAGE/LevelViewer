using CATHODE.Scripting;
using Godot;
using OpenCAGE;

public static class FunctionEntityPreviewSetup
{
    public static bool TryAddPreview(
        AlienScene scene,
        FunctionEntity function,
        Node3D entityNode,
        CommandsUtils utils,
        ShortGuid ownerComposite,
        bool geometryOnly = false)
    {
        if (function == null || entityNode == null || !function.function.IsFunctionType)
            return false;

        uint ownerCompositeId = ownerComposite.AsUInt32;
        FunctionType functionType = function.function.AsFunctionType;

        if (geometryOnly)
        {
            if (functionType != FunctionType.ModelReference)
                return false;

            ModelReferencePreview modelPreview = new ModelReferencePreview();
            entityNode.AddChild(modelPreview);
            modelPreview.Setup(scene, function, ownerCompositeId);
            return true;
        }

        if (!RenderFilterDefinitions.IsSupported(functionType))
        {
            if (functionType == FunctionType.ModelReference)
            {
                ModelReferencePreview preview = new ModelReferencePreview();
                entityNode.AddChild(preview);
                preview.Setup(scene, function, ownerCompositeId);
                return true;
            }
            return false;
        }

        switch (RenderFilterDefinitions.GetPreviewKind(functionType))
        {
            case RenderPreviewKind.Box:
            {
                BoxPreview preview = new BoxPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, utils, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.Sound:
            {
                IconBillboardPreview preview = new IconBillboardPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, IconBillboardPreview.IconKind.Sound, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.PositionMarker:
            {
                PositionMarkerPreview preview = new PositionMarkerPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.SoundEnvironmentMarker:
            {
                SoundEnvironmentMarkerPreview preview = new SoundEnvironmentMarkerPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.LightReference:
            {
                IconBillboardPreview preview = new IconBillboardPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, IconBillboardPreview.IconKind.Light, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.Character:
            {
                CharacterPreview preview = new CharacterPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.ParticleEmitter:
            {
                IconBillboardPreview preview = new IconBillboardPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, IconBillboardPreview.IconKind.Particle, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.SplinePath:
            {
                SplinePathPreview preview = new SplinePathPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.EnvironmentMap:
            {
                EnvironmentMapPreview preview = new EnvironmentMapPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.SoundNetworkNode:
            {
                SoundNetworkNodePreview preview = new SoundNetworkNodePreview();
                entityNode.AddChild(preview);
                preview.Setup(function, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.SoundObject:
            {
                IconBillboardPreview preview = new IconBillboardPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, IconBillboardPreview.IconKind.SoundObject, ownerCompositeId);
                return true;
            }
            case RenderPreviewKind.CameraResource:
            {
                IconBillboardPreview preview = new IconBillboardPreview();
                entityNode.AddChild(preview);
                preview.Setup(function, IconBillboardPreview.IconKind.Camera, ownerCompositeId);
                return true;
            }
            default:
                return false;
        }
    }
}
