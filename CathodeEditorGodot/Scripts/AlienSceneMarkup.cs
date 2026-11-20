using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Generic;

/// <summary>
/// Editor-only composite markup (COMPOSITE_MARKUP): the shapes OpenCAGE sends for a composite, drawn under every instance of it
/// behind the Markup scene filter. One shared mesh per composite and one node per instance, kept out of picking, selection
/// highlights, composite focus and composite previews; never part of the level.
/// </summary>
public partial class AlienScene
{
	internal static readonly StringName MarkupNodeName = new StringName("OpenCAGE_Markup");
	private static readonly NodePath MarkupNodePath = new NodePath("OpenCAGE_Markup");

	private readonly Dictionary<uint, ArrayMesh> _markupMeshes = new Dictionary<uint, ArrayMesh>();
	private string _markupLevel;

	/// <summary>Replace the markup with what OpenCAGE sent for this level, and redraw it on every instance.</summary>
	public void SetCompositeMarkup(string levelName, List<SyncedCompositeMarkup> markup)
	{
		RemoveCompositeMarkupNodes();
		_markupMeshes.Clear();
		_markupLevel = levelName;

		if (markup != null)
		{
			foreach (SyncedCompositeMarkup entry in markup)
			{
				ArrayMesh mesh = BuildMarkupMesh(entry);
				if (mesh != null)
					_markupMeshes[entry.composite] = mesh;
			}
		}

		if (_content.Loaded)
			RefreshCompositeMarkup();
		ViewerLog.Print("[Markup] " + _markupMeshes.Count + " composite(s) with markup for " + levelName);
	}

	/// <summary>Another level's markup names composites this one doesn't have: it goes before that level is read.</summary>
	private void ForgetCompositeMarkupUnlessFor(string levelName)
	{
		static string Key(string name) => (name ?? "").Replace('\\', '/').Trim('/');
		if (_markupLevel != null && string.Equals(Key(_markupLevel), Key(levelName), StringComparison.OrdinalIgnoreCase))
			return;
		_markupMeshes.Clear();
		_markupLevel = null;
	}

	/// <summary>Markup onto every instance of a marked composite that hasn't got it (after a populate, or a new set).</summary>
	private void RefreshCompositeMarkup()
	{
		if (_markupMeshes.Count == 0)
			return;

		foreach (KeyValuePair<ShortGuid, List<Node3D>> entry in _compositeNodes)
		{
			if (!_markupMeshes.TryGetValue(entry.Key.AsUInt32, out ArrayMesh mesh))
				continue;
			for (int i = 0; i < entry.Value.Count; i++)
				AttachCompositeMarkup(entry.Value[i], mesh);
		}
	}

	/// <summary>An instance spawned outside a populate (an add, an undo) gets its markup straight away.</summary>
	private void AttachCompositeMarkup(Composite composite, Node3D instanceNode)
	{
		if (_isBulkPopulating || composite == null || _markupMeshes.Count == 0)
			return;
		if (_markupMeshes.TryGetValue(composite.shortGUID.AsUInt32, out ArrayMesh mesh))
			AttachCompositeMarkup(instanceNode, mesh);
	}

	private void AttachCompositeMarkup(Node3D instanceNode, ArrayMesh mesh)
	{
		if (instanceNode == null || !GodotObject.IsInstanceValid(instanceNode) || instanceNode.IsQueuedForDeletion())
			return;

		bool visible = RenderFilters.IsSceneFilterEnabled(SceneFilterKind.Markup);
		MeshInstance3D existing = instanceNode.GetNodeOrNull<MeshInstance3D>(MarkupNodePath);
		if (existing != null && !existing.IsQueuedForDeletion())
		{
			if (existing.Mesh == mesh)
			{
				existing.Visible = visible;
				return;
			}
			FreeReleasingName(existing);
		}

		MeshInstance3D markup = new MeshInstance3D
		{
			Name = MarkupNodeName,
			Mesh = mesh,
			Visible = visible,
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(markup);
		//Keeps the pick walk, focus dimming and zone tint off it: it is the viewport's, not the level's
		markup.AddToGroup(LevelViewerPick.SceneFilterGroupName);
		instanceNode.AddChild(markup);
	}

	/// <summary>The Markup filter was switched: show or hide what is drawn.</summary>
	private void ApplyCompositeMarkupVisibility()
	{
		bool visible = RenderFilters.IsSceneFilterEnabled(SceneFilterKind.Markup);
		ForEachCompositeMarkupNode(node => node.Visible = visible);
	}

	private void RemoveCompositeMarkupNodes()
	{
		ForEachCompositeMarkupNode(FreeReleasingName);
	}

	private void ForEachCompositeMarkupNode(Action<MeshInstance3D> visit)
	{
		if (_markupMeshes.Count == 0)
			return;

		foreach (KeyValuePair<ShortGuid, List<Node3D>> entry in _compositeNodes)
		{
			if (!_markupMeshes.ContainsKey(entry.Key.AsUInt32))
				continue;
			for (int i = 0; i < entry.Value.Count; i++)
			{
				Node3D instance = entry.Value[i];
				if (instance == null || !GodotObject.IsInstanceValid(instance))
					continue;
				MeshInstance3D node = instance.GetNodeOrNull<MeshInstance3D>(MarkupNodePath);
				if (node != null && !node.IsQueuedForDeletion())
					visit(node);
			}
		}
	}

	/// <summary>Whether a node is composite markup (left out of previews and anything else that is the level's own).</summary>
	internal static bool IsCompositeMarkupNode(Node node)
	{
		return node is MeshInstance3D && node.Name == MarkupNodeName;
	}

	/* One surface per colour; positions converted to Godot's handedness (Z flip only, as the level's meshes are). The shader
	   works its facet shading out from screen-space derivatives and draws both sides, so no normals are needed. */
	private static ArrayMesh BuildMarkupMesh(SyncedCompositeMarkup entry)
	{
		if (entry?.surfaces == null || entry.surfaces.Count == 0)
			return null;

		ArrayMesh mesh = new ArrayMesh { ResourceName = "OpenCAGE_Markup " + entry.composite };
		foreach (SyncedMarkupSurface surface in entry.surfaces)
		{
			if (surface?.vertices == null || surface.indices == null || surface.vertices.Length < 9 || surface.indices.Length < 3)
				continue;

			int vertexCount = surface.vertices.Length / 3;
			Vector3[] vertices = new Vector3[vertexCount];
			for (int i = 0; i < vertexCount; i++)
				vertices[i] = CathodeCoordinates.PositionToGodot(new Vector3(surface.vertices[i * 3], surface.vertices[i * 3 + 1], surface.vertices[i * 3 + 2]));

			List<int> indices = new List<int>(surface.indices.Length);
			for (int i = 0; i + 2 < surface.indices.Length; i += 3)
			{
				int a = surface.indices[i], b = surface.indices[i + 1], c = surface.indices[i + 2];
				if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount)
					continue;
				indices.Add(a);
				indices.Add(b);
				indices.Add(c);
			}
			if (indices.Count == 0)
				continue;

			Godot.Collections.Array arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = vertices;
			arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, AlienSceneMaterials.GetMarkupMaterial(new Color(surface.colour_r, surface.colour_g, surface.colour_b)));
		}

		return mesh.GetSurfaceCount() == 0 ? null : mesh;
	}
}
