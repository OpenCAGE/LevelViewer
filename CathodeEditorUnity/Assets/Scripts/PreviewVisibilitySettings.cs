/// <summary>
/// Level Viewer settings synced from OpenCAGE (nested script entity preview visibility).
/// </summary>
public static class PreviewVisibilitySettings
{
    public static bool HideNestedScriptEntities { get; set; }

    /// <summary>
    /// The composite OpenCAGE is currently viewing (not necessarily the root composite loaded in Unity).
    /// </summary>
    public static uint ActiveCompositeId { get; set; }
}
