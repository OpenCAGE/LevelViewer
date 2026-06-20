using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Renders ModelReference function entities from resource parameters and live remapped resources.
/// </summary>
public partial class ModelReferencePreview : FunctionEntityPreview
{
    private const string ResourceParameter = "resource";

    private AlienScene _scene;
    private uint _mappingScopeInstanceEntityId;

    public void Setup(AlienScene scene, FunctionEntity entity, uint ownerCompositeId = 0, uint mappingScopeInstanceEntityId = 0)
    {
        _scene = scene;
        _mappingScopeInstanceEntityId = mappingScopeInstanceEntityId;
        base.Setup(entity, ownerCompositeId);
    }

    public void SetMappingScopeInstanceEntityId(uint mappingScopeInstanceEntityId)
    {
        _mappingScopeInstanceEntityId = mappingScopeInstanceEntityId;
    }

    protected override Node3D GetVisibilityRoot() => null;

    public override void CleanupPreviewVisuals()
    {
        if (_scene == null)
            return;

        Node3D renderTarget = GetRenderTarget();
        if (renderTarget != null)
            _scene.ClearRenderableChildren(renderTarget);
    }

    public override void RefreshVisibility()
    {
        // Model references are not gated by hide-nested; avoid respawning meshes.
    }

    public override void RegisterPickablesWithOwner()
    {
        Node3D owner = GetParent() as Node3D;
        if (owner == null || !GodotObject.IsInstanceValid(owner))
            return;

        LevelViewerPick.RegisterPickableSubtree(owner);
    }

	public override void Refresh()
	{
		if (_scene == null || Entity == null)
			return;

		Node3D renderTarget = GetRenderTarget();
		_scene.ClearRenderableChildren(renderTarget);
		SpawnAllRenderables(renderTarget);
		RegisterPickablesWithOwner();
	}

	/// <summary>First bulk spawn after deferred setup — render target has no mesh children yet.</summary>
	internal void SpawnRenderablesForBulkLoad()
	{
		if (_scene == null || Entity == null)
			return;

		Node3D renderTarget = GetRenderTarget();
		if (renderTarget == null)
			return;

		SpawnAllRenderables(renderTarget);
	}

	internal void SpawnAllRenderables(Node3D renderTarget)
	{
		if (_scene == null || renderTarget == null)
			return;

		List<Tuple<int, int>> sourceRenderables = GetSourceRenderableIndexes();
		if (sourceRenderables.Count == 0)
			return;

		MaterialMappings.MaterialMapping mapping = TryResolveMappingForSpawnScope();
		List<Tuple<int, int>> renderables = mapping != null
			? ModelReferenceMaterialMapping.ApplyMapping(_scene.Content.Level, mapping, sourceRenderables)
			: sourceRenderables;

		int count = Math.Min(sourceRenderables.Count, renderables.Count);
		for (int i = 0; i < count; i++)
		{
			Tuple<int, int> source = sourceRenderables[i];
			Tuple<int, int> renderable = renderables[i];
			if (source == null || renderable == null)
				continue;

			SpawnSingleRenderable(
				renderTarget,
				renderable.Item1,
				renderable.Item2,
				source.Item2);
		}
	}

	internal void SpawnSingleRenderable(
		Node3D renderTarget,
		int modelWriteIndex,
		int materialWriteIndex,
		int sourceMaterialWriteIndex = -1)
	{
		if (_scene == null || renderTarget == null || modelWriteIndex < 0 || materialWriteIndex < 0)
			return;

		_scene.SpawnRenderable(
			renderTarget,
			_scene.Content.Level.Models.GetAtWriteIndex(modelWriteIndex),
			_scene.Content.Level.Materials.GetAtWriteIndex(materialWriteIndex),
			sourceMaterialWriteIndex >= 0 ? sourceMaterialWriteIndex : materialWriteIndex);
	}

	internal Node3D GetPopulateRenderTarget() => GetRenderTarget();

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

    private Node3D GetRenderTarget()
    {
        EntityOverride entityOverride = EntityNodeUtil.GetEntityOverride(GetParent());
        if (entityOverride != null && entityOverride.PointedEntity != null)
            return entityOverride.PointedEntity;
        return GetParent() as Node3D ?? this;
    }

