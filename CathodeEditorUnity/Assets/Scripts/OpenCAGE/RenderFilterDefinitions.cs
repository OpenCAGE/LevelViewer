using CATHODE.Scripting;
using System.Collections.Generic;

namespace OpenCAGE
{
    public enum RenderPreviewKind
    {
        Box,
        Sound,
        PositionMarker,
        SoundEnvironmentMarker,
        LightReference,
        Character,
        ParticleEmitter,
    }

    /// <summary>
    /// Hardcoded function types with Level Viewer previews and their filter colours.
    /// Canonical copy lives in the Unity project; OpenCAGE links this file in the .csproj.
    /// </summary>
    public static class RenderFilterDefinitions
    {
        public const float PreviewAlpha = 0.24f;

        public readonly struct Definition
        {
            public readonly FunctionType FunctionType;
            public readonly string DisplayName;
            public readonly RenderPreviewKind PreviewKind;
            public readonly float R;
            public readonly float G;
            public readonly float B;

            public Definition(FunctionType functionType, string displayName, RenderPreviewKind previewKind, float r, float g, float b)
            {
                FunctionType = functionType;
                DisplayName = displayName;
                PreviewKind = previewKind;
                R = r;
                G = g;
                B = b;
            }

            public uint FunctionTypeUInt => (uint)FunctionType;
        }

        public readonly struct RenderFilterColor
        {
            public readonly float R;
            public readonly float G;
            public readonly float B;
            public readonly float A;

            public RenderFilterColor(float r, float g, float b, float a = PreviewAlpha)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }
        }

        public static readonly Definition[] All = new Definition[]
        {
            // Box volumes
            new Definition(FunctionType.Box, "Box", RenderPreviewKind.Box, 0.85f, 0.85f, 0.85f),
            new Definition(FunctionType.CameraCollisionBox, "Camera Collision Box", RenderPreviewKind.Box, 0.6f, 0.75f, 0.9f),
            new Definition(FunctionType.CollisionBarrier, "Collision Barrier", RenderPreviewKind.Box, 0.95f, 0.4f, 0.3f),
            new Definition(FunctionType.CoverExclusionArea, "Cover Exclusion Area", RenderPreviewKind.Box, 1f, 0.6f, 0.2f),
            new Definition(FunctionType.FogBox, "Fog Box", RenderPreviewKind.Box, 0.7f, 0.65f, 0.85f),
            new Definition(FunctionType.NPC_AreaBox, "NPC Area Box", RenderPreviewKind.Box, 1f, 0.95f, 0.3f),
            new Definition(FunctionType.NavMeshArea, "Nav Mesh Area", RenderPreviewKind.Box, 0.25f, 0.55f, 1f),
            new Definition(FunctionType.NavMeshBarrier, "Nav Mesh Barrier", RenderPreviewKind.Box, 0.2f, 0.4f, 0.85f),
            new Definition(FunctionType.NavMeshExclusionArea, "Nav Mesh Exclusion Area", RenderPreviewKind.Box, 0.85f, 0.25f, 0.35f),
            new Definition(FunctionType.NavMeshWalkablePlatform, "Nav Mesh Walkable Platform", RenderPreviewKind.Box, 0.45f, 1f, 0.55f),
            new Definition(FunctionType.PlayerTriggerBox, "Player Trigger Box", RenderPreviewKind.Box, 0.25f, 0.85f, 1f),
            new Definition(FunctionType.PlayerUseTriggerBox, "Player Use Trigger Box", RenderPreviewKind.Box, 0.3f, 1f, 0.45f),
            new Definition(FunctionType.ProjectiveDecal, "Projective Decal", RenderPreviewKind.Box, 0.85f, 0.4f, 0.75f),
            new Definition(FunctionType.SimpleRefraction, "Simple Refraction", RenderPreviewKind.Box, 0.35f, 0.85f, 0.8f),
            new Definition(FunctionType.SimpleWater, "Simple Water", RenderPreviewKind.Box, 0.3f, 0.6f, 1f),
            new Definition(FunctionType.SoundBarrier, "Sound Barrier", RenderPreviewKind.Box, 0.75f, 0.35f, 1f),
            new Definition(FunctionType.SoundEnvironmentZone, "Sound Environment Zone", RenderPreviewKind.Box, 0.55f, 0.45f, 0.95f),
            new Definition(FunctionType.SpottingExclusionArea, "Spotting Exclusion Area", RenderPreviewKind.Box, 1f, 0.35f, 0.35f),
            new Definition(FunctionType.SurfaceEffectBox, "Surface Effect Box", RenderPreviewKind.Box, 0.5f, 0.9f, 0.4f),
            new Definition(FunctionType.UiSelectionBox, "UI Selection Box", RenderPreviewKind.Box, 0.95f, 0.9f, 0.5f),

            // Other previews
            new Definition(FunctionType.Sound, "Sound", RenderPreviewKind.Sound, 0.2f, 0.45f, 1f),
            new Definition(FunctionType.PositionMarker, "Position Marker", RenderPreviewKind.PositionMarker, 1f, 0.55f, 0.1f),
            new Definition(FunctionType.SoundEnvironmentMarker, "Sound Environment Marker", RenderPreviewKind.SoundEnvironmentMarker, 0.12f, 0.22f, 0.65f),
            new Definition(FunctionType.LightReference, "Light Reference", RenderPreviewKind.LightReference, 1f, 0.92f, 0.35f),
            new Definition(FunctionType.Character, "Character", RenderPreviewKind.Character, 0.25f, 0.85f, 0.35f),
            new Definition(FunctionType.ParticleEmitterReference, "Particle Emitter Reference", RenderPreviewKind.ParticleEmitter, 0.35f, 0.75f, 1f),
            new Definition(FunctionType.RibbonEmitterReference, "Ribbon Emitter Reference", RenderPreviewKind.ParticleEmitter, 0.75f, 0.35f, 1f),
            new Definition(FunctionType.GPU_PFXEmitterReference, "GPU PFX Emitter Reference", RenderPreviewKind.ParticleEmitter, 1f, 0.4f, 0.75f),
        };

