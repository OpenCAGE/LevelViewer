using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Applies composite-instance <c>mapping</c> parameters to ModelReference material choices.
/// Scope is non-recursive: only direct children of a composite instance node are affected.
/// </summary>
public static class ModelReferenceMaterialMapping
{
	public const string MappingParameterName = "mapping";
	public const string InstanceMappingMetaKey = "instance_material_mapping";
	/// <summary>Original ModelReference material write index before instance mapping is applied.</summary>
	public const string SourceMaterialWriteIndexMetaKey = "source_material_write_index";

	private static readonly ShortGuid MappingParameterId = ShortGuidUtils.Generate(MappingParameterName);
	private static readonly Dictionary<uint, List<AliasMappingSource>> AliasesByTargetEntityId = new Dictionary<uint, List<AliasMappingSource>>();
	private static readonly Dictionary<uint, Composite> OwningCompositeByEntityId = new Dictionary<uint, Composite>();
	private static readonly Dictionary<uint, Entity> EntityById = new Dictionary<uint, Entity>();
	private static readonly Dictionary<ulong, MaterialMappings.MaterialMapping> ResolvedMappingCache = new Dictionary<ulong, MaterialMappings.MaterialMapping>();
	private static bool _aliasMappingIndexBuilt;

	private readonly struct AliasMappingSource
	{
		public AliasMappingSource(Composite ownerComposite, AliasEntity alias, cResource mappingResource)
		{
			OwnerComposite = ownerComposite;
			Alias = alias;
			MappingResource = mappingResource;
		}

		public Composite OwnerComposite { get; }
		public AliasEntity Alias { get; }
		public cResource MappingResource { get; }
	}

	public static void PrepareForLevelPopulate(Commands commands)
	{
		ClearMappingCaches();
		EnsureAliasMappingIndex(commands);
	}

	public static void ClearMappingCaches()
	{
		AliasesByTargetEntityId.Clear();
		OwningCompositeByEntityId.Clear();
		EntityById.Clear();
		ResolvedMappingCache.Clear();
		_aliasMappingIndexBuilt = false;
	}

	/// <summary>Clears resolved mapping and alias-index caches after live mapping assignment changes.</summary>
	public static void InvalidateRuntimeMappingCaches(Commands commands)
	{
		ResolvedMappingCache.Clear();
		AliasesByTargetEntityId.Clear();
		_aliasMappingIndexBuilt = false;
		EnsureAliasMappingIndex(commands);
	}

	public static Entity TryGetEntityById(uint entityId)
	{
		if (entityId == 0)
			return null;

		EntityById.TryGetValue(entityId, out Entity entity);
		return entity;
	}

	public static bool IsCompositeInstanceEntity(FunctionEntity function, Commands commands)
	{
		if (function == null || commands == null || function.function.IsFunctionType)
			return false;

		return commands.GetComposite(function.function) != null;
	}

	public static bool IsModelReferenceEntity(FunctionEntity function)
	{
		return function != null
			&& function.function.IsFunctionType
			&& function.function.AsFunctionType == FunctionType.ModelReference;
	}

	/// <summary>Instance-specific cache key: model reference entity plus owning composite instance scope.</summary>
	public static ulong MakeModelRefRenderablesCacheKey(uint entityId, uint mappingScopeInstanceEntityId)
	{
		return ((ulong)entityId << 32) | mappingScopeInstanceEntityId;
	}

	public static cResource TryGetMappingParameter(Entity entity)
	{
		if (entity == null)
			return null;

		Parameter parameter = entity.GetParameter(MappingParameterId);
		if (parameter?.content is not cResource resource)
			return null;

		if (resource.shortGUID == ShortGuid.Invalid)
			return null;

		return resource;
	}

	public static MaterialMappings.MaterialMapping TryResolveMaterialMapping(Level level, cResource mappingResource)
	{
		if (level?.MaterialMappings?.Entries == null || mappingResource == null)
			return null;

		return level.MaterialMappings.Entries.FirstOrDefault(entry => entry.ID == mappingResource.shortGUID);
	}

	public static MaterialMappings.MaterialMapping TryResolveMaterialMapping(Level level, Entity scopeEntity)
	{
		return TryResolveMaterialMapping(level, scopeEntity, null, null, null, null);
	}

