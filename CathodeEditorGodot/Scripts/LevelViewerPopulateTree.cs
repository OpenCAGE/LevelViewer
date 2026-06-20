using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

/// <summary>
/// Builds a flat entity spawn plan by walking the composite tree in parallel (CPU-only).
/// Godot nodes are created on the main thread from the plan afterward.
/// </summary>
public static class LevelViewerPopulateTree
{
	public readonly struct Command
	{
		public Command(
			int parentIndex,
			ShortGuid compositeId,
			Entity entity,
			Vector3 position,
			Vector3 rotationDegrees,
			bool hasTransform,
			string nodeName,
			uint mappingScopeInstanceEntityId = 0)
		{
			ParentIndex = parentIndex;
			CompositeId = compositeId;
			Entity = entity;
			Position = position;
			RotationDegrees = rotationDegrees;
			HasTransform = hasTransform;
			NodeName = nodeName;
			MappingScopeInstanceEntityId = mappingScopeInstanceEntityId;
		}

		/// <summary>Index into the spawn list, or -1 to attach to the composite instance root.</summary>
		public int ParentIndex { get; }
		public ShortGuid CompositeId { get; }
		public Entity Entity { get; }
		public Vector3 Position { get; }
		public Vector3 RotationDegrees { get; }
		public bool HasTransform { get; }
		public string NodeName { get; }
		/// <summary>Composite instance entity that owns a <c>mapping</c> parameter for this spawn command.</summary>
		public uint MappingScopeInstanceEntityId { get; }
	}

	public static bool TryGetSpawnTransform(Entity entity, out Vector3 position, out Vector3 rotationDegrees)
	{
		position = Vector3.Zero;
		rotationDegrees = Vector3.Zero;
		if (entity == null)
			return false;

		Parameter positionParam = entity.GetParameter("position");
		if (positionParam?.content == null || positionParam.content.dataType != DataType.TRANSFORM)
			return false;

		cTransform transform = (cTransform)positionParam.content;
		position = CathodeCoordinates.PositionToGodot(transform.position);
		rotationDegrees = CathodeCoordinates.EulerDegreesToGodot(transform.rotation);
		return true;
	}

	public sealed class Plan
	{
		public List<Command> Commands { get; } = new List<Command>();
		public List<FunctionEntity> ModelReferences { get; } = new List<FunctionEntity>();
		public double CollectCpuMs { get; set; }
	}

	public static Plan Collect(Composite root, LevelContent content, bool deferAliasProxy, bool includeVariables = true)
	{
		Plan plan = new Plan();
		if (root == null || content?.Level == null)
			return plan;

		Stopwatch stopwatch = Stopwatch.StartNew();
		List<Command> commands = CollectCompositeSubtree(
			root,
			content,
			deferAliasProxy,
			includeVariables,
			plan.ModelReferences,
			mappingScopeInstanceEntityId: 0);
		plan.Commands.AddRange(commands);
		stopwatch.Stop();
		plan.CollectCpuMs = stopwatch.Elapsed.TotalMilliseconds;
		return plan;
	}

	private static List<Command> CollectCompositeSubtree(
		Composite composite,
		LevelContent content,
		bool deferAliasProxy,
		bool includeVariables,
		List<FunctionEntity> modelReferences,
		uint mappingScopeInstanceEntityId)
	{
		Level level = content.Level;
		List<Command> commands = new List<Command>();
		List<(int ParentLocalIndex, Composite Nested, uint NestedMappingScopeInstanceEntityId)> nestedBranches =
			new List<(int, Composite, uint)>();

		CollectEntityList(
			composite.functions,
			composite,
			level,
			commands,
			nestedBranches,
			modelReferences,
			mappingScopeInstanceEntityId);
		if (includeVariables)
		{
			CollectEntityList(
				composite.variables,
				composite,
				level,
				commands,
				nestedBranches,
				modelReferences,
				mappingScopeInstanceEntityId);
		}

		if (!deferAliasProxy)
		{
			CollectEntityList(
				composite.aliases,
				composite,
				level,
				commands,
				nestedBranches,
				modelReferences,
				mappingScopeInstanceEntityId);
			CollectEntityList(
				composite.proxies,
				composite,
				level,
				commands,
				nestedBranches,
				modelReferences,
				mappingScopeInstanceEntityId);
		}

		if (nestedBranches.Count == 0)
			return commands;

		if (nestedBranches.Count == 1)
		{
			(int parentLocalIndex, Composite nested, uint nestedScopeId) = nestedBranches[0];
			List<Command> nestedCommands = CollectCompositeSubtree(
				nested,
				content,
				deferAliasProxy,
				includeVariables,
				modelReferences,
				nestedScopeId);
			MergeSubtree(commands, nestedCommands, parentLocalIndex);
			return commands;
		}

		(int ParentLocalIndex, Composite Nested, uint NestedMappingScopeInstanceEntityId)[] branches = nestedBranches.ToArray();
		List<Command>[] nestedPlans = new List<Command>[branches.Length];
		Parallel.For(0, branches.Length, i =>
		{
			nestedPlans[i] = CollectCompositeSubtree(
				branches[i].Nested,
				content,
				deferAliasProxy,
				includeVariables,
				modelReferences,
				branches[i].NestedMappingScopeInstanceEntityId);
		});

		for (int i = 0; i < branches.Length; i++)
			MergeSubtree(commands, nestedPlans[i], branches[i].ParentLocalIndex);

		return commands;
	}

	private static void CollectEntityList(
		IEnumerable<Entity> entities,
		Composite composite,
		Level level,
		List<Command> commands,
		List<(int ParentLocalIndex, Composite Nested, uint NestedMappingScopeInstanceEntityId)> nestedBranches,
		List<FunctionEntity> modelReferences,
		uint mappingScopeInstanceEntityId)
	{
		if (entities == null)
			return;

		foreach (Entity entity in entities)
		{
			int localIndex = commands.Count;
			TryGetSpawnTransform(entity, out Vector3 position, out Vector3 rotationDegrees);
			bool hasTransform = TryGetSpawnTransform(entity, out position, out rotationDegrees);
			commands.Add(new Command(
				-1,
				composite.shortGUID,
				entity,
				position,
				rotationDegrees,
				hasTransform,
				entity.shortGUID.AsUInt32.ToString(),
				mappingScopeInstanceEntityId));

			if (entity is not FunctionEntity function)
				continue;

			if (function.function.IsFunctionType)
			{
				if (function.function.AsFunctionType == FunctionType.ModelReference)
					modelReferences.Add(function);
				continue;
			}

			Composite nested = level.Commands.GetComposite(function.function);
			if (nested != null)
				nestedBranches.Add((localIndex, nested, function.shortGUID.AsUInt32));
		}
	}

	private static void MergeSubtree(List<Command> target, List<Command> subtree, int attachParentIndex)
	{
		if (subtree == null || subtree.Count == 0)
			return;

		int offset = target.Count;
		for (int i = 0; i < subtree.Count; i++)
		{
			Command cmd = subtree[i];
			int parentIndex = cmd.ParentIndex < 0 ? attachParentIndex : cmd.ParentIndex + offset;
			target.Add(new Command(
				parentIndex,
				cmd.CompositeId,
				cmd.Entity,
				cmd.Position,
				cmd.RotationDegrees,
				cmd.HasTransform,
				cmd.NodeName,
				cmd.MappingScopeInstanceEntityId));
		}
	}
}
