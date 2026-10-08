using Godot;
using OpenCAGE;
using OpenCAGE.UnityConnection;
using System.Collections.Generic;

/// <summary>
/// Draws the level's geometry in the colour of the zone it belongs to, one colour per zone - and
/// white where no zone reaches.
/// </summary>
/// <remarks>
/// The zones themselves come from OpenCAGE (ZONES_CHANGED): membership is made entirely of links,
/// which are not part of the entity sync, so this side is told the answer rather than working it out
/// from a copy of the level that would go stale at the first rewiring.
///
/// A zone names the entities it reaches directly and claims everything inside them, which is exactly
/// a node and its subtree here - so the roots arrive as instance paths and the colour runs down from
/// each. Where roots nest, the innermost wins; see Rebuild for why that is the right way round.
///
/// The colour REPLACES each mesh's material rather than being laid over it. An overlay is what the
/// selection and alias highlights use, and it is the right tool for a handful of meshes: it keeps the
/// surface underneath. Here it covers most of a level at once, and a second draw call on 69,000 meshes
/// - in the transparent pass, so no early-Z either - made the viewport unusable. Swapping the material
/// costs nothing per frame over drawing the level normally, and the textures are not wanted anyway.
/// </remarks>
public static class LevelViewerZoneHighlight
{
	/// <summary>What one mesh is carrying for us, and what it was carrying before.</summary>
	/// <remarks>
	/// A mesh is in here while it is coloured - by its zone, or white for none. <see cref="Applied"/>
	/// is false while the mesh has been handed back to the selection, which is drawn in its own
	/// material - it is still ours to re-take when the selection moves on.
	/// </remarks>
	private sealed class Tint
	{
		public Material Saved;
		public Color Colour;
		public bool Applied;
	}

	private static readonly Dictionary<MeshInstance3D, Tint> _tinted = new();

	private static List<SyncedZone> _zones = new();

	/* Whether OpenCAGE has sent a table for this level - an empty one included, as a level with no zones (a new one, say)
	   has everything unzoned. Until it has, nothing is coloured: the level is still loading, and painting it all white
	   only to recolour it a moment later would flash. */
	private static bool _haveTable = false;

	/// <summary>
	/// What no zone reaches is drawn white: left in its own material it looked as though nothing had been worked out for
	/// it, when being in no zone is an answer in itself. No zone's colour comes near white - they are kept well saturated
	/// (ZoneDefinitions.GetColour).
	/// </summary>
	public static readonly Color UnzonedColour = new Color(1f, 1f, 1f, 1f);

	/// <summary>Whether anything is currently coloured - not whether a table has arrived.</summary>
	public static bool HasAny => _tinted.Count != 0;

	/// <summary>The table OpenCAGE last worked out. Replacing it takes effect on the next rebuild.</summary>
	public static void SetZones(List<SyncedZone> zones)
	{
		_zones = zones ?? new List<SyncedZone>();
		_haveTable = true;
	}

	public static void Clear()
	{
		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
			Restore(entry.Key, entry.Value);

		_tinted.Clear();
	}

	/// <summary>Forget the table as well as the colouring - the level itself has gone.</summary>
	public static void Reset()
	{
		Clear();
		_zones = new List<SyncedZone>();
		_haveTable = false;
		//After Clear: its restore still needs to recognise them
		AlienSceneMaterials.ClearZoneTintMaterials();
	}

