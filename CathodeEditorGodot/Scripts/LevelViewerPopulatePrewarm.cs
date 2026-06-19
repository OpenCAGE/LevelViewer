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

	/// <summary>Derives prewarm work from the flat spawn plan (avoids a second composite tree walk).</summary>
	public static Plan CollectPlanFromSpawnCommands(LevelViewerPopulateTree.Plan spawnPlan, LevelContent content)
	{
		Plan plan = new Plan();
		if (spawnPlan?.Commands == null || content?.Level == null)
			return plan;

		for (int i = 0; i < spawnPlan.Commands.Count; i++)
		{
			LevelViewerPopulateTree.Command command = spawnPlan.Commands[i];
			if (command.Entity is not FunctionEntity function)
				continue;

			if (!function.function.IsFunctionType)
				continue;

			if (function.function.AsFunctionType != FunctionType.ModelReference)
				continue;

			CollectModelReference(function, content, plan);
		}

		return plan;
	}

	public sealed class ModelReferenceCache
	{
		public Plan PrewarmPlan { get; } = new Plan();
		public Dictionary<uint, List<Tuple<int, int>>> RenderablesByEntityId { get; } = new Dictionary<uint, List<Tuple<int, int>>>();
		public double BuildCpuMs { get; set; }
	}

	/// <summary>One GetRenderableIndexes pass per model reference, parallel on the thread pool.</summary>
	public static ModelReferenceCache BuildModelReferenceCache(
		IReadOnlyList<FunctionEntity> modelReferences,
		LevelContent content)
	{
		ModelReferenceCache cache = new ModelReferenceCache();
		if (modelReferences == null || modelReferences.Count == 0 || content?.Level == null)
			return cache;

		Stopwatch stopwatch = Stopwatch.StartNew();
		ConcurrentDictionary<uint, List<Tuple<int, int>>> renderablesById = new ConcurrentDictionary<uint, List<Tuple<int, int>>>();
		ParallelOptions options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
		};

		Parallel.ForEach(modelReferences, options, function =>
		{
			if (function == null)
				return;

			renderablesById[function.shortGUID.AsUInt32] =
				ModelReferencePreview.GetRenderableIndexes(content, function);
		});

		foreach (KeyValuePair<uint, List<Tuple<int, int>>> entry in renderablesById)
		{
			cache.RenderablesByEntityId[entry.Key] = entry.Value;
			CollectModelReferenceRenderables(entry.Value, content, cache.PrewarmPlan);
		}

		stopwatch.Stop();
		cache.BuildCpuMs = stopwatch.Elapsed.TotalMilliseconds;
		return cache;
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
