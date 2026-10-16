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
	/// is false while the selection holds the mesh's material slot (Wireframe Transparent draws a
	/// selection as its edges alone, which takes the override): it is still ours to re-take when the
	/// selection moves on. Every other selection mode draws over the zone colour, which stays.
	/// </remarks>
	private sealed class Tint
	{
		public Material Saved;
		public Color Colour;
		public bool Applied;
		//The reconcile that last found the mesh still the zones' (see _pass): one that did not lets it go (DropUnclaimed)
		public int Pass;
	}

	private static readonly Dictionary<MeshInstance3D, Tint> _tinted = new();

	//Counts reconciles. A Tint stamped with the current one has been claimed by it - a stamp rather than a set of the claimed
	//meshes, which cost a hash insert per mesh and a lookup per entry afterwards, on every reconcile
	private static int _pass;

	/// <summary>One mesh the last walk of the level reached, and the colour it was given there.</summary>
	private readonly struct WalkedMesh
	{
		public readonly MeshInstance3D Mesh;
		public readonly Color Colour;
		public readonly bool InZone;

		public WalkedMesh(MeshInstance3D mesh, Color colour, bool inZone)
		{
			Mesh = mesh;
			Colour = colour;
			InZone = inZone;
		}
	}

	/* The last walk of the level, for the next reconcile to reuse instead of walking every node again. The walk is most of what a
	   reconcile costs - a third of a second on a big level - and nearly every reconcile follows a parameter edit, which has
	   added nothing to the level and taken nothing out of it. Which meshes the walk reaches, and in what colour, comes from the
	   tree and the resolved zone roots alone. So while nothing under the level has been added, taken out or renamed (SceneTree's
	   own signals, watched only while there is a walk to keep) and the roots come to the same nodes in the same colours, a
	   fresh walk would reach the same meshes in the same colours. Each is still reconciled exactly as the walk would do it
	   (TintMesh), so a material written over a zone colour since is caught just the same. */
	private static List<WalkedMesh> _walked;
	private static Dictionary<Node, Color> _walkedRoots;
	//What each root's path led to at that walk (before an alias or proxy is followed: that is looked at every time, as one
	//can be pointed somewhere else without the tree changing), path by path in the table's order - and that table
	private static List<Node3D> _resolvedPaths;
	private static List<SyncedZone> _resolvedZones;
	//Set while the tree is being watched for the walk; whatever changes it drops the walk (OnTreeChanged)
	private static Node3D _watchedRoot;
	private static AlienScene _watchedScene;
	private static SceneTree _watchedTree;
	private static readonly SceneTree.NodeAddedEventHandler _onNodeAdded = OnTreeChanged;
	private static readonly SceneTree.NodeRemovedEventHandler _onNodeRemoved = OnTreeChanged;
	private static readonly SceneTree.NodeRenamedEventHandler _onNodeRenamed = OnTreeChanged;

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
		ForgetWalk();

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

		/* The tree as the last walk found it (see _walked), and the same table: each root's path then leads to the node it led to
		   then, so that is taken rather than looked up again name by name. Otherwise the tree is watched from here on - what the
		   paths resolve to is part of what a kept walk stands for. */
		bool treeAsWalked = _walked != null && ReferenceEquals(_watchedRoot, root) && ReferenceEquals(_resolvedZones, _zones)
			&& ResolvedPathsStillHeld();
		if (!treeAsWalked)
		{
			ForgetWalk();
			Watch(scene, root);
		}
		List<Node3D> resolvedPaths = treeAsWalked ? _resolvedPaths : new List<Node3D>();
		int pathIndex = 0;

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
				Node3D node;
				if (treeAsWalked)
				{
					node = resolvedPaths[pathIndex++];
				}
				else
				{
					node = scene.TryResolveInstancePathNode(path);
					resolvedPaths.Add(node);
				}
				//An alias or proxy's node is an empty stand-in; what it points at is what is drawn. OpenCAGE follows them
				//itself (EntityInstancePath.TryResolveTarget) - this is for one it could not, which the viewer has wired
				if (node is EntityOverride pointer && pointer.PointedEntity != null)
					node = pointer.PointedEntity;
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

		_pass++;
		int recoloured, walked, unzoned;
		bool reused = treeAsWalked && SameRoots(_walkedRoots, coloursByRootNode);
		if (reused)
		{
			recoloured = TintWalked(_walked, out walked, out unzoned);
		}
		else
		{
			//No walk to reuse, or an alias or proxy has been pointed somewhere else since (the tree itself as it was, and still watched)
			_walked = null;
			List<WalkedMesh> meshes = new List<WalkedMesh>();
			recoloured = TintScene(scene, root, coloursByRootNode, meshes, out walked, out unzoned);
			//Kept only while the watch holds: anything added, taken out or renamed since it started has dropped it already
			if (_watchedRoot != null)
			{
				_walked = meshes;
				_walkedRoots = coloursByRootNode;
				_resolvedPaths = resolvedPaths;
				_resolvedZones = _zones;
			}
		}
		int released = DropUnclaimed();

		ViewerLog.Print("[Zones] " + (walked - unzoned) + " of " + walked + " meshes across " + drawnZones + " of "
			+ _zones.Count + " zones, " + unzoned + " in no zone (white), in " + timer.ElapsedMilliseconds + "ms ("
			+ recoloured + " recoloured, " + released + " released)"
			+ (unresolvedRoots != 0 ? " (" + unresolvedRoots + " roots not in the scene)" : "")
			+ (reused ? " (tree unchanged: the last walk reused)" : "") + ".");
	}

	//The same root nodes in the same colours
	private static bool SameRoots(Dictionary<Node, Color> before, Dictionary<Node, Color> now)
	{
		if (before == null || before.Count != now.Count)
			return false;

		foreach (KeyValuePair<Node, Color> entry in now)
		{
			if (!before.TryGetValue(entry.Key, out Color colour) || colour != entry.Value)
				return false;
		}

		return true;
	}

	/// <summary>Reconcile the meshes the last walk reached, in its order and its colours, without walking the tree.</summary>
	private static int TintWalked(List<WalkedMesh> meshes, out int walked, out int unzoned)
	{
		int recoloured = 0;
		int seen = 0;
		int outside = 0;

		for (int i = 0; i < meshes.Count; i++)
		{
			LevelViewerSentMessages.PumpIfDue();
			WalkedMesh entry = meshes[i];
			//Passed over as the walk passes over them: freed, or on the way out (see TintScene)
			if (!GodotObject.IsInstanceValid(entry.Mesh) || entry.Mesh.IsQueuedForDeletion())
				continue;

			seen++;
			if (!entry.InZone)
				outside++;
			if (TintMesh(entry.Mesh, entry.Colour))
				recoloured++;
		}

		walked = seen;
		unzoned = outside;
		return recoloured;
	}

	//Start watching the level for anything added under it or taken out of it, for the walk about to be made
	private static void Watch(AlienScene scene, Node3D root)
	{
		SceneTree tree = root.IsInsideTree() ? root.GetTree() : null;
		if (tree == null)
			return;

		tree.NodeAdded += _onNodeAdded;
		tree.NodeRemoved += _onNodeRemoved;
		tree.NodeRenamed += _onNodeRenamed;
		_watchedTree = tree;
		_watchedRoot = root;
		_watchedScene = scene;
	}

	private static void ForgetWalk()
	{
		_walked = null;
		_walkedRoots = null;
		_resolvedPaths = null;
		_resolvedZones = null;
		if (_watchedTree != null && GodotObject.IsInstanceValid(_watchedTree))
		{
			_watchedTree.NodeAdded -= _onNodeAdded;
			_watchedTree.NodeRemoved -= _onNodeRemoved;
			_watchedTree.NodeRenamed -= _onNodeRenamed;
		}
		_watchedTree = null;
		_watchedRoot = null;
		_watchedScene = null;
	}

	//One node per path of the table, each still there (or never found): a guard - anything freed was taken out of the tree first
	private static bool ResolvedPathsStillHeld()
	{
		if (_resolvedPaths == null)
			return false;

		int paths = 0;
		foreach (SyncedZone zone in _zones)
		{
			if (zone?.roots != null)
				paths += zone.roots.Count;
		}
		if (paths != _resolvedPaths.Count)
			return false;

		for (int i = 0; i < _resolvedPaths.Count; i++)
		{
			if (_resolvedPaths[i] != null && !GodotObject.IsInstanceValid(_resolvedPaths[i]))
				return false;
		}

		return true;
	}

	/* A node added anywhere in the tree, taken out of it or renamed (a path goes by names). One under the level drops the walk -
	   unless it is inside one of the viewport's own overlays (or is one), which the walk never goes into and no path names: a
	   selected light's range is rebuilt on every edit. */
	private static void OnTreeChanged(Node node)
	{
		Node3D root = _watchedRoot;
		if (root == null)
			return;

		if (GodotObject.IsInstanceValid(root) && node != null && GodotObject.IsInstanceValid(node))
		{
			if (!ReferenceEquals(node, root) && !root.IsAncestorOf(node))
				return;

			for (Node n = node; n != null && !ReferenceEquals(n, root); n = n.GetParent())
			{
				if (_watchedScene.IsViewportOverlay(n))
					return;
			}
		}

		ForgetWalk();
	}

	private static int TintScene(
		AlienScene scene,
		Node3D root,
		Dictionary<Node, Color> coloursByRootNode,
		List<WalkedMesh> reached,
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

			/* The viewport's own overlays live in the level's tree but are no part of it - the navmesh and cover sheets,
			   the collision filter, a selected light's range. Filled with a flat opaque colour they hid the level under
			   them (the navmesh as a white sheet over every floor), and they mean nothing in zone terms. */
			if (scene.IsViewportOverlay(node))
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
				Color meshColour = inZone ? colour : UnzonedColour;
				reached.Add(new WalkedMesh(mesh, meshColour, inZone));
				if (TintMesh(mesh, meshColour))
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
	private static bool TintMesh(MeshInstance3D mesh, Color colour)
	{
		Material ownBefore = null;
		if (_tinted.TryGetValue(mesh, out Tint existing))
		{
			//Read once for both questions below (nothing between them writes it): an interop call, on every mesh
			Material wearing = mesh.MaterialOverride;

			//The selection is drawing it as its edges and has the material slot; it hands back the mesh's own material
			//when it moves on, and SyncWithSelection colours it then - in whatever colour it should be by that time
			if (LevelViewerHighlightOverlay.IsSelectionWireframe(wearing))
			{
				existing.Colour = colour;
				existing.Applied = false;
				existing.Pass = _pass;
				return false;
			}

			//Already the right colour and still carrying it: the common case, and free
			if (existing.Applied && existing.Colour == colour && AlienSceneMaterials.IsZoneTintMaterial(wearing))
			{
				existing.Pass = _pass;
				return false;
			}

			ownBefore = existing.Saved;
			Restore(mesh, existing);
			_tinted.Remove(mesh);
		}

		//The wireframe pass draws a model's edges over it; filling those in solid would erase it. A scene filter's hulls
		//(occlusion volumes, collision) are drawn in the filter's own colour when it is switched on to look at them
		if (mesh.IsInGroup(LevelViewerPick.WireframeOverlayGroupName) || mesh.IsInGroup(LevelViewerPick.SceneFilterGroupName))
			return false;

		//Selected while it was being spawned, with the selection holding its slot: wait for it to let go
		if (IsHeldBySelection(mesh))
		{
			_tinted[mesh] = new Tint { Saved = ownBefore, Colour = colour, Applied = false, Pass = _pass };
			return false;
		}

		//What it is drawn with when it is not ours: never one of our own colours, which a restore would put back for good
		Material own = mesh.MaterialOverride;
		if (AlienSceneMaterials.IsZoneTintMaterial(own))
			own = ownBefore;

		//A preview icon is a camera-facing sprite, not a piece of the level: flat-filling it says nothing
		Material current = own ?? mesh.GetActiveMaterial(0);
		if (current == null || PreviewVisualUtility.IsIconBillboardMaterial(current))
			return false;

		Material tint = TintFor(colour, current);
		if (tint == null)
			return false;

		_tinted[mesh] = new Tint { Saved = own, Colour = colour, Applied = true, Pass = _pass };
		mesh.MaterialOverride = tint;
		return true;
	}

	/* The zone colour in a material that draws the way the mesh's own does: both faces where it drew both, and see-through
	   where it was see-through - a trigger box or sphere is a translucent volume, and filled solid it hid the room it sits in. */
	private static Material TintFor(Color colour, Material own)
	{
		if (PreviewVisualUtility.TryGetTransparentPreviewAlpha(own, out float alpha))
			return AlienSceneMaterials.GetZoneTintMaterial(new Color(colour.R, colour.G, colour.B, alpha), false);
		return AlienSceneMaterials.GetZoneTintMaterial(colour, AlienSceneMaterials.IsDoubleSidedMaterial(own));
	}

	/// <summary>
	/// Let go of everything no zone reached this time - including meshes that no longer exist, which
	/// is most of what turns up here: editing a material respawns every model reference showing it.
	/// </summary>
	private static int DropUnclaimed()
	{
		List<MeshInstance3D> going = null;
		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
		{
			if (entry.Value.Pass == _pass)
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
	/// <remarks>
	/// Only a selection drawn as its edges alone (Wireframe Transparent) takes a mesh's material slot - the other modes draw
	/// over the zone colour, which stays, so a selection keeps showing which zone it is in. Run after every selection change.
	/// </remarks>
	public static void SyncWithSelection()
	{
		foreach (KeyValuePair<MeshInstance3D, Tint> entry in _tinted)
		{
			MeshInstance3D mesh = entry.Key;
			Tint tint = entry.Value;
			if (tint.Applied || mesh == null || !GodotObject.IsInstanceValid(mesh) || mesh.IsQueuedForDeletion() || IsHeldBySelection(mesh))
				continue;

			//What the selection gave back is the mesh's own material - never one of our colours, which a restore would put back for good
			Material own = mesh.MaterialOverride;
			if (AlienSceneMaterials.IsZoneTintMaterial(own))
				own = tint.Saved;

			Material material = TintFor(tint.Colour, own ?? mesh.GetActiveMaterial(0));
			if (material == null)
				continue;

			tint.Saved = own;
			tint.Applied = true;
			mesh.MaterialOverride = material;
		}
	}

	/// <summary>
	/// The material a selection drawn as its edges should put back when it lets go of <paramref name="mesh"/>: the mesh's own,
	/// not the zone colour it is wearing - which is handed over meanwhile (SyncWithSelection takes it back). Put back the
	/// colour, a mesh would keep it after zones went off, the restore having already run.
	/// </summary>
	public static Material LendToSelection(MeshInstance3D mesh)
	{
		Material current = mesh?.MaterialOverride;
		if (mesh == null || !_tinted.TryGetValue(mesh, out Tint tint) || !AlienSceneMaterials.IsZoneTintMaterial(current))
			return current;

		tint.Applied = false;
		return tint.Saved != null && GodotObject.IsInstanceValid(tint.Saved) ? tint.Saved : null;
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

	//The selection is drawing it as its edges alone, in the slot the colour goes in (one property read, no tree walk: this is
	//asked of every coloured mesh on every selection change)
	private static bool IsHeldBySelection(MeshInstance3D mesh) => LevelViewerHighlightOverlay.IsSelectionWireframe(mesh.MaterialOverride);
}
