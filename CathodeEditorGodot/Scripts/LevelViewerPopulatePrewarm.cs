using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using static CATHODE.Models;

/// <summary>
/// Parallel CPU conversion for composite population. Godot resource creation happens afterward on the main thread.
/// </summary>
public static class LevelViewerPopulatePrewarm
{
	public sealed class Plan
	{
		public HashSet<int> MeshWriteIndices { get; } = new HashSet<int>();
		public HashSet<Textures.TEX4> Textures { get; } = new HashSet<Textures.TEX4>();
		public Dictionary<Textures.TEX4, TexturePtr.Source> TextureLocations { get; } = new Dictionary<Textures.TEX4, TexturePtr.Source>();
		public HashSet<Materials.Material> Materials { get; } = new HashSet<Materials.Material>();
	}

	public sealed class Result
	{
		public Dictionary<int, ParsedMeshSurface> Meshes { get; } = new Dictionary<int, ParsedMeshSurface>();
		public Dictionary<Textures.TEX4, BakedTextureCpu> Textures { get; } = new Dictionary<Textures.TEX4, BakedTextureCpu>();
		public double CpuElapsedMs { get; set; }
	}

	public static Plan CollectPlan(Composite root, LevelContent content)
	{
		Plan plan = new Plan();
		if (root == null || content?.Level == null)
			return plan;

		CollectComposite(root, content, plan);
		return plan;
	}

	public sealed class ModelReferenceCache
	{
		public Plan PrewarmPlan { get; } = new Plan();
		public Dictionary<ulong, List<Tuple<int, int>>> RenderablesByInstanceKey { get; } = new Dictionary<ulong, List<Tuple<int, int>>>();
		public double BuildCpuMs { get; set; }
	}

	/// <summary>
	/// Resolves renderables per spawn command (entity + composite instance scope), not per entity definition.
	/// </summary>
	public static ModelReferenceCache BuildModelReferenceCache(
		IReadOnlyList<FunctionEntity> modelReferences,
		LevelContent content,
		LevelViewerPopulateTree.Plan spawnPlan = null)
	{
		ModelReferenceCache cache = new ModelReferenceCache();
		if (content?.Level == null)
			return cache;

		if (spawnPlan?.Commands == null || spawnPlan.Commands.Count == 0)
		{
			if (modelReferences == null || modelReferences.Count == 0)
				return cache;

			BuildModelReferenceCacheWithoutSpawnPlan(modelReferences, content, cache);
			return cache;
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		Level level = content.Level;
		Commands commands = level.Commands;
		Dictionary<uint, FunctionEntity> modelRefEntities = new Dictionary<uint, FunctionEntity>();

		for (int commandIndex = 0; commandIndex < spawnPlan.Commands.Count; commandIndex++)
		{
			LevelViewerPopulateTree.Command command = spawnPlan.Commands[commandIndex];
			if (command.Entity is not FunctionEntity function)
				continue;

			if (!ModelReferenceMaterialMapping.IsModelReferenceEntity(function))
				continue;

			modelRefEntities[function.shortGUID.AsUInt32] = function;
		}

		if (modelRefEntities.Count == 0)
			return cache;

		ConcurrentDictionary<uint, List<Tuple<int, int>>> baseRenderablesByEntityId =
			new ConcurrentDictionary<uint, List<Tuple<int, int>>>();
		ParallelOptions options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
		};

		Parallel.ForEach(modelRefEntities, options, entry =>
		{
			baseRenderablesByEntityId[entry.Key] =
				ModelReferencePreview.GetRenderableIndexes(content, entry.Value);
		});

		for (int commandIndex = 0; commandIndex < spawnPlan.Commands.Count; commandIndex++)
		{
			LevelViewerPopulateTree.Command command = spawnPlan.Commands[commandIndex];
			if (command.Entity is not FunctionEntity function)
				continue;

			if (!ModelReferenceMaterialMapping.IsModelReferenceEntity(function))
				continue;

			uint entityId = function.shortGUID.AsUInt32;
			if (!baseRenderablesByEntityId.TryGetValue(entityId, out List<Tuple<int, int>> baseRenderables))
				continue;

			Entity scopeEntity = command.MappingScopeInstanceEntityId != 0
				? ModelReferenceMaterialMapping.TryGetEntityById(command.MappingScopeInstanceEntityId)
				: null;
			List<Composite> compositeChain = ModelReferenceMaterialMapping.BuildCompositeChainFromSpawnPlanAncestors(
				spawnPlan.Commands,
				commandIndex,
				commands);
			MaterialMappings.MaterialMapping mapping = ModelReferenceMaterialMapping.TryResolveMaterialMapping(
				level,
				scopeEntity,
				null,
				null,
				null,
				compositeChain);
			List<Tuple<int, int>> renderables = ModelReferenceMaterialMapping.ApplyMapping(
				level,
				mapping,
				baseRenderables);

			ulong cacheKey = ModelReferenceMaterialMapping.MakeModelRefRenderablesCacheKey(
				entityId,
				command.MappingScopeInstanceEntityId);
			cache.RenderablesByInstanceKey[cacheKey] = renderables;
			CollectModelReferenceRenderables(renderables, content, cache.PrewarmPlan);
		}

		stopwatch.Stop();
		cache.BuildCpuMs = stopwatch.Elapsed.TotalMilliseconds;
		return cache;
	}

