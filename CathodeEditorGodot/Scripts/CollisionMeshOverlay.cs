using CATHODE;
using CATHODE.Scripting;
using CathodeLib;
using Godot;
using OpenCAGE;
using System;
using System.Collections.Generic;

/// <summary>
/// Draws the level's Havok collision geometry when the Collision Meshes filter is on.
///
/// COLLISION.MAP addresses one instance inside a shared world host compound per entity, so the host is
/// triangulated once (tracking which instance produced each triangle) and then sliced back out into a
/// mesh per entity. A row's CollisionProxy is only a template - triangulating that would produce
/// instances no row ever references.
///
/// Built lazily: a level's collision is expensive to triangulate, and the filter defaults to off.
/// </summary>
public partial class CollisionMeshOverlay : Node3D
{
	private readonly Dictionary<uint, List<MeshInstance3D>> _meshesByEntity = new Dictionary<uint, List<MeshInstance3D>>();

	//One holder per entity, parented under that entity's node so the collision follows it when moved
	private readonly Dictionary<uint, Node3D> _holdersByEntity = new Dictionary<uint, Node3D>();

	private AlienScene _scene;
	private LevelContent _content;
	private bool _built;

	public void Setup(AlienScene scene)
	{
		_scene = scene;
		_content = scene?.Content;
		Clear();
	}

	public void Clear()
	{
		foreach (KeyValuePair<uint, Node3D> holder in _holdersByEntity)
		{
			if (holder.Value != null && GodotObject.IsInstanceValid(holder.Value))
				holder.Value.QueueFree();
		}

		_holdersByEntity.Clear();
		_meshesByEntity.Clear();
		_built = false;
	}

	/// <summary>Show/hide, building the geometry the first time it's actually asked for.</summary>
	public void ApplyFilter()
	{
		bool enabled = RenderFilters.IsSceneFilterEnabled(SceneFilterKind.CollisionMeshes);
		if (enabled && !_built)
			Build();

		//The meshes live under their entities rather than under this node, so visibility is per holder
		foreach (KeyValuePair<uint, Node3D> holder in _holdersByEntity)
		{
			if (holder.Value != null && GodotObject.IsInstanceValid(holder.Value))
				holder.Value.Visible = enabled;
		}

		Visible = enabled;
	}

	/// <summary>Rebuild after an entity's COLLISION_MAPPING resource changed.</summary>
	public void RefreshEntity(ShortGuid entity)
	{
		if (!_built)
			return;

		//The host triangulation is shared across every entity, so a single one can't be re-sliced on
		//its own - drop the lot and rebuild if the overlay is on screen.
		Clear();
		if (RenderFilters.IsSceneFilterEnabled(SceneFilterKind.CollisionMeshes))
			Build();
	}

	private void Build()
	{
		_built = true;
		if (_content?.Level?.CollisionMaps == null)
			return;

		HavokPackfile packfile = _content.Level.CollisionHKX ?? _content.Level.CollisionHKX64;
		if (packfile == null)
		{
			ViewerLog.Print("[Collision] No COLLISION.HKX loaded - nothing to draw.");
			return;
		}

		System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

		//The hosts are what carry the instances rows address (see the class note)
		Dictionary<HavokPackfile.StaticCompoundShape, bool> hosts =
			new Dictionary<HavokPackfile.StaticCompoundShape, bool>();

		//Which entity each instance belongs to
		Dictionary<HavokPackfile.CompoundInstance, uint> entityByInstance =
			new Dictionary<HavokPackfile.CompoundInstance, uint>();

		for (int i = 0; i < _content.Level.CollisionMaps.Entries.Count; i++)
		{
			CollisionMaps.COLLISION_MAPPING mapping = _content.Level.CollisionMaps.Entries[i];
			if (mapping?.CollisionInstance == null)
				continue;
			if (mapping.Entity == null || mapping.Entity.entity_id == ShortGuid.Invalid)
				continue;

			entityByInstance[mapping.CollisionInstance] = mapping.Entity.entity_id.AsUInt32;

			HavokPackfile.StaticCompoundShape host = mapping.CollisionInstance.Owner
				?? packfile.WorldHostFor((mapping.Flags & CollisionMaps.CollisionFlags.WORLD) != 0);
			if (host != null)
				hosts[host] = true;
		}

		if (hosts.Count == 0)
		{
			ViewerLog.Print("[Collision] No collision hosts resolved from "
				+ _content.Level.CollisionMaps.Entries.Count + " mappings - nothing to draw.");
			return;
		}

		Material material = AlienSceneMaterials.GetSceneFilterMaterial(SceneFilterKind.CollisionMeshes);
		Dictionary<uint, List<Node3D>> entityNodes = BuildEntityNodeLookup();
		int spawned = 0;

		foreach (HavokPackfile.StaticCompoundShape host in new List<HavokPackfile.StaticCompoundShape>(hosts.Keys))
		{
			HavokPackfile.PreviewMesh preview;
			try
			{
				preview = packfile.BuildBakeMesh(host, skipInstances: null, trackInstances: true);
			}
			catch (Exception ex)
			{
				ViewerLog.PrintErr("[Collision] Failed to triangulate host: " + ex.Message);
				continue;
			}

			if (preview == null || preview.InstanceRanges == null || preview.TriangleCount == 0)
			{
				ViewerLog.Print("[Collision] Host produced no tracked triangles.");
				continue;
			}

			spawned += SliceInstances(preview, entityByInstance, entityNodes, material);
		}

		ViewerLog.Print("[Collision] Built " + spawned + " collision meshes from " + hosts.Count
			+ " host(s) in " + timer.ElapsedMilliseconds + "ms.");
	}

