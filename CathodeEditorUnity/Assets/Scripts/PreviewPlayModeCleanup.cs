#if UNITY_EDITOR && !LOCAL_DEV
using UnityEditor;
using UnityEngine;

/// <summary>
/// Render-filter preview objects use HideFlags.DontSave and can outlive their parent when play mode ends.
/// </summary>
[InitializeOnLoad]
public static class PreviewPlayModeCleanup
{
    static PreviewPlayModeCleanup()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingPlayMode)
            return;

        PreviewVisualUtility.CleanupAllFunctionEntityPreviews();
    }
}
#endif