	/// <remarks>
	/// One pass over the scene rather than one per root. A level names thousands of roots and they
	/// nest - a room's root sits inside the environment instance's - so walking each root's subtree
	/// would walk the deep ones once per root above them. Instead the roots are resolved to nodes
	/// first and the tree is walked once, carrying whichever zone is currently in force.
	///
	/// Carrying it means the innermost root wins, which is the rule the build settles on: a zone that
	/// names an entity directly beats one that only reached it by descending into a composite. It is
	/// also what was measured - on TECH_COMMS this agrees with the instancer's own primary zone for
	/// 992,699 of the 992,704 entities it zones, the other 5 being their secondary.
	///
	/// It RECONCILES rather than starting again: a mesh already carrying the right colour is left
	/// alone. That is what makes this safe to run whenever the scene changes - editing a material
	/// respawns every model reference showing it, and those come back in their own material and have
	/// to be recoloured - without paying to rewrite 69,000 materials each time something small moves.
	/// </remarks>
	public static void Rebuild(AlienScene scene)
	{
		Node3D root = scene?.ParentNode;
		if (root == null || !GodotObject.IsInstanceValid(root)
			|| !PreviewVisibilitySettings.ShowZones || !_haveTable)
		{
			Clear();
			return;
		}

		System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

		//Keyed by the node itself: Godot keeps one managed wrapper per node, and GetInstanceId() is an
		//interop call this would otherwise make a quarter of a million times per rebuild
		Dictionary<Node, Color> coloursByRootNode = new Dictionary<Node, Color>();
		int drawnZones = 0;
		int unresolvedRoots = 0;

		foreach (SyncedZone zone in _zones)
		{
			if (zone?.roots == null)
				continue;

			Color colour = new Color(zone.colour_r, zone.colour_g, zone.colour_b, 1f);
			bool resolvedAny = false;

			foreach (List<uint> path in zone.roots)
			{
				LevelViewerSentMessages.PumpIfDue();
				Node3D node = scene.TryResolveInstancePathNode(path);
				if (node == null)
				{
					unresolvedRoots++;
					continue;
				}

				resolvedAny = true;
				//Two zones naming the same entity: the first keeps it, as the build's first arrival does
				if (!coloursByRootNode.ContainsKey(node))
					coloursByRootNode[node] = colour;
			}

			if (resolvedAny)
				drawnZones++;
		}

		HashSet<MeshInstance3D> claimed = new HashSet<MeshInstance3D>();
		int recoloured = TintScene(root, coloursByRootNode, claimed, out int walked, out int unzoned);
		int released = DropUnclaimed(claimed);

		ViewerLog.Print("[Zones] " + (walked - unzoned) + " of " + walked + " meshes across " + drawnZones + " of "
			+ _zones.Count + " zones, " + unzoned + " in no zone (white), in " + timer.ElapsedMilliseconds + "ms ("
			+ recoloured + " recoloured, " + released + " released)"
			+ (unresolvedRoots != 0 ? " (" + unresolvedRoots + " roots not in the scene)" : "") + ".");
	}

	private static int TintScene(
		Node3D root,
		Dictionary<Node, Color> coloursByRootNode,
		HashSet<MeshInstance3D> claimed,
		out int walked,
		out int unzoned)
	{
		int recoloured = 0;
		int seen = 0;
		int outside = 0;

		//Explicit stack: composite nesting can run deep and this covers the whole level
		Stack<(Node Node, Color Colour, bool InZone)> pending = new Stack<(Node, Color, bool)>();
		pending.Push((root, default, false));

		while (pending.Count != 0)
		{
			//Every node of the level: seconds on the big ones, with no sent message answered otherwise
			LevelViewerSentMessages.PumpIfDue();
			(Node node, Color colour, bool inZone) = pending.Pop();
			if (node == null || !GodotObject.IsInstanceValid(node))
				continue;

			/* A respawn frees the old meshes with QueueFree, which does not take effect until the end
			   of the frame - and a resource sync rebuilds and then refreshes highlights within one.
			   Colouring the outgoing copies would double the count and leave them to be dropped on
			   some later rebuild; they are already on their way out, so pass over them and their
			   children, which go with them. */
			if (node.IsQueuedForDeletion())
				continue;

			if (coloursByRootNode.TryGetValue(node, out Color rootColour))
			{
				colour = rootColour;
				inZone = true;
			}

			if (node is MeshInstance3D mesh)
			{
				seen++;
				if (!inZone)
					outside++;
				if (TintMesh(mesh, inZone ? colour : UnzonedColour, claimed))
					recoloured++;
			}

			//GetChild by index rather than GetChildren(): this runs on every node in the level, and
			//GetChildren allocates and marshals a Godot array for each one
			int children = node.GetChildCount();
			for (int i = 0; i < children; i++)
				pending.Push((node.GetChild(i), colour, inZone));
		}

		walked = seen;
		unzoned = outside;
		return recoloured;
	}

	/// <summary>Bring one mesh to the colour it should be. True when that meant actually writing to it.</summary>
	private static bool TintMesh(MeshInstance3D mesh, Color colour, HashSet<MeshInstance3D> claimed)
	{
		if (_tinted.TryGetValue(mesh, out Tint existing))
		{
			//Already the right colour and still carrying it: the common case, and free
			bool stillOurs = !existing.Applied || AlienSceneMaterials.IsZoneTintMaterial(mesh.MaterialOverride);
			if (existing.Colour == colour && stillOurs)
			{
				claimed.Add(mesh);
				return false;
			}

			Restore(mesh, existing);
			_tinted.Remove(mesh);
		}

		//The wireframe pass draws a model's edges over it; filling those in solid would erase it
		if (mesh.IsInGroup(LevelViewerPick.WireframeOverlayGroupName))
			return false;

		//A preview icon is a camera-facing sprite, not a piece of the level: flat-filling it says nothing
		Material current = mesh.MaterialOverride ?? mesh.GetActiveMaterial(0);
		if (current == null || PreviewVisualUtility.IsIconBillboardMaterial(current))
			return false;

		Material tint = AlienSceneMaterials.GetZoneTintMaterial(
			colour, AlienSceneMaterials.IsDoubleSidedMaterial(current));
		if (tint == null)
			return false;

		claimed.Add(mesh);

		//The selection is drawn as itself, textures and all; this waits for it to move on
		if (IsMeshUnderSelection(mesh))
		{
			_tinted[mesh] = new Tint { Saved = mesh.MaterialOverride, Colour = colour, Applied = false };
			return false;
		}

		_tinted[mesh] = new Tint { Saved = mesh.MaterialOverride, Colour = colour, Applied = true };
		mesh.MaterialOverride = tint;
		return true;
	}