        private static readonly HashSet<FunctionType> SupportedTypes = BuildSupportedTypes();
        private static readonly Dictionary<FunctionType, Definition> DefinitionsByType = BuildDefinitionsByType();
        private static readonly Dictionary<FunctionType, RenderFilterColor> ColorsByType = BuildColors();

        public static bool IsSupported(FunctionType functionType)
        {
            return SupportedTypes.Contains(functionType);
        }

        public static bool TryGetDefinition(FunctionType functionType, out Definition definition)
        {
            return DefinitionsByType.TryGetValue(functionType, out definition);
        }

        public static RenderPreviewKind GetPreviewKind(FunctionType functionType)
        {
            if (DefinitionsByType.TryGetValue(functionType, out Definition definition))
                return definition.PreviewKind;
            return RenderPreviewKind.Box;
        }

        public static RenderFilterColor GetColor(FunctionType functionType)
        {
            if (ColorsByType.TryGetValue(functionType, out RenderFilterColor color))
                return color;
            return new RenderFilterColor(0.85f, 0.85f, 0.85f);
        }

        private static HashSet<FunctionType> BuildSupportedTypes()
        {
            HashSet<FunctionType> supported = new HashSet<FunctionType>();
            for (int i = 0; i < All.Length; i++)
                supported.Add(All[i].FunctionType);
            return supported;
        }

        private static Dictionary<FunctionType, Definition> BuildDefinitionsByType()
        {
            Dictionary<FunctionType, Definition> definitions = new Dictionary<FunctionType, Definition>();
            for (int i = 0; i < All.Length; i++)
                definitions[All[i].FunctionType] = All[i];
            return definitions;
        }

        private static Dictionary<FunctionType, RenderFilterColor> BuildColors()
        {
            Dictionary<FunctionType, RenderFilterColor> colors = new Dictionary<FunctionType, RenderFilterColor>();
            for (int i = 0; i < All.Length; i++)
            {
                Definition definition = All[i];
                colors[definition.FunctionType] = new RenderFilterColor(definition.R, definition.G, definition.B);
            }
            return colors;
        }
    }
}