    private List<Tuple<int, int>> GetRenderableIndexes()
    {
        return GetResolvedRenderableIndexes();
    }

    internal uint MappingScopeInstanceEntityId => _mappingScopeInstanceEntityId;

    internal List<Tuple<int, int>> GetResolvedRenderableIndexes()
    {
        List<Tuple<int, int>> indexes = GetSourceRenderableIndexes();
        if (indexes.Count == 0 || _scene?.Content?.Level == null)
            return indexes;

        if (_scene.IsBulkPopulating
            && _scene.TryGetModelRefRenderables(
                Entity.shortGUID.AsUInt32,
                _mappingScopeInstanceEntityId,
                out List<Tuple<int, int>> cached))
        {
            return cached;
        }

        MaterialMappings.MaterialMapping mapping = TryResolveMappingForSpawnScope();
        if (mapping == null)
            return indexes;

        return ModelReferenceMaterialMapping.ApplyMapping(
			_scene.Content.Level,
			mapping,
			indexes);
    }

    internal List<Tuple<int, int>> GetSourceRenderableIndexes()
    {
        return GetRenderableIndexes(_scene.Content, Entity);
    }

    private List<Tuple<int, int>> GetBaseRenderableIndexes()
    {
        return GetSourceRenderableIndexes();
    }

    private MaterialMappings.MaterialMapping TryResolveMappingForSpawnScope()
    {
        if (_scene?.Content?.Level == null)
            return null;

        Commands commands = _scene.Content.Level.Commands;
        Node3D modelRefNode = GetParent() as Node3D;

        if (_mappingScopeInstanceEntityId != 0)
        {
            Entity scopeEntity = _scene.FindEntityById(_mappingScopeInstanceEntityId);
            Node3D scopeNode = FindScopeInstanceNode(modelRefNode);
            List<Composite> compositeChain = ModelReferenceMaterialMapping.BuildCompositeChainFromModelRefAncestors(
                modelRefNode,
                commands,
                _scene.ParentNode);
            MaterialMappings.MaterialMapping mapping = ModelReferenceMaterialMapping.TryResolveMaterialMapping(
                _scene.Content.Level,
                scopeEntity,
                scopeNode,
                modelRefNode,
                _scene.NodeEntities,
                compositeChain);
            if (mapping != null)
                return mapping;
        }

        if (modelRefNode == null)
            return null;

        return ModelReferenceMaterialMapping.TryResolveMappingForEntityNode(
            _scene.Content.Level,
            modelRefNode,
            _scene.ParentNode,
            _scene.NodeEntities,
            commands);
    }

    private Node3D FindScopeInstanceNode(Node3D modelRefNode)
    {
        if (modelRefNode == null || _mappingScopeInstanceEntityId == 0 || _scene?.ParentNode == null)
            return null;

        ShortGuid scopeId = new ShortGuid(_mappingScopeInstanceEntityId);
        Node current = modelRefNode.GetParent();
        while (current != null && current != _scene.ParentNode)
        {
            if (current is Node3D node3D
                && _scene.NodeEntities.TryGetValue(node3D, out Entity entity)
                && entity.shortGUID == scopeId)
            {
                return node3D;
            }

            current = current.GetParent();
        }

        return null;
    }

    public static List<Tuple<int, int>> GetRenderableIndexes(LevelContent content, Entity entity)
    {
        if (content?.Level == null || entity == null)
            return new List<Tuple<int, int>>();

        if (content.RemappedResources.TryGetValue(entity, out List<Tuple<int, int>> remapped))
            return remapped;

        Parameter resourceParam = entity.GetParameter(ResourceParameter);
        if (resourceParam?.content == null || resourceParam.content.dataType != DataType.RESOURCE)
            return new List<Tuple<int, int>>();

        cResource resource = (cResource)resourceParam.content;
        ResourceReference renderable = resource.GetResource(ResourceType.RENDERABLE_INSTANCE);
        if (renderable == null || renderable.RenderableInstance == null)
            return new List<Tuple<int, int>>();

        List<Tuple<int, int>> indexes = new List<Tuple<int, int>>();
        Level level = content.Level;
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
