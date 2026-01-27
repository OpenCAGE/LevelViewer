Shader "OpenCAGE"
{
    Properties
    {
        _SeparateAlphaMap ("Separate Alpha Map", 2D) = "white" {}
        _DiffuseMap ("Diffuse Map", 2D) = "white" {}
        _SecondaryDiffuseMap ("Secondary Diffuse Map", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _SecondaryNormalMap ("Secondary Normal Map", 2D) = "bump" {}
        _SpecularMap ("Specular Map", 2D) = "white" {}
        _SecondarySpecularMap ("Secondary Specular Map", 2D) = "white" {}
        _EnvironmentMap ("Environment Map", CUBE) = "black" {}
        _AmbientOcclusionMap ("Ambient Occlusion Map", 2D) = "white" {}
        _DustMap ("Dust Map", 2D) = "white" {}
        _IrradianceCubeMap ("Irradiance Cube Map", CUBE) = "black" {}
        _ParallaxMap ("Parallax Map", 2D) = "white" {}
        _SparkleMap ("Sparkle Map", 2D) = "white" {}
        
        _SizeCullingThreshold ("Size Culling Threshold", Float) = 0.01
        _ForcePriorityLevel ("Force Priority Level", Float) = 0
        _ShiftPriorityLevel ("Shift Priority Level", Float) = 0
        _FresnelIntensity ("Fresnel Intensity", Float) = 1
        _PlanarReflectiveOverbrightScalar ("Planar Reflective Overbright Scalar", Float) = 1
        
        _SeparateAlphaUvMult ("Separate Alpha UV Mult", Float) = 1
        
        _DiffuseUvMult ("Diffuse UV Mult", Float) = 1
        _DiffuseTint ("Diffuse Tint", Color) = (1,1,1,1)
        _SecondaryDiffuseUvMult ("Secondary Diffuse UV Mult", Float) = 1
        _SecondaryDiffuseTint ("Secondary Diffuse Tint", Color) = (1,1,1,1)
        
        _NormalUvMult ("Normal UV Mult", Float) = 1
        _NormalMapStrength ("Normal Map Strength", Float) = 1
        _SecondaryNormalUvMult ("Secondary Normal UV Mult", Float) = 1
        _SecondaryNormalMapStrength ("Secondary Normal Map Strength", Float) = 1
        
        _SpecularTint ("Specular Tint", Color) = (1,1,1,1)
        _SpecularUvMult ("Specular UV Mult", Float) = 1
        _SpecularPower ("Specular Power", Float) = 32
        _SecondarySpecularTint ("Secondary Specular Tint", Color) = (1,1,1,1)
        _SecondarySpecularUvMult ("Secondary Specular UV Mult", Float) = 1
        _SecondarySpecularPower ("Secondary Specular Power", Float) = 32
        
        _GlassDensity ("Glass Density", Float) = 1
        _GlassLightness ("Glass Lightness", Float) = 1
        _GlassTint ("Glass Tint", Color) = (1,1,1,1)
        
        _DiffuseRoughnessFactor ("Diffuse Roughness Factor", Float) = 1
        
        _EnvironmentEmissiveFactor ("Environment Emissive Factor", Float) = 1
        _EnvironmentMapMult ("Environment Map Mult", Float) = 1
        
        _AoTint ("AO Tint", Color) = (1,1,1,1)
        _AmbientOcclusionMapMult ("Ambient Occlusion Map Mult", Float) = 1
        _VertAoTint ("Vert AO Tint", Color) = (1,1,1,1)
        
        _EmissiveMult ("Emissive Mult", Float) = 1
        _EmissiveTint ("Emissive Tint", Color) = (1,1,1,1)
        
        _DustUvMult ("Dust UV Mult", Float) = 1
        _DustFalloff ("Dust Falloff", Float) = 1
        
        _SsrAmount ("SSR Amount", Float) = 1
        
        _FurRimLightingFactor ("Fur Rim Lighting Factor", Float) = 1
        
        _ParallaxUvMult ("Parallax UV Mult", Float) = 1
        _ParallaxScale ("Parallax Scale", Float) = 0.1
        _ParallaxBias ("Parallax Bias", Float) = 0.02
        
        _OpacityModifierValue ("Opacity Modifier Value", Float) = 1
        
        _AlphablendNoiseUvMult ("Alphablend Noise UV Mult", Float) = 1
        _AlphablendNoisePower ("Alphablend Noise Power", Float) = 1
        
        _SparkleUvScale ("Sparkle UV Scale", Float) = 1
        _SparkleNormalBias ("Sparkle Normal Bias", Float) = 0
        _SparkleMultiplier ("Sparkle Multiplier", Float) = 1
        _SparkleFadeStart ("Sparkle Fade Start", Float) = 0
        _SparklePower ("Sparkle Power", Float) = 1
        _SparkleThreshold ("Sparkle Threshold", Float) = 0.5
        
        _DirtBlendMultSpecPower ("Dirt Blend Mult Spec Power", Float) = 1
        _DirtUvMult ("Dirt UV Mult", Float) = 1
        _DirtAoAmount ("Dirt AO Amount", Float) = 1
        
        _WetLevel ("Wet Level", Float) = 0
        _WetnessUvMult ("Wetness UV Mult", Float) = 1
        
        _CustomTintColour ("Custom Tint Colour", Color) = (1,1,1,1)
        
        _TessellationFactor ("Tessellation Factor", Float) = 1
        _MinTessellationDistance ("Min Tessellation Distance", Float) = 1
        _TessellationRange ("Tessellation Range", Float) = 10
        _ShapeFactor ("Shape Factor", Float) = 1
        
        _DisplacementFactor ("Displacement Factor", Float) = 1
        _DisplacementMapUvScale ("Displacement Map UV Scale", Float) = 1
        
        _VertexColour ("Vertex Colour", Float) = 0
        _FogAlpha ("Fog Alpha", Float) = 0
        _ReflectivePlastic ("Reflective Plastic", Float) = 0
        _DoubleSided ("Double Sided", Float) = 0
        _UseAlphaAsBlendFactor ("Use Alpha As Blend Factor", Float) = 0
        _ForceToAlpha ("Force To Alpha", Float) = 0
        _AlphaTest ("Alpha Test", Float) = 0
        _TextureLodBiasNone ("Texture LOD Bias None", Float) = 0
        _TextureLodBiasSlight ("Texture LOD Bias Slight", Float) = 0
        _TextureLodBiasHigh ("Texture LOD Bias High", Float) = 0
        _PlanarReflective ("Planar Reflective", Float) = 0
        _SeparateAlpha ("Separate Alpha", Float) = 0
        _SeparateAlphaMapUseGreenChannel ("Separate Alpha Map Use Green Channel", Float) = 0
        _SignedDistanceField ("Signed Distance Field", Float) = 0
        _DiffuseMappingParallax ("Diffuse Mapping Parallax", Float) = 0
        _SecondaryDiffuseMapping ("Secondary Diffuse Mapping", Float) = 0
        _SecondaryDiffuseBlendMultiply ("Secondary Diffuse Blend Multiply", Float) = 0
        _NormalMapping ("Normal Mapping", Float) = 0
        _NormalMappingParallax ("Normal Mapping Parallax", Float) = 0
        _SecondaryNormalMapping ("Secondary Normal Mapping", Float) = 0
        _SecondaryNormalBlendAdd ("Secondary Normal Blend Add", Float) = 0
        _SpecularMapping ("Specular Mapping", Float) = 0
        _SpecularMappingParallax ("Specular Mapping Parallax", Float) = 0
        _SecondarySpecularMapping ("Secondary Specular Mapping", Float) = 0
        _SecondarySpecularMappingParallax ("Secondary Specular Mapping Parallax", Float) = 0
        _SecondarySpecularBlendMultiply ("Secondary Specular Blend Multiply", Float) = 0
        _Glass ("Glass", Float) = 0
        _DiffuseRoughness ("Diffuse Roughness", Float) = 0
        _FrontRoughness ("Front Roughness", Float) = 0
        _AdditiveRoughness ("Additive Roughness", Float) = 0
        _EnvironmentMapping ("Environment Mapping", Float) = 0
        _AmbientOcclusionMapping ("Ambient Occlusion Mapping", Float) = 0
        _AmbientOcclusionUV ("Ambient Occlusion UV", Float) = 0
        _VertexAmbientOcclusion ("Vertex Ambient Occlusion", Float) = 0
        _Emissive ("Emissive", Float) = 0
        _DustMapping ("Dust Mapping", Float) = 0
        _DustMappingParallax ("Dust Mapping Parallax", Float) = 0
        _SSR ("SSR", Float) = 0
        _IrradianceCube ("Irradiance Cube", Float) = 0
        _RadiosityDynamic ("Radiosity Dynamic", Float) = 0
        _FurRimLighting ("Fur Rim Lighting", Float) = 0
        _ParallaxMapping ("Parallax Mapping", Float) = 0
        _Decal ("Decal", Float) = 0
        _DecalDiffuse ("Decal Diffuse", Float) = 0
        _DecalNormal ("Decal Normal", Float) = 0
        _DecalSpecularEmissive ("Decal Specular Emissive", Float) = 0
        _SpecularMappingMetalnessMasking ("Specular Mapping Metalness Masking", Float) = 0
        _AlphablendNoise ("Alphablend Noise", Float) = 0
        _AlphaLighting ("Alpha Lighting", Float) = 0
        _Sparkle ("Sparkle", Float) = 0
        _RadiosityStatic ("Radiosity Static", Float) = 0
        _DirtMapping ("Dirt Mapping", Float) = 0
        _DirtBlendMultiply ("Dirt Blend Multiply", Float) = 0
        _DirtMappingParallax ("Dirt Mapping Parallax", Float) = 0
        _Wetness ("Wetness", Float) = 0
        _HiLodCustomCharacterCorpseConstants ("Hi LOD Custom Character Corpse Constants", Float) = 0
        _NoClip ("No Clip", Float) = 0
        _Tessellation ("Tessellation", Float) = 0
        _OrientationAdaptiveTessellation ("Orientation Adaptive Tessellation", Float) = 0
        _PhongTessellation ("Phong Tessellation", Float) = 0
        _DisplacementMapping ("Displacement Mapping", Float) = 0
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        
        #include "UnityCG.cginc"
        #include "OpenCAGE_Common.cginc"
        
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            OpenCAGE_Surf(IN, o);
        }
        ENDCG
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 200
        
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows alpha
        #pragma target 3.0
        #define TRANSPARENT_MODE
        
        #include "UnityCG.cginc"
        #include "OpenCAGE_Common.cginc"
        
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            OpenCAGE_Surf(IN, o);
        }
        ENDCG
    }
    
    Fallback "Diffuse"
}