	public static MaterialMappings.MaterialMapping TryResolveMaterialMapping(
		Level level,
		Entity scopeEntity,
		Node3D scopeInstanceNode,
		Node3D modelRefNode,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		IReadOnlyList<Composite> compositesFromModelRefToScope)
	{
		if (scopeEntity == null || level == null)
			return null;

		Commands commands = level.Commands;
		EnsureAliasMappingIndex(commands);

		MaterialMappings.MaterialMapping mapping = TryResolveHierarchicalAliasMapping(
			level,
			scopeEntity,
			commands,
			compositesFromModelRefToScope);
		if (mapping != null)
			return mapping;

		mapping = TryResolveMappingFromInstanceNodeMeta(level, scopeInstanceNode);
		if (mapping != null)
			return mapping;

		cResource mappingResource = TryGetMappingParameter(scopeEntity);
		if (mappingResource == null)
			return null;

		return TryResolveMaterialMapping(level, mappingResource);
	}

	public static List<Composite> BuildCompositeChainFromModelRefToScope(
		Node3D modelRefNode,
		Node3D scopeInstanceNode,
		Commands commands)
	{
		_ = scopeInstanceNode;
		return BuildCompositeChainFromModelRefAncestors(modelRefNode, commands);
	}

	public static List<Composite> BuildCompositeChainFromModelRefAncestors(
		Node3D modelRefNode,
		Commands commands,
		Node stopAtRoot = null)
	{
		List<Composite> composites = new List<Composite>();
		if (modelRefNode == null || commands == null)
			return composites;

		Node current = modelRefNode;
		while (current != null && current != stopAtRoot)
		{
			if (current is Node3D node3D
				&& node3D.HasMeta(AlienScene.OwnerCompositeMetaKey))
			{
				Composite composite = commands.GetComposite(
					new ShortGuid(node3D.GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32()));
				if (composite != null && (composites.Count == 0 || composites[composites.Count - 1] != composite))
					composites.Add(composite);
			}

			current = current.GetParent();
		}

		return composites;
	}

	public static List<Composite> BuildCompositeChainFromSpawnPlan(
		IReadOnlyList<LevelViewerPopulateTree.Command> commands,
		int modelRefCommandIndex,
		ShortGuid scopeInstanceEntityId,
		Commands commandDb)
	{
		_ = scopeInstanceEntityId;
		return BuildCompositeChainFromSpawnPlanAncestors(commands, modelRefCommandIndex, commandDb);
	}

	public static List<Composite> BuildCompositeChainFromSpawnPlanAncestors(
		IReadOnlyList<LevelViewerPopulateTree.Command> commands,
		int modelRefCommandIndex,
		Commands commandDb)
	{
		List<Composite> composites = new List<Composite>();
		if (commands == null || commandDb == null || modelRefCommandIndex < 0 || modelRefCommandIndex >= commands.Count)
			return composites;

		int current = modelRefCommandIndex;
		while (current >= 0)
		{
			LevelViewerPopulateTree.Command command = commands[current];
			Composite composite = commandDb.GetComposite(command.CompositeId);
			if (composite == null || command.Entity == null)
				break;

			if (composites.Count == 0 || composites[composites.Count - 1] != composite)
				composites.Add(composite);

			current = command.ParentIndex;
		}

		return composites;
	}

	private static MaterialMappings.MaterialMapping TryResolveMappingFromInstanceNodeMeta(Level level, Node3D scopeInstanceNode)
	{
		if (level?.MaterialMappings?.Entries == null
			|| scopeInstanceNode == null
			|| !scopeInstanceNode.HasMeta(InstanceMappingMetaKey))
		{
			return null;
		}

		uint mappingId = scopeInstanceNode.GetMeta(InstanceMappingMetaKey).AsUInt32();
		if (mappingId == 0)
			return null;

		return level.MaterialMappings.Entries.FirstOrDefault(entry => entry.ID.AsUInt32 == mappingId);
	}

	/// <summary>
	/// Walks ancestor composites from the model reference upward (innermost first),
	/// then the scope owner's composite. The outermost matching alias wins, matching instancing.
	/// </summary>
	private static MaterialMappings.MaterialMapping TryResolveHierarchicalAliasMapping(
		Level level,
		Entity scopeInstanceEntity,
		Commands commands,
		IReadOnlyList<Composite> compositesFromModelRefToScope)
	{
		if (scopeInstanceEntity == null || commands == null)
			return null;

		ulong cacheKey = MakeMappingCacheKey(scopeInstanceEntity.shortGUID, compositesFromModelRefToScope);
		if (ResolvedMappingCache.TryGetValue(cacheKey, out MaterialMappings.MaterialMapping cached))
			return cached;

		List<Composite> searchOrder = BuildAliasSearchOrder(scopeInstanceEntity, commands, compositesFromModelRefToScope);
		MaterialMappings.MaterialMapping winningMapping = null;
		if (searchOrder.Count > 0
			&& AliasesByTargetEntityId.TryGetValue(
				scopeInstanceEntity.shortGUID.AsUInt32,
				out List<AliasMappingSource> sources))
		{
			for (int i = 0; i < searchOrder.Count; i++)
			{
				Composite composite = searchOrder[i];
				for (int s = 0; s < sources.Count; s++)
				{
					AliasMappingSource source = sources[s];
					if (source.OwnerComposite != composite)
						continue;

					MaterialMappings.MaterialMapping mapping = TryResolveMaterialMapping(level, source.MappingResource);
					if (mapping != null)
						winningMapping = mapping;
				}
			}
		}

		ResolvedMappingCache[cacheKey] = winningMapping;
		return winningMapping;
	}

