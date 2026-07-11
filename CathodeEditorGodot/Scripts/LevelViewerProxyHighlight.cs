using CATHODE;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Blue additive overlay on entities targeted by proxies in the active composite.
/// Only applies when stepped into a nested composite instance below Commands.EntryPoints[0].
/// </summary>
public static class LevelViewerProxyHighlight
{
	public static readonly Color HighlightBlue = new(0.3f, 0.55f, 1f, 1f);

	private static readonly EntityHighlightState _state =
		new EntityHighlightState(LevelViewerHighlightOverlay.HighlightOverlayMode.Proxy);

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

		if (!PreviewVisibilitySettings.HighlightProxies || !PreviewVisibilitySettings.IsSteppedDownFromLevelRoot())
		{
			_state.MarkRebuildFailed();
			return;
		}

		HashSet<ulong> tintedMeshIds = new HashSet<ulong>();
		scene.ForEachProxyInActiveComposite((ownerComposite, proxy) =>
		{
			if (!scene.TryGetEntitySceneNodes(ownerComposite.shortGUID, proxy.shortGUID, out List<Node3D> proxyNodes))
				return;

			for (int i = 0; i < proxyNodes.Count; i++)
			{
				if (proxyNodes[i] is not EntityOverride proxyOverride)
					continue;

				if (!scene.TryResolveProxyPointedSceneNode(
						proxyOverride,
						proxy,
						out Node3D pointedNode,
						preferCached: true))
				{
					continue;
				}

				_state.ApplyToNode(pointedNode, tintedMeshIds);
			}
		});

		_state.MarkRebuilt(activeCompositeId);
	}

	public static void SyncWithSelection() => _state.ReapplyIfActive();

	public static void Clear() => _state.Clear();

	public static void ReleaseNode(Node3D root) => _state.ReleaseNode(root);
}
