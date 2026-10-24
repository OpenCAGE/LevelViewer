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

    /// <summary>How the viewer smooths edges (Options > Viewport > Anti-aliasing).</summary>
    public enum LevelViewerAntialiasMode
    {
        Off = 0,
        //Post filters over the finished frame: edges and the alpha-tested cut-outs (grates, foliage) alike
        Fxaa = 1,
        Smaa = 2,
        //Several samples per pixel at geometry edges: the sharpest, dearest on dense views
        Msaa2x = 3,
        Msaa4x = 4,
        Msaa8x = 5,
    }

    /// <summary>How far the viewer draws each model, by its size (Options > Viewport > Draw Distance).</summary>
    public enum LevelViewerDrawDistance
    {
        Short = 0,
        Medium = 1,
        Long = 2,
        VeryLong = 3,
        Unlimited = 4,
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
        /// <summary>What a new install starts with, and what a viewer takes from an OpenCAGE too old to say.</summary>
        public const LevelViewerAntialiasMode DefaultAntialiasMode = LevelViewerAntialiasMode.Smaa;

        public static LevelViewerAntialiasMode NormalizeAntialiasMode(int value)
        {
            if (value < 0 || value > (int)LevelViewerAntialiasMode.Msaa8x)
                return DefaultAntialiasMode;
            return (LevelViewerAntialiasMode)value;
        }

        /// <summary>What a new install starts with, and what a viewer takes from an OpenCAGE too old to say.</summary>
        public const LevelViewerDrawDistance DefaultDrawDistance = LevelViewerDrawDistance.Medium;

        public static LevelViewerDrawDistance NormalizeDrawDistance(int value)
        {
            if (value < 0 || value > (int)LevelViewerDrawDistance.Unlimited)
                return DefaultDrawDistance;
            return (LevelViewerDrawDistance)value;
        }

        /* A model is drawn out to this many times its radius, and never less than the minimum (metres): a 1 m prop to 50 m at
           Medium, a 10 m wall to half a kilometre. Unlimited draws everything wherever the camera is (0, 0). */
        public static void GetDrawDistanceFactors(LevelViewerDrawDistance preset, out float perRadius, out float minimum)
        {
            switch (preset)
            {
                case LevelViewerDrawDistance.Short:
                    perRadius = 50f; minimum = 10f;
                    break;
                case LevelViewerDrawDistance.Long:
                    perRadius = 200f; minimum = 30f;
                    break;
                case LevelViewerDrawDistance.VeryLong:
                    perRadius = 400f; minimum = 60f;
                    break;
                case LevelViewerDrawDistance.Unlimited:
                    perRadius = 0f; minimum = 0f;
                    break;
                default:
                    perRadius = 100f; minimum = 15f;
                    break;
            }
        }

        public static string FormatAntialiasModeLabel(LevelViewerAntialiasMode mode)
        {
            switch (mode)
            {
                case LevelViewerAntialiasMode.Off:
                    return "Off";
                case LevelViewerAntialiasMode.Fxaa:
                    return "FXAA";
                case LevelViewerAntialiasMode.Msaa2x:
                    return "MSAA 2x";
                case LevelViewerAntialiasMode.Msaa4x:
                    return "MSAA 4x";
                case LevelViewerAntialiasMode.Msaa8x:
                    return "MSAA 8x";
                default:
                    return "SMAA";
            }
        }

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