	private static List<Composite> BuildAliasSearchOrder(
		Entity scopeInstanceEntity,
		Commands commands,
		IReadOnlyList<Composite> compositesFromModelRefToScope)
	{
		List<Composite> searchOrder = new List<Composite>();
		if (compositesFromModelRefToScope != null)
		{
			for (int i = 0; i < compositesFromModelRefToScope.Count; i++)
				AddCompositeIfMissing(searchOrder, compositesFromModelRefToScope[i]);
		}

		AddCompositeIfMissing(searchOrder, FindOwningComposite(scopeInstanceEntity, commands));
		return searchOrder;
	}

	private static ulong MakeMappingCacheKey(ShortGuid scopeInstanceEntityId, IReadOnlyList<Composite> composites)
	{
		ulong hash = scopeInstanceEntityId.AsUInt32;
		if (composites == null)
			return hash;

		for (int i = 0; i < composites.Count; i++)
		{
			Composite composite = composites[i];
			if (composite == null)
				continue;

			hash = unchecked(hash * 397 + composite.shortGUID.AsUInt32);
		}

		return hash;
	}

	private static void EnsureAliasMappingIndex(Commands commands)
	{
		if (_aliasMappingIndexBuilt || commands?.Entries == null)
			return;

		for (int i = 0; i < commands.Entries.Count; i++)
		{
			Composite composite = commands.Entries[i];
			List<Entity> entities = composite.GetEntities();
			for (int e = 0; e < entities.Count; e++)
			{
				Entity entity = entities[e];
				if (entity != null)
				{
					uint entityId = entity.shortGUID.AsUInt32;
					OwningCompositeByEntityId[entityId] = composite;
					EntityById[entityId] = entity;
				}
			}

			if (composite.aliases == null)
				continue;

			foreach (Entity aliasEntity in composite.aliases)
			{
				if (aliasEntity is not AliasEntity alias)
					continue;

				cResource mappingResource = TryGetMappingParameter(alias);
				if (mappingResource == null)
					continue;

				(Composite _, Entity targetEntity) = commands.Utils.GetResolvedTarget(
					commands.Utils.ResolveAlias(alias, composite));
				if (targetEntity == null)
					continue;

				uint targetId = targetEntity.shortGUID.AsUInt32;
				if (!AliasesByTargetEntityId.TryGetValue(targetId, out List<AliasMappingSource> sources))
				{
					sources = new List<AliasMappingSource>();
					AliasesByTargetEntityId[targetId] = sources;
				}

				sources.Add(new AliasMappingSource(composite, alias, mappingResource));
			}
		}

		_aliasMappingIndexBuilt = true;
	}

	private static void AddCompositeIfMissing(List<Composite> composites, Composite composite)
	{
		if (composite == null)
			return;

		for (int i = 0; i < composites.Count; i++)
		{
			if (composites[i] == composite)
				return;
		}

		composites.Add(composite);
	}

	public static MaterialMappings.MaterialMapping TryResolveMaterialMappingForInstanceNode(
		Level level,
		Node3D instanceNode,
		Node3D modelRefNode,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		Commands commands)
	{
		if (level == null || instanceNode == null || nodeEntities == null || commands == null)
			return null;

		if (!nodeEntities.TryGetValue(instanceNode, out Entity scopeEntity))
			return null;

		List<Composite> compositeChain = BuildCompositeChainFromModelRefAncestors(
			modelRefNode,
			commands);
		return TryResolveMaterialMapping(
			level,
			scopeEntity,
			instanceNode,
			modelRefNode,
			nodeEntities,
			compositeChain);
	}

	public static FunctionEntity FindMappingScopeCompositeInstance(
		Node3D entityNode,
		Node contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		Commands commands)
	{
		if (entityNode == null || contentRoot == null || nodeEntities == null || commands == null)
			return null;

		Node current = entityNode.GetParent();
		while (current != null && current != contentRoot)
		{
			if (current is Node3D node3D
				&& nodeEntities.TryGetValue(node3D, out Entity entity)
				&& entity is FunctionEntity function
				&& IsCompositeInstanceEntity(function, commands))
			{
				return function;
			}

			current = current.GetParent();
		}

		return null;
	}

