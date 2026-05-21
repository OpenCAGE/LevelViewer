using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders ModelReference function entities from resource parameters and live remapped resources.
/// </summary>
public class ModelReferencePreview : FunctionEntityPreview
{
    private const string ResourceParameter = "resource";

    private AlienScene _scene;

    public void Setup(AlienScene scene, FunctionEntity entity, uint ownerCompositeId = 0)
    {
        _scene = scene;
        base.Setup(entity, ownerCompositeId);
    }

    protected override GameObject GetVisibilityRoot() => null;

    public override void RefreshVisibility()
    {
        // Model references are not gated by hide-nested; avoid respawning meshes.
    }

    public override void Refresh()
    {
        if (_scene == null || Entity == null)
            return;

        Transform renderTarget = GetRenderTarget();
        _scene.ClearRenderableChildren(renderTarget);

        foreach (Tuple<int, int> renderable in GetRenderableIndexes())
        {
            _scene.SpawnRenderable(
                renderTarget.gameObject,
                _scene.Content.Level.Models.GetAtWriteIndex(renderable.Item1),
                _scene.Content.Level.Materials.GetAtWriteIndex(renderable.Item2));
        }
    }

    /// <summary>
    /// Resolve the ModelReference entity that owns resource/remap data (handles alias/proxy indirection).
    /// </summary>
    public static FunctionEntity ResolveModelReferenceEntity(Entity entity, Composite composite, Commands commands)
    {
        if (entity is FunctionEntity function && function.function.AsFunctionType == FunctionType.ModelReference)
            return function;

        if (entity.variant == EntityVariant.ALIAS)
        {
            (Composite resolvedComposite, Entity resolvedEntity) = commands.Utils.GetResolvedTarget(
                commands.Utils.ResolveAlias((AliasEntity)entity, composite));
            if (resolvedEntity is FunctionEntity resolvedFunction && resolvedFunction.function.AsFunctionType == FunctionType.ModelReference)
                return resolvedFunction;
        }

        if (entity.variant == EntityVariant.PROXY)
        {
            (Composite resolvedComposite, Entity resolvedEntity) = commands.Utils.GetResolvedTarget(
                commands.Utils.ResolveProxy((ProxyEntity)entity));
            if (resolvedEntity is FunctionEntity resolvedFunction && resolvedFunction.function.AsFunctionType == FunctionType.ModelReference)
                return resolvedFunction;
        }

        return null;
    }

    private Transform GetRenderTarget()
    {
        EntityOverride entityOverride = GetComponent<EntityOverride>();
        if (entityOverride != null && entityOverride.PointedEntity != null)
            return entityOverride.PointedEntity.transform;
        return transform;
    }

    private List<Tuple<int, int>> GetRenderableIndexes()
    {
        if (_scene.Content.RemappedResources.TryGetValue(Entity, out List<Tuple<int, int>> remapped))
            return remapped;

        Parameter resourceParam = Entity.GetParameter(ResourceParameter);
        if (resourceParam?.content == null || resourceParam.content.dataType != DataType.RESOURCE)
            return new List<Tuple<int, int>>();

        cResource resource = (cResource)resourceParam.content;
        ResourceReference renderable = resource.GetResource(ResourceType.RENDERABLE_INSTANCE);
        if (renderable == null || renderable.RenderableInstance == null)
            return new List<Tuple<int, int>>();

        List<Tuple<int, int>> indexes = new List<Tuple<int, int>>();
        Level level = _scene.Content.Level;
        for (int i = 0; i < renderable.RenderableInstance.Count; i++)
        {
            int modelIndex = level.Models.GetWriteIndex(renderable.RenderableInstance[i].Model);
            int materialIndex = level.Materials.GetWriteIndex(renderable.RenderableInstance[i].Material);
            if (modelIndex < 0 || materialIndex < 0)
                continue;
            indexes.Add(new Tuple<int, int>(modelIndex, materialIndex));
        }
        return indexes;
    }
}
