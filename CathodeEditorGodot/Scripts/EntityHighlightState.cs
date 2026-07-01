using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Shared additive-overlay highlight state machine used by both alias (orange) and proxy (blue)
/// highlights. Owns the saved-overlay bookkeeping, cache validity, and selection-aware apply/release
/// logic. Callers supply the per-mode rebuild traversal and feed resolved nodes into
/// <see cref="ApplyToNode"/>.
/// </summary>
internal sealed class EntityHighlightState
{
	private readonly LevelViewerHighlightOverlay.HighlightOverlayMode _mode;
	private readonly Dictionary<MeshInstance3D, Material> _savedOverlays = new();
	private readonly List<MeshInstance3D> _highlightMeshes = new();
	private readonly List<MeshInstance3D> _meshCollectBuffer = new();

	private uint _cachedActiveCompositeId;
	private uint[] _cachedInstancePath = Array.Empty<uint>();
	private bool _cacheValid;

	public EntityHighlightState(LevelViewerHighlightOverlay.HighlightOverlayMode mode)
	{
		_mode = mode;
	}

	public bool NeedsRebuild(uint activeCompositeId)
	{
		if (!_cacheValid)
			return true;

		if (_cachedActiveCompositeId != activeCompositeId)
			return true;

		return !PreviewVisibilitySettings.InstancePathsEqual(
			_cachedInstancePath,
			PreviewVisibilitySettings.ActiveInstanceEntityPath);
	}

	public void InvalidateCache() => _cacheValid = false;

	/// <summary>Records a completed rebuild so <see cref="NeedsRebuild"/> can short-circuit.</summary>
	public void MarkRebuilt(uint activeCompositeId)
	{
		_cachedActiveCompositeId = activeCompositeId;
		_cachedInstancePath = (uint[])(PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>()).Clone();
		_cacheValid = true;
	}

	/// <summary>Marks the cache invalid after a rebuild produced no highlights.</summary>
	public void MarkRebuildFailed() => _cacheValid = false;

	public void Clear()
	{
		LevelViewerHighlightOverlay.RestoreOverlays(_savedOverlays);
		_highlightMeshes.Clear();
		_cacheValid = false;
	}

	/// <summary>Restores the overlay under <paramref name="root"/> so selection green can take over.</summary>
	public void ReleaseNode(Node3D root)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		for (int i = _highlightMeshes.Count - 1; i >= 0; i--)
		{
			MeshInstance3D mesh = _highlightMeshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (mesh != root && !root.IsAncestorOf(mesh))
				continue;

			ReleaseMeshForSelection(mesh);
		}
	}

	/// <summary>Re-applies the cached overlay while skipping the current selection subtree.</summary>
	public void ReapplyIfActive()
	{
		for (int i = 0; i < _highlightMeshes.Count; i++)
		{
			MeshInstance3D mesh = _highlightMeshes[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (_savedOverlays.ContainsKey(mesh))
				continue;

			if (IsMeshUnderSelection(mesh))
				continue;

			ApplyMeshHighlight(mesh);
		}
	}

	/// <summary>Collects meshes under <paramref name="root"/> and applies the overlay to new ones.</summary>
	public void ApplyToNode(Node3D root, HashSet<ulong> tintedMeshIds)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		_meshCollectBuffer.Clear();
		PreviewVisualUtility.CollectMeshInstances(root, _meshCollectBuffer);
		for (int i = 0; i < _meshCollectBuffer.Count; i++)
		{
			MeshInstance3D mesh = _meshCollectBuffer[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			ulong meshId = mesh.GetInstanceId();
			if (tintedMeshIds.Contains(meshId))
				continue;

			tintedMeshIds.Add(meshId);
			if (!_highlightMeshes.Contains(mesh))
				_highlightMeshes.Add(mesh);

			if (_savedOverlays.ContainsKey(mesh) || IsMeshUnderSelection(mesh))
				continue;

			ApplyMeshHighlight(mesh);
		}
	}

	private void ReleaseMeshForSelection(MeshInstance3D mesh)
	{
		if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			return;

		if (_savedOverlays.TryGetValue(mesh, out Material saved))
			mesh.MaterialOverlay = saved;

		_savedOverlays.Remove(mesh);
	}

	private static bool IsMeshUnderSelection(MeshInstance3D mesh)
	{
		Node current = mesh;
		while (current != null)
		{
			if (LevelViewerSelection.IsUnderSelection(current))
				return true;

			current = current.GetParent();
		}

		return false;
	}

	private void ApplyMeshHighlight(MeshInstance3D meshInstance)
	{
		LevelViewerHighlightOverlay.TryApplyOverlay(meshInstance, _savedOverlays, _mode);
	}
}