	private static void BuildModelReferenceCacheWithoutSpawnPlan(
		IReadOnlyList<FunctionEntity> modelReferences,
		LevelContent content,
		ModelReferenceCache cache)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		ConcurrentDictionary<uint, List<Tuple<int, int>>> baseRenderablesByEntityId =
			new ConcurrentDictionary<uint, List<Tuple<int, int>>>();
		ParallelOptions options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
		};

		Parallel.ForEach(modelReferences, options, function =>
		{
			if (function == null)
				return;

			baseRenderablesByEntityId[function.shortGUID.AsUInt32] =
				ModelReferencePreview.GetRenderableIndexes(content, function);
		});

		foreach (KeyValuePair<uint, List<Tuple<int, int>>> entry in baseRenderablesByEntityId)
		{
			ulong cacheKey = ModelReferenceMaterialMapping.MakeModelRefRenderablesCacheKey(entry.Key, 0);
			cache.RenderablesByInstanceKey[cacheKey] = entry.Value;
			CollectModelReferenceRenderables(entry.Value, content, cache.PrewarmPlan);
		}

		stopwatch.Stop();
		cache.BuildCpuMs = stopwatch.Elapsed.TotalMilliseconds;
	}

	public static void CollectModelReference(FunctionEntity entity, LevelContent content, Plan plan)
	{
		List<Tuple<int, int>> renderables = ModelReferencePreview.GetRenderableIndexes(content, entity);
		CollectModelReferenceRenderables(renderables, content, plan);
	}

	private static void CollectModelReferenceRenderables(
		List<Tuple<int, int>> renderables,
		LevelContent content,
		Plan plan)
	{
		if (renderables == null || renderables.Count == 0 || content?.Level == null)
			return;

		Level level = content.Level;
		for (int i = 0; i < renderables.Count; i++)
		{
			int modelIndex = renderables[i].Item1;
			int materialIndex = renderables[i].Item2;
			if (modelIndex >= 0)
				plan.MeshWriteIndices.Add(modelIndex);

			Materials.Material material = level.Materials?.GetAtWriteIndex(materialIndex);
			if (material != null)
				plan.Materials.Add(material);

			CollectMaterialTextures(material, plan);
		}
	}

	/// <summary>CPU-only conversion on the thread pool; blocks until complete.</summary>
	public static Result Execute(Plan plan, Level level)
	{
		if (plan == null || level == null)
			return new Result();

		return Task.Run(() => ExecuteCpuParallel(plan, level)).GetAwaiter().GetResult();
	}

	private static Result ExecuteCpuParallel(Plan plan, Level level)
	{
		Result result = new Result();
		Stopwatch stopwatch = Stopwatch.StartNew();
		ParallelOptions options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
		};

		ConcurrentDictionary<int, ParsedMeshSurface> meshes = new ConcurrentDictionary<int, ParsedMeshSurface>();
		ConcurrentDictionary<Textures.TEX4, BakedTextureCpu> textures = new ConcurrentDictionary<Textures.TEX4, BakedTextureCpu>();

		Parallel.Invoke(
			() => Parallel.ForEach(plan.MeshWriteIndices, options, writeIndex =>
			{
				CS2.Component.LOD.Submesh submesh = level.Models?.GetAtWriteIndex(writeIndex);
				if (submesh?.Data == null || submesh.Data.Length == 0)
					return;

				if (ParsedMeshSurface.TryParse(submesh, out ParsedMeshSurface surface))
					meshes[writeIndex] = surface;
			}),
			() => Parallel.ForEach(plan.Textures, options, tex =>
			{
				if (tex == null || tex.StateFlags.HasFlag(Textures.TextureStateFlag.CUBE))
					return;

				if (AlienSceneTextures.TryBakeCpu(tex, out BakedTextureCpu baked))
					textures[tex] = baked;
			}));

		foreach (KeyValuePair<int, ParsedMeshSurface> entry in meshes)
			result.Meshes[entry.Key] = entry.Value;

		foreach (KeyValuePair<Textures.TEX4, BakedTextureCpu> entry in textures)
			result.Textures[entry.Key] = entry.Value;

		stopwatch.Stop();
		result.CpuElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
		return result;
	}

	private static void CollectComposite(Composite composite, LevelContent content, Plan plan)
	{
		if (composite?.functions == null || content?.Level == null)
			return;

		Level level = content.Level;
		foreach (Entity entity in composite.functions)
		{
			if (entity is not FunctionEntity function)
				continue;

			if (!function.function.IsFunctionType)
			{
				Composite nested = level.Commands.GetComposite(function.function);
				CollectComposite(nested, content, plan);
				continue;
			}

			if (function.function.AsFunctionType != FunctionType.ModelReference)
				continue;

			CollectModelReference(function, content, plan);
		}
	}

	private static void CollectMaterialTextures(Materials.Material material, Plan plan)
	{
		if (material?.TextureReferences == null)
			return;

		foreach (TexturePtr texturePtr in material.TextureReferences)
		{
			if (texturePtr?.Texture == null || texturePtr.Location == TexturePtr.Source.NONE)
				continue;

			plan.Textures.Add(texturePtr.Texture);
			plan.TextureLocations[texturePtr.Texture] = texturePtr.Location;
		}
	}
}