	/// <summary>Split a host's triangles back into one mesh per entity.</summary>
	private int SliceInstances(
		HavokPackfile.PreviewMesh preview,
		Dictionary<HavokPackfile.CompoundInstance, uint> entityByInstance,
		Dictionary<uint, List<Node3D>> entityNodes,
		Material material)
	{
		//InstanceRanges is (first triangle, instance) in ascending triangle order
		Dictionary<uint, List<int>> trianglesByEntity = new Dictionary<uint, List<int>>();
		for (int range = 0; range < preview.InstanceRanges.Count; range++)
		{
			HavokPackfile.CompoundInstance instance = preview.InstanceRanges[range].Value;
			if (instance == null || !entityByInstance.TryGetValue(instance, out uint entity))
				continue;

			int firstTriangle = preview.InstanceRanges[range].Key;
			int endTriangle = range + 1 < preview.InstanceRanges.Count
				? preview.InstanceRanges[range + 1].Key
				: preview.TriangleCount;

			if (!trianglesByEntity.TryGetValue(entity, out List<int> indices))
			{
				indices = new List<int>();
				trianglesByEntity[entity] = indices;
			}

			for (int triangle = firstTriangle; triangle < endTriangle; triangle++)
			{
				int i = triangle * 3;
				if (i + 2 >= preview.Indices.Count)
					break;

				indices.Add(preview.Indices[i]);
				indices.Add(preview.Indices[i + 1]);
				indices.Add(preview.Indices[i + 2]);
			}
		}

		int spawned = 0;
		foreach (KeyValuePair<uint, List<int>> entry in trianglesByEntity)
		{
			ArrayMesh mesh = BuildMesh(preview.Positions, entry.Value);
			if (mesh == null)
				continue;

			Node3D holder = GetOrCreateHolder(entry.Key, entityNodes);
			MeshInstance3D instance = new MeshInstance3D
			{
				Name = "collision_" + entry.Key,
				Mesh = mesh,
				MaterialOverride = material,
			};
			LevelViewerMeshUtil.ConfigureMeshInstance(instance);
			holder.AddChild(instance);

			if (!_meshesByEntity.TryGetValue(entry.Key, out List<MeshInstance3D> meshes))
			{
				meshes = new List<MeshInstance3D>();
				_meshesByEntity[entry.Key] = meshes;
			}
			meshes.Add(instance);
			spawned++;
		}

		return spawned;
	}

	/// <summary>Entity shortGUID to the scene nodes representing it, so collision can follow its owner.</summary>
	private Dictionary<uint, List<Node3D>> BuildEntityNodeLookup()
	{
		Dictionary<uint, List<Node3D>> lookup = new Dictionary<uint, List<Node3D>>();
		if (_scene?.NodeEntities == null)
			return lookup;

		foreach (KeyValuePair<Node3D, CATHODE.Scripting.Internal.Entity> entry in _scene.NodeEntities)
		{
			if (entry.Key == null || entry.Value == null || !GodotObject.IsInstanceValid(entry.Key))
				continue;

			uint key = entry.Value.shortGUID.AsUInt32;
			if (!lookup.TryGetValue(key, out List<Node3D> nodes))
			{
				nodes = new List<Node3D>();
				lookup[key] = nodes;
			}
			nodes.Add(entry.Key);
		}

		return lookup;
	}

