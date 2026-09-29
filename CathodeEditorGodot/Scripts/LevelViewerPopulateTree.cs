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
			uint mappingScopeInstanceEntityId = 0)
		{
			ParentIndex = parentIndex;
			CompositeId = compositeId;
			Entity = entity;
			Position = position;
			RotationDegrees = rotationDegrees;
			HasTransform = hasTransform;
			MappingScopeInstanceEntityId = mappingScopeInstanceEntityId;
		}

		/// <summary>Index into the spawn list, or -1 to attach to the composite instance root.</summary>
		public int ParentIndex { get; }
		public ShortGuid CompositeId { get; }
		public Entity Entity { get; }
		public Vector3 Position { get; }
		public Vector3 RotationDegrees { get; }
		public bool HasTransform { get; }
		/// <summary>Composite instance entity that owns a <c>mapping</c> parameter for this spawn command.</summary>
		public uint MappingScopeInstanceEntityId { get; }
	}

	//Asked for every entity of the plan from several threads at once: by name, each was a lookup under ShortGuidUtils' one lock
	private static readonly ShortGuid PositionParameter = ShortGuidUtils.Generate("position");

	public static bool TryGetSpawnTransform(Entity entity, out Vector3 position, out Vector3 rotationDegrees)
	{
		position = Vector3.Zero;
		rotationDegrees = Vector3.Zero;
		if (entity == null)
			return false;

		Parameter positionParam = entity.GetParameter(PositionParameter);
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
		//Commands.GetComposite scans every composite, and the walk asks it for every instance entity it meets
		Dictionary<ShortGuid, Composite> compositesById = new Dictionary<ShortGuid, Composite>(content.Level.Commands.Entries.Count);
		foreach (Composite entry in content.Level.Commands.Entries)
			if (entry != null && !compositesById.ContainsKey(entry.shortGUID))
				compositesById[entry.shortGUID] = entry;
		Segment segment = CollectSegment(
			root,
			compositesById,
			deferAliasProxy,
			includeVariables,
			plan.ModelReferences,
			mappingScopeInstanceEntityId: 0);
		plan.Commands.Capacity = segment.Total;
		Flatten(segment, -1, plan.Commands);
		stopwatch.Stop();
		plan.CollectCpuMs = stopwatch.Elapsed.TotalMilliseconds;
		return plan;
	}

	/* A composite's own entities, and the composites it instances, each attached to one of those entities. The plan used to
	   be built by copying every child's finished list into its parent's, level by level: each command was copied once per
	   level of nesting, with list growth on top - over a gigabyte of garbage for one populate of TECH_Hub (1.1M commands),
	   a quarter of everything the viewer allocated and a steady stream of collections. Now the tree is kept as it is
	   collected and written out once, in the same order with the same parent indexes. */
	private sealed class Segment
	{
		public List<Command> Local;
		public List<(int ParentLocalIndex, Segment Child)> Children;
		public int Total;
	}

	private static void Flatten(Segment segment, int attachParentIndex, List<Command> output)
	{
		int start = output.Count;
		for (int i = 0; i < segment.Local.Count; i++)
		{
			Command cmd = segment.Local[i];
			output.Add(new Command(
				attachParentIndex,
				cmd.CompositeId,
				cmd.Entity,
				cmd.Position,
				cmd.RotationDegrees,
				cmd.HasTransform,
				cmd.MappingScopeInstanceEntityId));
		}

		if (segment.Children == null)
			return;
		for (int i = 0; i < segment.Children.Count; i++)
			Flatten(segment.Children[i].Child, start + segment.Children[i].ParentLocalIndex, output);
	}

	private static Segment CollectSegment(
		Composite composite,
		Dictionary<ShortGuid, Composite> compositesById,
		bool deferAliasProxy,
		bool includeVariables,
		List<FunctionEntity> modelReferences,
		uint mappingScopeInstanceEntityId)
	{
		List<Command> commands = new List<Command>();
		List<(int ParentLocalIndex, Composite Nested, uint NestedMappingScopeInstanceEntityId)> nestedBranches =
			new List<(int, Composite, uint)>();

		CollectEntityList(composite.functions, composite, compositesById, commands, nestedBranches, modelReferences, mappingScopeInstanceEntityId);
		if (includeVariables)
			CollectEntityList(composite.variables, composite, compositesById, commands, nestedBranches, modelReferences, mappingScopeInstanceEntityId);
		if (!deferAliasProxy)
		{
			CollectEntityList(composite.aliases, composite, compositesById, commands, nestedBranches, modelReferences, mappingScopeInstanceEntityId);
			CollectEntityList(composite.proxies, composite, compositesById, commands, nestedBranches, modelReferences, mappingScopeInstanceEntityId);
		}

		Segment segment = new Segment { Local = commands, Total = commands.Count };
		if (nestedBranches.Count == 0)
			return segment;

		segment.Children = new List<(int, Segment)>(nestedBranches.Count);
		if (nestedBranches.Count == 1)
		{
			(int parentLocalIndex, Composite nested, uint nestedScopeId) = nestedBranches[0];
			Segment child = CollectSegment(nested, compositesById, deferAliasProxy, includeVariables, modelReferences, nestedScopeId);
			segment.Children.Add((parentLocalIndex, child));
			segment.Total += child.Total;
			return segment;
		}

		(int ParentLocalIndex, Composite Nested, uint NestedMappingScopeInstanceEntityId)[] branches = nestedBranches.ToArray();
		Segment[] nestedSegments = new Segment[branches.Length];
		// Each branch collects into its own model-reference list; List<T>.Add is not thread-safe,
		// so a shared list would corrupt/crash under Parallel.For on multi-core machines.
		List<FunctionEntity>[] nestedModelReferences = new List<FunctionEntity>[branches.Length];
		Parallel.For(0, branches.Length, i =>
		{
			List<FunctionEntity> branchModelReferences = new List<FunctionEntity>();
			nestedSegments[i] = CollectSegment(
				branches[i].Nested,
				compositesById,
				deferAliasProxy,
				includeVariables,
				branchModelReferences,
				branches[i].NestedMappingScopeInstanceEntityId);
			nestedModelReferences[i] = branchModelReferences;
		});

		for (int i = 0; i < branches.Length; i++)
		{
			if (nestedModelReferences[i] != null)
				modelReferences.AddRange(nestedModelReferences[i]);
			segment.Children.Add((branches[i].ParentLocalIndex, nestedSegments[i]));
			segment.Total += nestedSegments[i].Total;
		}
		return segment;
	}

	private static void CollectEntityList(
		IEnumerable<Entity> entities,
		Composite composite,
		Dictionary<ShortGuid, Composite> compositesById,
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
			bool hasTransform = TryGetSpawnTransform(entity, out Vector3 position, out Vector3 rotationDegrees);
			commands.Add(new Command(
				-1,
				composite.shortGUID,
				entity,
				position,
				rotationDegrees,
				hasTransform,
				mappingScopeInstanceEntityId));

			if (entity is not FunctionEntity function)
				continue;

			if (function.function.IsFunctionType)
			{
				if (function.function.AsFunctionType == FunctionType.ModelReference)
					modelReferences.Add(function);
				continue;
			}

			if (compositesById.TryGetValue(function.function, out Composite nested) && nested != null)
				nestedBranches.Add((localIndex, nested, function.shortGUID.AsUInt32));
		}
	}
}