	/// <summary>
	/// Let go of everything no zone reached this time - including meshes that no longer exist, which
	/// is most of what turns up here: editing a material respawns every model reference showing it.
	/// </summary>
	private static int DropUnclaimed(HashSet<MeshInstance3D> claimed)
	{
		List<MeshInstance3D> going = null;
		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
		{
			if (claimed.Contains(entry.Key))
				continue;

			(going ??= new List<MeshInstance3D>()).Add(entry.Key);
		}

		if (going == null)
			return 0;

		for (int i = 0; i < going.Count; i++)
		{
			Restore(going[i], _tinted[going[i]]);
			_tinted.Remove(going[i]);
		}

		return going.Count;
	}

	/// <summary>Re-colour anything the selection has since let go of.</summary>
	public static void SyncWithSelection()
	{
		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
		{
			MeshInstance3D mesh = entry.Key;
			Tint tint = entry.Value;
			if (tint.Applied || mesh == null || !GodotObject.IsInstanceValid(mesh) || IsMeshUnderSelection(mesh))
				continue;

			Material material = AlienSceneMaterials.GetZoneTintMaterial(
				tint.Colour, AlienSceneMaterials.IsDoubleSidedMaterial(mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)));
			if (material == null)
				continue;

			tint.Saved = mesh.MaterialOverride;
			tint.Applied = true;
			mesh.MaterialOverride = material;
		}
	}

	/// <summary>
	/// Give the meshes under <paramref name="root"/> their own material back, so a selected entity is
	/// seen as itself - textures and all - rather than as a flat piece of its zone.
	/// </summary>
	public static void ReleaseNode(Node3D root)
	{
		if (root == null || !GodotObject.IsInstanceValid(root) || _tinted.Count == 0)
			return;

		/* The selection's own meshes, found by walking down from it and looked up here: a selection is usually one entity,
		   and asking every tinted mesh whether the selection is its ancestor (an interop call each) was ~150k calls per
		   selected node on a DLC map. A selection with more nodes under it than there are tinted meshes scans instead. */
		List<MeshInstance3D> under = new List<MeshInstance3D>();
		if (CollectMeshesUnder(root, under, _tinted.Count))
		{
			for (int i = 0; i < under.Count; i++)
			{
				if (_tinted.TryGetValue(under[i], out Tint found) && found.Applied)
					Restore(under[i], found);
			}
			return;
		}

		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
		{
			MeshInstance3D mesh = entry.Key;
			if (!entry.Value.Applied || mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (mesh != root && !root.IsAncestorOf(mesh))
				continue;

			Restore(mesh, entry.Value);
		}
	}

	/// <summary>The meshes at and under <paramref name="root"/>; false (and a partial list) past <paramref name="limit"/> nodes.</summary>
	private static bool CollectMeshesUnder(Node root, List<MeshInstance3D> into, int limit)
	{
		Stack<Node> pending = new Stack<Node>();
		pending.Push(root);
		int visited = 0;
		while (pending.Count != 0)
		{
			Node node = pending.Pop();
			if (node == null || !GodotObject.IsInstanceValid(node))
				continue;
			if (++visited > limit)
				return false;

			if (node is MeshInstance3D mesh)
				into.Add(mesh);

			int children = node.GetChildCount();
			for (int i = 0; i < children; i++)
				pending.Push(node.GetChild(i));
		}
		return true;
	}

	/// <summary>
	/// Put back what a mesh was drawn with - unless something else has taken the override over since
	/// (composite focus dimming, a wireframe selection), which is then left holding it.
	/// </summary>
	private static void Restore(MeshInstance3D mesh, Tint tint)
	{
		tint.Applied = false;

		if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			return;

		//Only ours to give back. Whoever took it saved our colour and will put that back in turn.
		if (!AlienSceneMaterials.IsZoneTintMaterial(mesh.MaterialOverride))
			return;

		//A material retired by a resource sync is disposed; drawing the mesh's own surfaces beats a freed one
		mesh.MaterialOverride = tint.Saved != null && GodotObject.IsInstanceValid(tint.Saved) ? tint.Saved : null;
	}

	/* IsUnderSelection already walks the node's ancestors. This used to walk them too and ask it at every level - depth
	   squared interop calls per mesh, for every tinted mesh on every selection change: seconds a click on a DLC map
	   with Show Zones on, packets handled with no frame drawn in between. */
	private static bool IsMeshUnderSelection(MeshInstance3D mesh) => LevelViewerSelection.IsUnderSelection(mesh);
}
