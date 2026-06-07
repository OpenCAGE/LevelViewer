using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Orange additive overlay on instances targeted by parameterized aliases in the active composite.
/// Uses the same MaterialOverlay path as selection highlights.
/// </summary>
public static class LevelViewerAliasHighlight
{
	public static readonly Color HighlightOrange = new(1f, 0.55f, 0.15f, 1f);

	private static readonly Dictionary<MeshInstance3D, Material> _savedOverlays = new();
	private static readonly List<MeshInstance3D> _highlightMeshes = new();
	private static readonly List<MeshInstance3D> _meshCollectBuffer = new();

	private static uint _cachedActiveCompositeId;
	private static uint[] _cachedInstancePath = Array.Empty<uint>();
	private static bool _cacheValid;

	public static bool NeedsRebuild(uint activeCompositeId)
	{
		if (!_cacheValid)
			return true;

		if (_cachedActiveCompositeId != activeCompositeId)
			return true;

		return !PreviewVisibilitySettings.InstancePathsEqual(
			_cachedInstancePath,
			PreviewVisibilitySettings.ActiveInstanceEntityPath);
	}

	public static void InvalidateCache() => _cacheValid = false;

	public static void Rebuild(AlienScene scene, Commands commands, uint activeCompositeId)
	{
		Clear();
		if (scene == null || commands == null || activeCompositeId == 0)
		{
			_cacheValid = false;
			return;
		}

		Node3D contentRoot = scene.ParentNode;
		if (contentRoot == null)
		{
			_cacheValid = false;
			return;
		}

		HashSet<ulong> tintedMeshIds = new HashSet<ulong>();
		scene.ForEachParameterizedAliasInActiveComposite((ownerComposite, alias) =>
		{
			if (!scene.TryGetEntitySceneNodes(ownerComposite.shortGUID, alias.shortGUID, out List<Node3D> aliasNodes))
				return;

			for (int i = 0; i < aliasNodes.Count; i++)
			{
				if (aliasNodes[i] is not EntityOverride aliasOverride)
					continue;

				if (!LevelViewerCompositeFocus.IsPickOwnerInScope(aliasOverride, commands))
					continue;

				if (!scene.TryResolveAliasPointedSceneNode(
						aliasOverride,
						alias,
						ownerComposite,
						out Node3D pointedNode,
						preferCached: true))
				{
					continue;
				}

				if (!LevelViewerCompositeFocus.IsNodeInScope(pointedNode, contentRoot, commands))
					continue;

				ApplyToNode(pointedNode, tintedMeshIds);
			}
		});

		_cachedActiveCompositeId = activeCompositeId;
		_cachedInstancePath = (uint[])(PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>()).Clone();
		_cacheValid = true;
	}

	/// <summary>Re-applies cached orange overlay while skipping the current selection subtree.</summary>
	public static void SyncWithSelection() => ReapplyIfActive();

	public static void Clear()
	{
		LevelViewerHighlightOverlay.RestoreOverlays(_savedOverlays);
		_highlightMeshes.Clear();
		_cacheValid = false;
	}

	/// <summary>Restores alias overlay under <paramref name="root"/> so selection green can take over.</summary>
	public static void ReleaseNode(Node3D root)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		_meshCollectBuffer.Clear();
		CollectMeshes(root, _meshCollectBuffer);
		for (int i = 0; i < _meshCollectBuffer.Count; i++)
			ReleaseMeshForSelection(_meshCollectBuffer[i]);
	}

	public static void ReapplyIfActive()
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

	private static void ReleaseMeshForSelection(MeshInstance3D mesh)
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

	private static void ApplyToNode(Node3D root, HashSet<ulong> tintedMeshIds)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		_meshCollectBuffer.Clear();
		CollectMeshes(root, _meshCollectBuffer);
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

	private static void CollectMeshes(Node node, List<MeshInstance3D> meshes)
	{
		PreviewVisualUtility.CollectMeshInstances(node, meshes);
	}

	private static void ApplyMeshHighlight(MeshInstance3D meshInstance)
	{
		LevelViewerHighlightOverlay.TryApplyOverlay(meshInstance, _savedOverlays, aliasHighlight: true);
	}
}
