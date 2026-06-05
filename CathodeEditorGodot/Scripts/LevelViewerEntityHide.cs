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
