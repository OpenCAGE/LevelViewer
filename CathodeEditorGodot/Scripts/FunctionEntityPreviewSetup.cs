using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using System;

public static class FunctionEntityPreviewSetup
{
    public static bool TryAddPreview(
        AlienScene scene,
        FunctionEntity function,
        Node3D entityNode,
        CommandsUtils utils,
        ShortGuid ownerComposite,
        bool geometryOnly = false,
        uint mappingScopeInstanceEntityId = 0)
    {
        if (function == null || entityNode == null || !function.function.IsFunctionType)
            return false;

        uint ownerCompositeId = ownerComposite.AsUInt32;
        FunctionType functionType = function.function.AsFunctionType;

        if (geometryOnly)
        {
            if (functionType != FunctionType.ModelReference)
                return false;

            return AddPreview<ModelReferencePreview>(
                entityNode,
                p => p.Setup(scene, function, ownerCompositeId, mappingScopeInstanceEntityId));
        }

        if (!RenderFilterDefinitions.IsSupported(functionType))
        {
            if (functionType == FunctionType.ModelReference)
            {
                return AddPreview<ModelReferencePreview>(
                    entityNode,
                    p => p.Setup(scene, function, ownerCompositeId, mappingScopeInstanceEntityId));
            }
            return false;
        }

        switch (RenderFilterDefinitions.GetPreviewKind(functionType))
        {
            case RenderPreviewKind.Box:
                return AddPreview<BoxPreview>(entityNode, p => p.Setup(function, utils, ownerCompositeId));
            case RenderPreviewKind.Sound:
                return AddIcon(entityNode, function, IconBillboardPreview.IconKind.Sound, ownerCompositeId);
            case RenderPreviewKind.PositionMarker:
                return AddPreview<PositionMarkerPreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.SoundEnvironmentMarker:
                return AddPreview<SoundEnvironmentMarkerPreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.LightReference:
                return AddIcon(entityNode, function, IconBillboardPreview.IconKind.Light, ownerCompositeId);
            case RenderPreviewKind.Character:
                return AddPreview<CharacterPreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.ParticleEmitter:
                return AddIcon(entityNode, function, IconBillboardPreview.IconKind.Particle, ownerCompositeId);
            case RenderPreviewKind.SplinePath:
                return AddPreview<SplinePathPreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.EnvironmentMap:
                return AddPreview<EnvironmentMapPreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.SoundNetworkNode:
                return AddPreview<SoundNetworkNodePreview>(entityNode, p => p.Setup(function, ownerCompositeId));
            case RenderPreviewKind.SoundObject:
                return AddIcon(entityNode, function, IconBillboardPreview.IconKind.SoundObject, ownerCompositeId);
            case RenderPreviewKind.CameraResource:
                return AddIcon(entityNode, function, IconBillboardPreview.IconKind.Camera, ownerCompositeId);
            default:
                return false;
        }
    }

    private static bool AddIcon(
        Node3D entityNode,
        FunctionEntity function,
        IconBillboardPreview.IconKind kind,
        uint ownerCompositeId)
        => AddPreview<IconBillboardPreview>(entityNode, p => p.Setup(function, kind, ownerCompositeId));

    private static bool AddPreview<T>(Node3D entityNode, Action<T> setup)
        where T : FunctionEntityPreview, new()
    {
        T preview = new T();
        entityNode.AddChild(preview);
        setup(preview);
        return true;
    }
}
