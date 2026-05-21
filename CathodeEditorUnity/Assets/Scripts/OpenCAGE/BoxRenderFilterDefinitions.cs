using CATHODE.Scripting;
using System.Collections.Generic;

namespace OpenCAGE
{
    /// <summary>
    /// Hardcoded box volume types rendered in the Level Viewer and their preview colours.
    /// Canonical copy lives in the Unity project; OpenCAGE links this file in the .csproj.
    /// </summary>
    public static class BoxRenderFilterDefinitions
    {
        public const float PreviewAlpha = 0.24f;

        public readonly struct Definition
        {
            public readonly FunctionType FunctionType;
            public readonly string DisplayName;
            public readonly float R;
            public readonly float G;
            public readonly float B;

            public Definition(FunctionType functionType, string displayName, float r, float g, float b)
            {
                FunctionType = functionType;
                DisplayName = displayName;
                R = r;
                G = g;
                B = b;
            }

            public uint FunctionTypeUInt => (uint)FunctionType;
        }

        public readonly struct BoxRenderFilterColor
        {
            public readonly float R;
            public readonly float G;
            public readonly float B;
            public readonly float A;

            public BoxRenderFilterColor(float r, float g, float b, float a = PreviewAlpha)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }
        }

        public static readonly Definition[] All = new Definition[]
        {
            new Definition(FunctionType.Box, "Box", 0.85f, 0.85f, 0.85f),
            new Definition(FunctionType.CameraCollisionBox, "Camera Collision Box", 0.6f, 0.75f, 0.9f),
            new Definition(FunctionType.CollisionBarrier, "Collision Barrier", 0.95f, 0.4f, 0.3f),
            new Definition(FunctionType.CoverExclusionArea, "Cover Exclusion Area", 1f, 0.6f, 0.2f),
            new Definition(FunctionType.FogBox, "Fog Box", 0.7f, 0.65f, 0.85f),
            new Definition(FunctionType.NPC_AreaBox, "NPC Area Box", 1f, 0.95f, 0.3f),
            new Definition(FunctionType.NavMeshArea, "Nav Mesh Area", 0.25f, 0.55f, 1f),
            new Definition(FunctionType.NavMeshBarrier, "Nav Mesh Barrier", 0.2f, 0.4f, 0.85f),
            new Definition(FunctionType.NavMeshExclusionArea, "Nav Mesh Exclusion Area", 0.85f, 0.25f, 0.35f),
            new Definition(FunctionType.NavMeshWalkablePlatform, "Nav Mesh Walkable Platform", 0.45f, 1f, 0.55f),
            new Definition(FunctionType.PlayerTriggerBox, "Player Trigger Box", 0.25f, 0.85f, 1f),
            new Definition(FunctionType.PlayerUseTriggerBox, "Player Use Trigger Box", 0.3f, 1f, 0.45f),
            new Definition(FunctionType.ProjectiveDecal, "Projective Decal", 0.85f, 0.4f, 0.75f),
            new Definition(FunctionType.SimpleRefraction, "Simple Refraction", 0.35f, 0.85f, 0.8f),
            new Definition(FunctionType.SimpleWater, "Simple Water", 0.3f, 0.6f, 1f),
            new Definition(FunctionType.SoundBarrier, "Sound Barrier", 0.75f, 0.35f, 1f),
            new Definition(FunctionType.SoundEnvironmentZone, "Sound Environment Zone", 0.55f, 0.45f, 0.95f),
            new Definition(FunctionType.SpottingExclusionArea, "Spotting Exclusion Area", 1f, 0.35f, 0.35f),
            new Definition(FunctionType.SurfaceEffectBox, "Surface Effect Box", 0.5f, 0.9f, 0.4f),
            new Definition(FunctionType.UiSelectionBox, "UI Selection Box", 0.95f, 0.9f, 0.5f),
        };

        private static readonly HashSet<FunctionType> SupportedTypes = BuildSupportedTypes();
        private static readonly Dictionary<FunctionType, BoxRenderFilterColor> ColorsByType = BuildColors();

        public static bool IsSupported(FunctionType functionType)
        {
            return SupportedTypes.Contains(functionType);
        }

        public static BoxRenderFilterColor GetColor(FunctionType functionType)
        {
            if (ColorsByType.TryGetValue(functionType, out BoxRenderFilterColor color))
                return color;
            return new BoxRenderFilterColor(0.85f, 0.85f, 0.85f);
        }

        private static HashSet<FunctionType> BuildSupportedTypes()
        {
            HashSet<FunctionType> supported = new HashSet<FunctionType>();
            for (int i = 0; i < All.Length; i++)
                supported.Add(All[i].FunctionType);
            return supported;
        }

        private static Dictionary<FunctionType, BoxRenderFilterColor> BuildColors()
        {
            Dictionary<FunctionType, BoxRenderFilterColor> colors = new Dictionary<FunctionType, BoxRenderFilterColor>();
            for (int i = 0; i < All.Length; i++)
            {
                Definition definition = All[i];
                colors[definition.FunctionType] = new BoxRenderFilterColor(definition.R, definition.G, definition.B);
            }
            return colors;
        }
    }
}