	public static MaterialMappings.MaterialMapping TryResolveMappingForEntityNode(
		Level level,
		Node3D entityNode,
		Node contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		Commands commands)
	{
		if (entityNode == null)
			return null;

		FunctionEntity scopeInstance = FindMappingScopeCompositeInstance(
			entityNode,
			contentRoot,
			nodeEntities,
			commands);
		if (scopeInstance == null)
			return null;

		Node3D instanceNode = entityNode;
		Node walk = entityNode.GetParent();
		while (walk != null && walk != contentRoot)
		{
			if (walk is Node3D node3D
				&& nodeEntities.TryGetValue(node3D, out Entity entity)
				&& ReferenceEquals(entity, scopeInstance))
			{
				instanceNode = node3D;
				break;
			}

			walk = walk.GetParent();
		}

		return TryResolveMaterialMappingForInstanceNode(
			level,
			instanceNode,
			entityNode,
			nodeEntities,
			commands);
	}

	public static List<Tuple<int, int>> ApplyMapping(
		Level level,
		MaterialMappings.MaterialMapping mapping,
		IReadOnlyList<Tuple<int, int>> source)
	{
		List<Tuple<int, int>> result = new List<Tuple<int, int>>();
		if (source == null || source.Count == 0)
			return result;

		if (mapping == null || level?.Materials == null)
		{
			for (int i = 0; i < source.Count; i++)
				result.Add(source[i]);
			return result;
		}

		for (int i = 0; i < source.Count; i++)
		{
			Tuple<int, int> element = source[i];
			if (element == null)
				continue;

			int materialIndex = RemapMaterialWriteIndex(
				level,
				mapping,
				element.Item2,
				sourceMaterialWriteIndex: element.Item2);
			result.Add(new Tuple<int, int>(element.Item1, materialIndex));
		}

		return result;
	}

	public static int RemapMaterialWriteIndex(
		Level level,
		MaterialMappings.MaterialMapping mapping,
		int materialWriteIndex,
		int sourceMaterialWriteIndex = -1)
	{
		if (level?.Materials == null || mapping == null || materialWriteIndex < 0)
			return materialWriteIndex;

		if (sourceMaterialWriteIndex >= 0)
		{
			Materials.Material sourceMaterial = level.Materials.GetAtWriteIndex(sourceMaterialWriteIndex);
			if (sourceMaterial != null
				&& !string.IsNullOrEmpty(sourceMaterial.Name)
				&& TryFindMappingTarget(mapping, sourceMaterial.Name, out string sourceTargetName))
			{
				return ResolveMappedMaterialWriteIndex(level, sourceTargetName, materialWriteIndex);
			}
		}

		Materials.Material material = level.Materials.GetAtWriteIndex(materialWriteIndex);
		if (material == null || string.IsNullOrEmpty(material.Name))
			return materialWriteIndex;

		if (TryFindMappingTarget(mapping, material.Name, out string targetName))
			return ResolveMappedMaterialWriteIndex(level, targetName, materialWriteIndex);

		// Input may already be a previous mapping target; resolve via the original "from" name.
		MaterialMappings.MaterialMapping.Mapping reverseEntry = FindMappingEntryByTarget(mapping, material.Name);
		if (reverseEntry != null
			&& TryFindMappingTarget(mapping, reverseEntry.from, out string refreshedTargetName))
		{
			return ResolveMappedMaterialWriteIndex(level, refreshedTargetName, materialWriteIndex);
		}

		return materialWriteIndex;
	}

	private static int ResolveMappedMaterialWriteIndex(
		Level level,
		string targetName,
		int fallbackWriteIndex)
	{
		Materials.Material remapped = FindMaterialByName(level.Materials, targetName);
		if (remapped == null)
			return fallbackWriteIndex;

		int remappedIndex = level.Materials.GetWriteIndex(remapped);
		return remappedIndex < 0 ? fallbackWriteIndex : remappedIndex;
	}

	public static bool TryFindMappingTarget(
		MaterialMappings.MaterialMapping mapping,
		string materialName,
		out string targetName)
	{
		targetName = null;
		if (mapping?.Mappings == null || string.IsNullOrEmpty(materialName))
			return false;

		MaterialMappings.MaterialMapping.Mapping remap = FindMappingEntry(mapping, materialName);
		if (remap == null)
			return false;

		targetName = remap.to;
		return !string.IsNullOrEmpty(targetName);
	}

