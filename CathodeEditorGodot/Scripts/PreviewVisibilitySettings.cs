using System;
using System.Collections.Generic;

/// <summary>
/// Level Viewer settings synced from OpenCAGE (nested script entity preview visibility).
/// </summary>
public static class PreviewVisibilitySettings
{
    public static bool HideNestedScriptEntities { get; set; }

    public static bool HighlightAliases { get; set; } = true;

    public enum DeepSelectModeKind
    {
        None,
        /// <summary>LMB aliases one composite level deeper per click on the same target; Ctrl+MMB steps in one composite at a time.</summary>
        DeepSelect,
        /// <summary>LMB creates/selects aliases to the deepest picked entity; Ctrl+MMB drills the full hierarchy.</summary>
        AdvancedDeepSelect,
    }

    public static DeepSelectModeKind DeepSelectMode { get; set; }

    /// <summary>
    /// The composite OpenCAGE is currently viewing (not necessarily the root composite loaded in the scene).
    /// </summary>
    public static uint ActiveCompositeId { get; set; }

    /// <summary>
    /// Entity IDs stepped through to reach the active composite instance (empty at the loaded root).
    /// </summary>
    public static uint[] ActiveInstanceEntityPath { get; private set; } = Array.Empty<uint>();

    public static void SyncFromEditorPath(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        ActiveInstanceEntityPath = BuildInstanceEntityPath(pathEntities, pathComposites, entitySelected);
    }

    public static uint[] BuildInstanceEntityPath(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        if (pathEntities == null || pathComposites == null || pathComposites.Count == 0 || pathEntities.Count == 0)
            return Array.Empty<uint>();

        int drillEntityCount = entitySelected && pathEntities.Count == pathComposites.Count
            ? pathEntities.Count - 1
            : pathEntities.Count;

        if (drillEntityCount <= 0)
            return Array.Empty<uint>();

        uint[] path = new uint[drillEntityCount];
        for (int i = 0; i < drillEntityCount; i++)
            path[i] = pathEntities[i];
        return path;
    }

    public static bool InstancePathsEqual(uint[] left, uint[] right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null)
            return left == right;

        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }
}
