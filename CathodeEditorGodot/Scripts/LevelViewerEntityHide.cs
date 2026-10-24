using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Temporary entity hides scoped to the active composite + instance drill path (cleared on composite navigation).
/// </summary>
public static class LevelViewerEntityHide
{
	private sealed class HiddenEntry
	{
		public Node3D VisualRoot;
		public bool WasVisible;
		public readonly List<Node3D> SuppressedPickOwners = new();
	}

	private static uint _scopeCompositeId;
	private static uint[] _scopeInstancePath = Array.Empty<uint>();
	private static readonly List<HiddenEntry> _hiddenEntries = new();

	public static bool HasAny => _hiddenEntries.Count > 0;

	public static void SyncCompositeScope(uint activeCompositeId, uint[] instancePath)
	{
		uint[] path = instancePath ?? Array.Empty<uint>();
		if (_scopeCompositeId == activeCompositeId
			&& PreviewVisibilitySettings.InstancePathsEqual(_scopeInstancePath, path))
		{
			return;
		}

		ClearAll();
		_scopeCompositeId = activeCompositeId;
		_scopeInstancePath = (uint[])path.Clone();
	}

	public static bool TryHide(Node3D visualRoot)
	{
		if (visualRoot == null || !GodotObject.IsInstanceValid(visualRoot))
			return false;

		if (IsHidden(visualRoot))
			return false;

		HiddenEntry entry = new HiddenEntry
		{
			VisualRoot = visualRoot,
			WasVisible = visualRoot.Visible,
		};
		visualRoot.Visible = false;

		LevelViewerPick.ForEachPickOwner((owner, meshes) =>
		{
			if (!ShouldSuppressPickOwner(owner, visualRoot, meshes))
				return;

			LevelViewerPick.SetOwnerSuppressed(owner, true);
			entry.SuppressedPickOwners.Add(owner);
		});

		_hiddenEntries.Add(entry);
		LevelViewerPick.InvalidateScopedPickables();
		return true;
	}

	public static bool IsHidden(Node3D visualRoot)
	{
		if (visualRoot == null)
			return false;

		ulong visualId = visualRoot.GetInstanceId();
		for (int i = 0; i < _hiddenEntries.Count; i++)
		{
			HiddenEntry entry = _hiddenEntries[i];
			if (entry.VisualRoot != null
				&& GodotObject.IsInstanceValid(entry.VisualRoot)
				&& entry.VisualRoot.GetInstanceId() == visualId)
			{
				return true;
			}
		}

		return false;
	}

	public static void ClearAll()
	{
		for (int i = 0; i < _hiddenEntries.Count; i++)
		{
			HiddenEntry entry = _hiddenEntries[i];
			if (entry.VisualRoot != null && GodotObject.IsInstanceValid(entry.VisualRoot))
				entry.VisualRoot.Visible = entry.WasVisible;

			for (int j = 0; j < entry.SuppressedPickOwners.Count; j++)
				LevelViewerPick.SetOwnerSuppressed(entry.SuppressedPickOwners[j], false);
		}

		_hiddenEntries.Clear();
		LevelViewerPick.InvalidateScopedPickables();
	}

	/// <summary>Where each hidden entity is under <paramref name="root"/> (the content root), for <see cref="Restore"/>.</summary>
	public static List<NodePath> CapturePaths(Node3D root)
	{
		List<NodePath> paths = new List<NodePath>();
		if (root == null || !GodotObject.IsInstanceValid(root))
			return paths;

		for (int i = 0; i < _hiddenEntries.Count; i++)
		{
			Node3D visualRoot = _hiddenEntries[i].VisualRoot;
			//One freed this frame has given its name up (AlienScene.FreeReleasingName): its path names nothing in the rebuilt scene
			if (visualRoot != null && GodotObject.IsInstanceValid(visualRoot) && !visualRoot.IsQueuedForDeletion() && root.IsAncestorOf(visualRoot))
				paths.Add(root.GetPathTo(visualRoot));
		}
		return paths;
	}

	/// <summary>
	/// The scene was built again with the scope unchanged (a rebuild of the composite on screen, the view put back after
	/// previews): the hides go on the new nodes at the same paths, and the old entries - their nodes went with the old
	/// scene, as did the pick registry - are dropped. Returns how many came back.
	/// </summary>
	public static int Restore(Node3D root, List<NodePath> paths)
	{
		_hiddenEntries.Clear();
		int restored = 0;
		if (root != null && GodotObject.IsInstanceValid(root) && paths != null)
		{
			for (int i = 0; i < paths.Count; i++)
			{
				if (root.GetNodeOrNull(paths[i]) is Node3D node && TryHide(node))
					restored++;
			}
		}
		LevelViewerPick.InvalidateScopedPickables();
		return restored;
	}

	private static bool ShouldSuppressPickOwner(
		Node3D owner,
		Node3D visualRoot,
		IReadOnlyList<MeshInstance3D> meshes)
	{
		if (owner == null || visualRoot == null)
			return false;

		if (owner == visualRoot || owner.IsAncestorOf(visualRoot))
			return true;

		for (int i = 0; i < meshes.Count; i++)
		{
			MeshInstance3D mesh = meshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (mesh == visualRoot || visualRoot.IsAncestorOf(mesh))
				return true;
		}

		return false;
	}
}