	private static MaterialMappings.MaterialMapping.Mapping FindMappingEntry(
		MaterialMappings.MaterialMapping mapping,
		string materialName)
	{
		MaterialMappings.MaterialMapping.Mapping remap = mapping.Mappings.FirstOrDefault(entry => entry.from == materialName);
		if (remap != null)
			return remap;

		string normalizedMaterialName = NormalizeMaterialNameForLookup(materialName);
		for (int i = 0; i < mapping.Mappings.Count; i++)
		{
			MaterialMappings.MaterialMapping.Mapping entry = mapping.Mappings[i];
			if (entry == null || string.IsNullOrEmpty(entry.from))
				continue;

			if (NormalizeMaterialNameForLookup(entry.from) == normalizedMaterialName)
				return entry;
		}

		return null;
	}

	private static MaterialMappings.MaterialMapping.Mapping FindMappingEntryByTarget(
		MaterialMappings.MaterialMapping mapping,
		string targetMaterialName)
	{
		if (mapping?.Mappings == null || string.IsNullOrEmpty(targetMaterialName))
			return null;

		MaterialMappings.MaterialMapping.Mapping remap = mapping.Mappings.FirstOrDefault(entry => entry.to == targetMaterialName);
		if (remap != null)
			return remap;

		string normalizedTargetName = NormalizeMaterialNameForLookup(targetMaterialName);
		for (int i = 0; i < mapping.Mappings.Count; i++)
		{
			MaterialMappings.MaterialMapping.Mapping entry = mapping.Mappings[i];
			if (entry == null || string.IsNullOrEmpty(entry.to))
				continue;

			if (NormalizeMaterialNameForLookup(entry.to) == normalizedTargetName)
				return entry;
		}

		return null;
	}

	public static string NormalizeMaterialNameForLookup(string materialName)
	{
		if (string.IsNullOrEmpty(materialName))
			return string.Empty;

		string normalized = StripTrailingVariantSuffix(materialName).ToUpperInvariant();
		if (!normalized.Contains("->"))
			normalized += "->" + normalized;

		return normalized;
	}

	/// <summary>
	/// Materials in the MTL can carry a trailing instance index (e.g. "foo->foo[000000]") that the
	/// material-mapping from/to names do not. Strip it so name comparisons line up.
	/// </summary>
	private static string StripTrailingVariantSuffix(string name)
	{
		if (string.IsNullOrEmpty(name) || name[name.Length - 1] != ']')
			return name;

		int open = name.LastIndexOf('[');
		if (open <= 0)
			return name;

		return name.Substring(0, open);
	}

	public static Materials.Material FindMaterialByName(Materials materials, string name)
	{
		if (materials?.Entries == null || string.IsNullOrEmpty(name))
			return null;

		Materials.Material exact = materials.Entries.FirstOrDefault(material => material.Name == name);
		if (exact != null)
			return exact;

		string normalizedName = NormalizeMaterialNameForLookup(name);
		return materials.Entries.FirstOrDefault(material =>
			material != null
			&& !string.IsNullOrEmpty(material.Name)
			&& NormalizeMaterialNameForLookup(material.Name) == normalizedName);
	}

	public static void ApplyAliasInstanceMappingMeta(AliasEntity alias, Node3D pointedNode)
	{
		if (alias == null || pointedNode == null || !GodotObject.IsInstanceValid(pointedNode))
			return;

		cResource mapping = TryGetMappingParameter(alias);
		if (mapping == null)
		{
			ClearAliasInstanceMappingMeta(pointedNode);
			return;
		}

		pointedNode.SetMeta(InstanceMappingMetaKey, mapping.shortGUID.AsUInt32);
	}

	public static void ClearAliasInstanceMappingMeta(Node3D pointedNode)
	{
		if (pointedNode == null || !GodotObject.IsInstanceValid(pointedNode))
			return;

		if (pointedNode.HasMeta(InstanceMappingMetaKey))
			pointedNode.RemoveMeta(InstanceMappingMetaKey);
	}

	private static Composite FindOwningComposite(Entity entity, Commands commands)
	{
		if (entity == null)
			return null;

		if (OwningCompositeByEntityId.TryGetValue(entity.shortGUID.AsUInt32, out Composite cached))
			return cached;

		if (commands?.Entries == null)
			return null;

		for (int i = 0; i < commands.Entries.Count; i++)
		{
			Composite composite = commands.Entries[i];
			if (composite.GetEntityByID(entity.shortGUID) == entity)
			{
				OwningCompositeByEntityId[entity.shortGUID.AsUInt32] = composite;
				return composite;
			}
		}

		return null;
	}
}
