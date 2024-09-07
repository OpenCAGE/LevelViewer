using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class OpenCAGEShaderMaterial
{

    public UnityEngine.Material baseMaterial;

    public string shaderCategory;

    // Textures
    public Dictionary<string, Texture2D> shaderTextures = new Dictionary<string, Texture2D>()
    {
        { "DiffuseMap", null },
        { "NormalMap", null },
        { "SpecularMap", null },
        { "Occlusion", null },
        { "Emissive", null },
        { "ColorRampMap", null },
        { "SecondaryDiffuseMap", null },
        { "DiffuseMapStatic", null },
        { "Opacity", null },
        { "SecondaryNormalMap", null },
        { "SecondarySpecularMap", null },
        { "EnvironmentMap", null },
        { "FresnelLut", null },
        { "ParallaxMap", null },
        { "OpacityNoiseMap", null },
        { "DirtMap", null },
        { "WetnessNoise", null },
        { "AlphaThreshold", null },
        { "IrradianceMap", null },
        { "ConvolvedDiffuse", null },
        { "WrinkleMask", null },
        { "WrinkleNormalMap", null },
        { "ScatterMap", null },
        { "BurnThrough", null },
        { "Liquify", null },
        { "Liquify2", null },
        { "ColorRamp", null },
        { "FlowMap", null },
        { "FlowTextureMap", null },
        { "AlphaMask", null },
        { "LowLodCharacterMask", null },
        { "UnscaledDirtMap", null },
        { "FaceMap", null },
        { "MaskingMap", null },
        { "AtmosphereMap", null },
        { "DetailMap", null },
        { "LightMap", null }
    };

    // Shader params
    public Dictionary<string, object> shaderParams = new Dictionary<string, object>
    {
        { "Diffuse0", null },
        { "DiffuseMap0UVMultiplier", null },
        { "NormalMap0UVMultiplier", null },
        { "OcclusionMapUVMultiplier", null },
        { "SpecularMap0UVMultiplier", null },
        { "Diffuse1", null },
        { "DiffuseMap1UVMultiplier", null },
        { "DirtPower", null },
        { "DirtStrength", null },
        { "DirtUVMultiplier", null },
        { "Emission", null },
        { "EmissiveFactor", null },
        { "EnvironmentMapEmission", null },
        { "EnvironmentMapStrength", null },
        { "FresnelLUT", null },
        { "Iris0", null },
        { "Iris1", null },
        { "Iris2", null },
        { "IrisParallaxDisplacement", null },
        { "IsTransparent", null },
        { "LimbalSmoothRadius", null },
        { "Metallic", null },
        { "MetallicFactor0", null },
        { "MetallicFactor1", null },
        { "NormalMap0Strength", null },
        { "NormalMap0UVMultiplierOfMultiplier", null },
        { "NormalMap1Strength", null },
        { "NormalMap1UVMultiplier", null },
        { "NormalMapStrength", null },
        { "NormalMapUVMultiplier", null },
        { "OcclusionTint", null },
        { "OpacityMapUVMultiplier", null },
        { "OpacityNoiseAmplitude", null },
        { "OpacityNoiseMapUVMultiplier", null },
        { "ParallaxFactor", null },
        { "ParallaxMapUVMultiplier", null },
        { "ParallaxOffset", null },
        { "PupilDilation", null },
        { "RetinaIndexOfRefraction", null },
        { "RetinaRadius", null },
        { "ScatterMapMultiplier", null },
        { "SpecularFactor0", null },
        { "SpecularFactor1", null },
        { "SpecularMap1UVMultiplier", null },
    };

    public OpenCAGEShaderMaterial(Shader shader)
    {
        baseMaterial = new UnityEngine.Material(shader);
    }
}