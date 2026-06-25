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
        SplinePath,
        EnvironmentMap,
        SoundNetworkNode,
        SoundObject,
        CameraResource,
    }

    /// <summary>
    /// Hardcoded function types with Level Viewer previews and their filter colours.
    /// Canonical copy for Level Viewer and OpenCAGE (linked in OpenCAGE.csproj).
    /// </summary>
    public static class RenderFilterDefinitions
    {
        public const float PreviewAlpha = 0.24f;

        public readonly struct Definition
        {
            public readonly FunctionType FunctionType;
            public readonly RenderPreviewKind PreviewKind;
            public readonly float R;
            public readonly float G;
            public readonly float B;

            public Definition(FunctionType functionType, RenderPreviewKind previewKind, float r, float g, float b)
            {
                FunctionType = functionType;
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
            new Definition(FunctionType.Box, RenderPreviewKind.Box, 0.85f, 0.85f, 0.85f),
            new Definition(FunctionType.CameraCollisionBox, RenderPreviewKind.Box, 0.6f, 0.75f, 0.9f),
            new Definition(FunctionType.CollisionBarrier, RenderPreviewKind.Box, 0.95f, 0.4f, 0.3f),
            new Definition(FunctionType.CoverExclusionArea, RenderPreviewKind.Box, 1f, 0.6f, 0.2f),
            new Definition(FunctionType.FogBox, RenderPreviewKind.Box, 0.7f, 0.65f, 0.85f),
            new Definition(FunctionType.NPC_AreaBox, RenderPreviewKind.Box, 1f, 0.95f, 0.3f),
            new Definition(FunctionType.NavMeshArea, RenderPreviewKind.Box, 0.25f, 0.55f, 1f),
            new Definition(FunctionType.NavMeshBarrier, RenderPreviewKind.Box, 0.2f, 0.4f, 0.85f),
            new Definition(FunctionType.NavMeshExclusionArea, RenderPreviewKind.Box, 0.85f, 0.25f, 0.35f),
            new Definition(FunctionType.NavMeshWalkablePlatform, RenderPreviewKind.Box, 0.45f, 1f, 0.55f),
            new Definition(FunctionType.PlayerTriggerBox, RenderPreviewKind.Box, 0.25f, 0.85f, 1f),
            new Definition(FunctionType.PlayerUseTriggerBox, RenderPreviewKind.Box, 0.3f, 1f, 0.45f),
            new Definition(FunctionType.ProjectiveDecal, RenderPreviewKind.Box, 0.85f, 0.4f, 0.75f),
            new Definition(FunctionType.SimpleRefraction, RenderPreviewKind.Box, 0.35f, 0.85f, 0.8f),
            new Definition(FunctionType.SimpleWater, RenderPreviewKind.Box, 0.3f, 0.6f, 1f),
            new Definition(FunctionType.SoundBarrier, RenderPreviewKind.Box, 0.75f, 0.35f, 1f),
            new Definition(FunctionType.SoundEnvironmentZone, RenderPreviewKind.Box, 0.55f, 0.45f, 0.95f),
            new Definition(FunctionType.SpottingExclusionArea, RenderPreviewKind.Box, 1f, 0.35f, 0.35f),
            new Definition(FunctionType.SurfaceEffectBox, RenderPreviewKind.Box, 0.5f, 0.9f, 0.4f),
            new Definition(FunctionType.UiSelectionBox, RenderPreviewKind.Box, 0.95f, 0.9f, 0.5f),

            // Other previews
            new Definition(FunctionType.Sound, RenderPreviewKind.Sound, 0.2f, 0.45f, 1f),
            new Definition(FunctionType.PositionMarker, RenderPreviewKind.PositionMarker, 1f, 0.55f, 0.1f),
            new Definition(FunctionType.SoundEnvironmentMarker, RenderPreviewKind.SoundEnvironmentMarker, 0.12f, 0.22f, 0.65f),
            new Definition(FunctionType.GCIP_WorldPickup, RenderPreviewKind.SoundEnvironmentMarker, 0.1f, 0.9f, 0.2f),
            new Definition(FunctionType.LightReference, RenderPreviewKind.LightReference, 1f, 0.92f, 0.35f),
            new Definition(FunctionType.Character, RenderPreviewKind.Character, 0.25f, 0.85f, 0.35f),
            new Definition(FunctionType.ParticleEmitterReference, RenderPreviewKind.ParticleEmitter, 0.35f, 0.75f, 1f),
            new Definition(FunctionType.RibbonEmitterReference, RenderPreviewKind.ParticleEmitter, 0.75f, 0.35f, 1f),
            new Definition(FunctionType.GPU_PFXEmitterReference, RenderPreviewKind.ParticleEmitter, 1f, 0.4f, 0.75f),
            new Definition(FunctionType.SplinePath, RenderPreviewKind.SplinePath, 0.55f, 0.28f, 0.95f),
            new Definition(FunctionType.EnvironmentMap, RenderPreviewKind.EnvironmentMap, 0.45f, 0.8f, 0.9f),
            new Definition(FunctionType.SoundNetworkNode, RenderPreviewKind.SoundNetworkNode, 0.7f, 0.3f, 0.95f),
            new Definition(FunctionType.SoundObject, RenderPreviewKind.SoundObject, 0.25f, 0.75f, 0.45f),

            // Pathfinding / nav / camera
            new Definition(FunctionType.PathfindingAlienBackstageNode, RenderPreviewKind.PositionMarker, 0.2f, 0.45f, 1f),
            new Definition(FunctionType.PathfindingManualNode, RenderPreviewKind.PositionMarker, 1f, 0.25f, 0.25f),
            new Definition(FunctionType.NavMeshReachabilitySeedPoint, RenderPreviewKind.PositionMarker, 1f, 1f, 1f),
            new Definition(FunctionType.CameraResource, RenderPreviewKind.CameraResource, 0.78f, 0.82f, 0.88f),
            new Definition(FunctionType.PathfindingTeleportNode, RenderPreviewKind.PositionMarker, 0.25f, 0.85f, 0.35f),
            new Definition(FunctionType.PathfindingWaitNode, RenderPreviewKind.PositionMarker, 0.55f, 0.35f, 0.15f),
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

        /// <summary>
        /// Box volumes use the semi-transparent preview shader; all other mesh gizmos are fully opaque.
        /// </summary>
        public static bool UsesTransparentPreview(RenderPreviewKind previewKind)
        {
            return previewKind == RenderPreviewKind.Box;
        }

        public static bool UsesTransparentPreview(FunctionType functionType)
        {
            return UsesTransparentPreview(GetPreviewKind(functionType));
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
