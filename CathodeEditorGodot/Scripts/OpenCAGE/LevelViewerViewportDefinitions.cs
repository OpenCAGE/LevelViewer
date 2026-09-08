namespace OpenCAGE.UnityConnection
{
    public enum LevelViewerDeepSelectMode
    {
        Regular = 0,
        Deep = 1,
        AdvancedDeep = 2,
    }

    public enum LevelViewerGizmoMode
    {
        None = 0,
        TranslateWorld = 1,
        RotateLocal = 2,
        RotateWorld = 3,
        TranslateLocal = 4,
    }

    /// <summary>How the viewer marks the selected entity.</summary>
    public enum LevelViewerHighlightMode
    {
        Green = 0,
        Wireframe = 1,
        WireframeTransparent = 2,
        None = 3,
    }

    public static class LevelViewerViewportDefinitions
    {
        public static string FormatHighlightModeLabel(LevelViewerHighlightMode mode)
        {
            switch (mode)
            {
                case LevelViewerHighlightMode.Wireframe:
                    return "Wireframe";
                case LevelViewerHighlightMode.WireframeTransparent:
                    return "Wireframe Transparent";
                case LevelViewerHighlightMode.None:
                    return "None";
                default:
                    return "Green";
            }
        }

        public static LevelViewerHighlightMode NormalizeHighlightMode(int value)
        {
            if (value < 0 || value > (int)LevelViewerHighlightMode.None)
                return LevelViewerHighlightMode.Green;
            return (LevelViewerHighlightMode)value;
        }

        public static string FormatSelectionModeLabel(LevelViewerDeepSelectMode mode)
        {
            switch (mode)
            {
                case LevelViewerDeepSelectMode.Deep:
                    return "Deep";
                case LevelViewerDeepSelectMode.AdvancedDeep:
                    return "Advanced Deep";
                default:
                    return "Regular";
            }
        }

        public static string FormatTransformModeLabel(LevelViewerGizmoMode mode)
        {
            switch (mode)
            {
                case LevelViewerGizmoMode.TranslateLocal:
                    return "Translate (Local)";
                case LevelViewerGizmoMode.TranslateWorld:
                    return "Translate (World)";
                case LevelViewerGizmoMode.RotateLocal:
                    return "Rotate (Local)";
                case LevelViewerGizmoMode.RotateWorld:
                    return "Rotate (World)";
                default:
                    return "None";
            }
        }

        /* Alt and a number, rather than a bare number: 1-5 and 8-0 are already the gizmo and selection
           modes, and Shift is the camera's speed modifier, so it cannot carry a chord of its own. */
        public static string GetHighlightModeShortcut(LevelViewerHighlightMode mode)
        {
            switch (mode)
            {
                case LevelViewerHighlightMode.Green:
                    return "Alt+1";
                case LevelViewerHighlightMode.Wireframe:
                    return "Alt+2";
                case LevelViewerHighlightMode.WireframeTransparent:
                    return "Alt+3";
                case LevelViewerHighlightMode.None:
                    return "Alt+4";
                default:
                    return string.Empty;
            }
        }

        public static string GetSelectionModeShortcut(LevelViewerDeepSelectMode mode)
        {
            switch (mode)
            {
                case LevelViewerDeepSelectMode.Deep:
                    return "8";
                case LevelViewerDeepSelectMode.AdvancedDeep:
                    return "9";
                default:
                    return "0";
            }
        }

        public static string GetGizmoModeShortcut(LevelViewerGizmoMode mode)
        {
            switch (mode)
            {
                case LevelViewerGizmoMode.TranslateWorld:
                    return "1";
                case LevelViewerGizmoMode.TranslateLocal:
                    return "2";
                case LevelViewerGizmoMode.RotateWorld:
                    return "3";
                case LevelViewerGizmoMode.RotateLocal:
                    return "4";
                case LevelViewerGizmoMode.None:
                    return "5";
                default:
                    return string.Empty;
            }
        }

        public static LevelViewerDeepSelectMode NormalizeDeepSelectMode(int value)
        {
            if (value < 0 || value > (int)LevelViewerDeepSelectMode.AdvancedDeep)
                return LevelViewerDeepSelectMode.Regular;
            return (LevelViewerDeepSelectMode)value;
        }

        public static LevelViewerGizmoMode NormalizeGizmoMode(int value)
        {
            if (value < 0 || value > (int)LevelViewerGizmoMode.TranslateLocal)
                return LevelViewerGizmoMode.None;
            return (LevelViewerGizmoMode)value;
        }
    }
}
