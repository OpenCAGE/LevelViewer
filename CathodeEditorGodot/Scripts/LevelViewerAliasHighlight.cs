using CATHODE;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Orange additive overlay on instances targeted by parameterized aliases in the active composite.
/// Uses the same MaterialOverlay path as selection highlights.
/// </summary>
public static class LevelViewerAliasHighlight
{
	public static readonly Color HighlightOrange = new(1f, 0.55f, 0.15f, 1f);

	private static readonly EntityHighlightState _state =
		new EntityHighlightState(LevelViewerHighlightOverlay.HighlightOverlayMode.Alias);

	public static bool NeedsRebuild(uint activeCompositeId) => _state.NeedsRebuild(activeCompositeId);

	public static void InvalidateCache() => _state.InvalidateCache();

	public static void Rebuild(AlienScene scene, Commands commands, uint activeCompositeId)
	{
		_state.Clear();
		if (scene == null || commands == null || activeCompositeId == 0)
		{
			_state.MarkRebuildFailed();
			return;
		}

		Node3D contentRoot = scene.ParentNode;
		if (contentRoot == null)
		{
			_state.MarkRebuildFailed();
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

				_state.ApplyToNode(pointedNode, tintedMeshIds);
			}
		});

		_state.MarkRebuilt(activeCompositeId);
	}

	/// <summary>Re-applies cached orange overlay while skipping the current selection subtree.</summary>
	public static void SyncWithSelection() => _state.ReapplyIfActive();

	public static void Clear() => _state.Clear();

	/// <summary>Restores alias overlay under <paramref name="root"/> so selection green can take over.</summary>
	public static void ReleaseNode(Node3D root) => _state.ReleaseNode(root);
}