	/// <summary>
	/// A holder under the owning entity's node, zeroed to the level root. The triangles come out of
	/// Havok already positioned in the level's own space, so the holder cancels the entity's transform
	/// to land them correctly - and from then on it inherits the entity's movement for free.
	///
	/// Zeroed to the level root and not to the world origin: the viewer shifts the whole level so the
	/// initial focus point sits near the origin (RecenterContentOrigin), and pinning the holders to
	/// world identity left every hull correct relative to its neighbours but the whole set offset by
	/// that shift.
	///
	/// It's a plain Node3D rather than putting the meshes straight on the entity, because a resource
	/// refresh frees the entity's MeshInstance3D children and would take the collision with it.
	/// </summary>
	private Node3D GetOrCreateHolder(uint entity, Dictionary<uint, List<Node3D>> entityNodes)
	{
		if (_holdersByEntity.TryGetValue(entity, out Node3D existing) && GodotObject.IsInstanceValid(existing))
			return existing;

		Node3D holder = new Node3D { Name = "CollisionOverlay" };

		Node3D owner = null;
		if (entityNodes.TryGetValue(entity, out List<Node3D> nodes) && nodes.Count > 0)
			owner = nodes[0];

		if (owner != null && owner.IsInsideTree() && IsInsideTree())
		{
			owner.AddChild(holder);
			//This overlay sits directly under the level root, so its own transform is level space
			holder.GlobalTransform = GlobalTransform;
		}
		else
		{
			//Nothing in the scene for it - leave it parked in level space under the overlay
			AddChild(holder);
		}

		_holdersByEntity[entity] = holder;
		return holder;
	}

	/// <summary>
	/// Build the entity's triangles as unshared vertices carrying their face normal. Collision hulls
	/// have no normals of their own, and flat-faceted shading is what makes a hull's shape readable -
	/// smoothing across a shared vertex pool would flatten it back into a silhouette.
	/// </summary>
	private static ArrayMesh BuildMesh(List<System.Numerics.Vector3> sourcePositions, List<int> sourceIndices)
	{
		if (sourceIndices == null || sourceIndices.Count < 3)
			return null;

		int triangleCount = sourceIndices.Count / 3;
		Vector3[] positions = new Vector3[triangleCount * 3];
		Vector3[] normals = new Vector3[triangleCount * 3];
		int written = 0;

		for (int t = 0; t < triangleCount; t++)
		{
			int i0 = sourceIndices[t * 3];
			int i1 = sourceIndices[t * 3 + 1];
			int i2 = sourceIndices[t * 3 + 2];
			if (i0 < 0 || i1 < 0 || i2 < 0
				|| i0 >= sourcePositions.Count || i1 >= sourcePositions.Count || i2 >= sourcePositions.Count)
				continue;

			Vector3 a = ToGodot(sourcePositions[i0]);
			Vector3 b = ToGodot(sourcePositions[i1]);
			Vector3 c = ToGodot(sourcePositions[i2]);

			Vector3 normal = (b - a).Cross(c - a);
			normal = normal.LengthSquared() > 1e-12f ? normal.Normalized() : Vector3.Up;

			positions[written] = a; normals[written++] = normal;
			positions[written] = b; normals[written++] = normal;
			positions[written] = c; normals[written++] = normal;
		}

		if (written == 0)
			return null;

		if (written != positions.Length)
		{
			Array.Resize(ref positions, written);
			Array.Resize(ref normals, written);
		}

		//`using` keeps the managed wrapper alive across AddSurfaceFromArrays. The binding hands the engine the native
		//array by value with no GC.KeepAlive, so once the wrapper has no further use the collector may finalize it
		//mid-call, and Array's finalizer destroys the native array while the engine is still reading it: "Condition
		//!success at Array::_ref" followed by an access violation, which killed the viewer building this overlay for
		//SCI_Hub in the 3 Sep 2026 soak. Every other AddSurfaceFromArrays site does the same.
		using Godot.Collections.Array surface = new Godot.Collections.Array();
		surface.Resize((int)Mesh.ArrayType.Max);
		surface[(int)Mesh.ArrayType.Vertex] = positions;
		surface[(int)Mesh.ArrayType.Normal] = normals;

		ArrayMesh mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surface);
		return mesh;
	}

	private static Vector3 ToGodot(System.Numerics.Vector3 point)
	{
		return CathodeCoordinates.PositionToGodot(new Vector3(point.X, point.Y, point.Z));
	}
}
