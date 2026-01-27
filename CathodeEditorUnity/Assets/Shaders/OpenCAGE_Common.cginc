// Texture Maps
sampler2D _SeparateAlphaMap;
sampler2D _DiffuseMap;
sampler2D _SecondaryDiffuseMap;
sampler2D _NormalMap;
sampler2D _SecondaryNormalMap;
sampler2D _AmbientOcclusionMap;
sampler2D _DustMap;
sampler2D _ParallaxMap;
sampler2D _SparkleMap;

// All parameters
float _SizeCullingThreshold;
float _ForcePriorityLevel;
float _ShiftPriorityLevel;
float _FresnelIntensity;
float _PlanarReflectiveOverbrightScalar;
float _SeparateAlphaUvMult;
float _DiffuseUvMult;
float4 _DiffuseTint;
float _SecondaryDiffuseUvMult;
float4 _SecondaryDiffuseTint;
float _NormalUvMult;
float _NormalMapStrength;
float _SecondaryNormalUvMult;
float _SecondaryNormalMapStrength;
float _GlassDensity;
float _GlassLightness;
float4 _GlassTint;
float4 _AoTint;
float _AmbientOcclusionMapMult;
float4 _VertAoTint;
float _EmissiveMult;
float4 _EmissiveTint;
float _DustUvMult;
float _DustFalloff;
float _SsrAmount;
float _FurRimLightingFactor;
float _ParallaxUvMult;
float _ParallaxScale;
float _ParallaxBias;
float _OpacityModifierValue;
float _AlphablendNoiseUvMult;
float _AlphablendNoisePower;
float _SparkleUvScale;
float _SparkleNormalBias;
float _SparkleMultiplier;
float _SparkleFadeStart;
float _SparklePower;
float _SparkleThreshold;
float _DirtBlendMultSpecPower;
float _DirtUvMult;
float _DirtAoAmount;
float4 _CustomTintColour;
float _TessellationFactor;
float _MinTessellationDistance;
float _TessellationRange;
float _ShapeFactor;
float _DisplacementFactor;
float _DisplacementMapUvScale;

// Feature Flags
float _VertexColour;
float _FogAlpha;
float _ReflectivePlastic;
float _DoubleSided;
float _UseAlphaAsBlendFactor;
float _ForceToAlpha;
float _AlphaTest;
float _TextureLodBiasNone;
float _TextureLodBiasSlight;
float _TextureLodBiasHigh;
float _PlanarReflective;
float _SeparateAlpha;
float _SeparateAlphaMapUseGreenChannel;
float _SignedDistanceField;
float _DiffuseMappingParallax;
float _SecondaryDiffuseMapping;
float _SecondaryDiffuseBlendMultiply;
float _NormalMapping;
float _NormalMappingParallax;
float _SecondaryNormalMapping;
float _SecondaryNormalBlendAdd;
float _Glass;
float _AmbientOcclusionMapping;
float _AmbientOcclusionUV;
float _VertexAmbientOcclusion;
float _Emissive;
float _DustMapping;
float _DustMappingParallax;
float _SSR;
float _RadiosityDynamic;
float _FurRimLighting;
float _ParallaxMapping;
float _Decal;
float _DecalDiffuse;
float _DecalNormal;
float _DecalSpecularEmissive;
float _AlphablendNoise;
float _AlphaLighting;
float _Sparkle;
float _RadiosityStatic;
float _DirtMapping;
float _DirtBlendMultiply;
float _DirtMappingParallax;
float _HiLodCustomCharacterCorpseConstants;
float _NoClip;
float _Tessellation;
float _OrientationAdaptiveTessellation;
float _PhongTessellation;
float _DisplacementMapping;

struct Input
{
    float2 uv_DiffuseMap;
    float2 uv_NormalMap;
    float2 uv_SecondaryDiffuseMap;
    float2 uv_SecondaryNormalMap;
    float2 uv_AmbientOcclusionMap;
    float2 uv_DustMap;
    float2 uv_ParallaxMap;
    float2 uv_SparkleMap;
    float2 uv_SeparateAlphaMap;
    float3 worldRefl;
    float3 worldNormal;
    float3 viewDir;
    float4 color : COLOR;
    float3 worldPos;
    float3 worldTangent;
    float3 worldBinormal;
    INTERNAL_DATA
};

void OpenCAGE_Surf (Input IN, inout SurfaceOutputStandard o)
{
    float2 parallaxOffset = 0;
    if (_ParallaxMapping > 0.5)
    {
        float height = tex2D(_ParallaxMap, IN.uv_DiffuseMap * _ParallaxUvMult).r;
        float3 viewDir = normalize(IN.viewDir);
        parallaxOffset = (height - 0.5) * _ParallaxScale * viewDir.xy;
    }
    
    float2 diffuseUV = IN.uv_DiffuseMap * _DiffuseUvMult + parallaxOffset;
    fixed4 diffuse = tex2D(_DiffuseMap, diffuseUV) * _DiffuseTint;
    
    if (_SeparateAlpha > 0.5)
    {
        float2 alphaUV = IN.uv_SeparateAlphaMap * _SeparateAlphaUvMult + parallaxOffset;
        fixed4 separateAlpha = tex2D(_SeparateAlphaMap, alphaUV);
        
        if (_Glass > 0.5)
        {
            //TODO: i think this is wrong!
        }
        else if (length(diffuse.rgb - fixed3(1,1,1)) < 0.01 || _DiffuseTint.a < 0.1)
        {
            diffuse.rgb = separateAlpha.rgb * _DiffuseTint.rgb;
        }
        
        if (_SeparateAlphaMapUseGreenChannel > 0.5)
        {
            diffuse.a = separateAlpha.g;
        }
        else
        {
            diffuse.a = separateAlpha.a;
        }
    }
    
    if (_SecondaryDiffuseMapping > 0.5)
    {
        float2 secondaryDiffuseUV = IN.uv_DiffuseMap * _SecondaryDiffuseUvMult + parallaxOffset;
        fixed4 secondaryDiffuse = tex2D(_SecondaryDiffuseMap, secondaryDiffuseUV) * _SecondaryDiffuseTint;
        
        half blendFactor = secondaryDiffuse.a;
        
        if (_SecondaryDiffuseBlendMultiply > 0.5)
        {
            diffuse.rgb = lerp(diffuse.rgb, diffuse.rgb * secondaryDiffuse.rgb, blendFactor);
        }
        else
        {
            diffuse.rgb = lerp(secondaryDiffuse.rgb, diffuse.rgb, blendFactor);
        }
    }
    
    fixed3 normal = fixed3(0, 0, 1);
    fixed3 worldNormal = IN.worldNormal; 
    if (_NormalMapping > 0.5)
    {
        float2 normalUV = IN.uv_NormalMap * _NormalUvMult + parallaxOffset;
        normal = UnpackNormal(tex2D(_NormalMap, normalUV));
        normal.xy *= _NormalMapStrength;
        normal = normalize(normal);
        
        if (_SecondaryNormalMapping > 0.5)
        {
            float2 secondaryNormalUV = IN.uv_NormalMap * _SecondaryNormalUvMult + parallaxOffset;
            fixed3 secondaryNormal = UnpackNormal(tex2D(_SecondaryNormalMap, secondaryNormalUV));
            secondaryNormal.xy *= _SecondaryNormalMapStrength;
            secondaryNormal = normalize(secondaryNormal); 
            if (_SecondaryNormalBlendAdd > 0.5)
            {
                normal = normalize(normal + secondaryNormal);
            }
            else
            {
                normal = lerp(normal, secondaryNormal, 0.5);
            }
        }
        
        float3x3 worldToTangent = float3x3(IN.worldTangent, IN.worldBinormal, IN.worldNormal);
        worldNormal = normalize(mul(normal, worldToTangent));
    }
    
    fixed ao = 1.0;
    if (_AmbientOcclusionMapping > 0.5)
    {
        float2 aoUV = IN.uv_DiffuseMap + parallaxOffset;
        if (_AmbientOcclusionUV > 0.5)
        {
            aoUV = IN.uv_NormalMap + parallaxOffset;
        }
        ao = tex2D(_AmbientOcclusionMap, aoUV).r * _AmbientOcclusionMapMult;
        diffuse.rgb *= lerp(_AoTint.rgb, fixed3(1,1,1), ao);
    }
    
    if (_DustMapping > 0.5)
    {
        float2 dustUV = IN.uv_DiffuseMap * _DustUvMult + parallaxOffset;
        if (_DustMappingParallax > 0.5)
        {
            dustUV += parallaxOffset;
        }
        fixed4 dust = tex2D(_DustMap, dustUV);
        
        half3 up = half3(0.0, 1.0, 0.0);
        half dustyness = saturate((dot(up, worldNormal) - (1.0 - _DustFalloff)) / _DustFalloff);
        dustyness *= dustyness; 
        
        diffuse.rgb = lerp(diffuse.rgb, dust.rgb, dustyness * dust.a);
    }
    
    if (_Sparkle > 0.5)
    {
        float2 sparkleUV = IN.uv_DiffuseMap * _SparkleUvScale + parallaxOffset;
        fixed4 sparkle = tex2D(_SparkleMap, sparkleUV);
        float sparkleFactor = pow(sparkle.r, _SparklePower);
        sparkleFactor = step(_SparkleThreshold, sparkleFactor);
        diffuse.rgb += sparkle.rgb * sparkleFactor * _SparkleMultiplier;
    }
    
    if (_Glass > 0.5)
    {
        diffuse.rgb = _GlassTint.rgb * _GlassDensity;
        
        if (_SeparateAlpha < 0.5)
        {
            diffuse.a = max(_GlassTint.a, 0.5);
        }
        else
        {
            diffuse.a = min(diffuse.a, 0.9); 
        }
    }
    
    fixed3 emission = 0;
    if (_Emissive > 0.5)
    {
        emission = diffuse.rgb * _EmissiveTint.rgb * _EmissiveMult;
    }
    
    if (_FurRimLighting > 0.5)
    {
        float rim = 1.0 - saturate(dot(normalize(IN.viewDir), worldNormal));
        emission += rim * _FurRimLightingFactor;
    }
    
    diffuse.rgb *= _CustomTintColour.rgb;
    
    half modifiedVertexAlpha = 1.0;
    if (_VertexColour > 0.5)
    {
        modifiedVertexAlpha = IN.color.a;
        
        if (_AlphablendNoise > 0.5)
        {
            float2 noiseUV = IN.uv_DiffuseMap * _AlphablendNoiseUvMult + parallaxOffset;
            half noise = tex2D(_DustMap, noiseUV).g * 2.0 - 1.0; 
            
            half vertexContribution = IN.color.r;
            modifiedVertexAlpha = saturate(noise * IN.color.g + vertexContribution);
        }
    }
    
    if (_VertexColour > 0.5)
    {
        if (_DirtMapping < 0.5)
        {
            diffuse.a *= modifiedVertexAlpha;
        }
    }
    
    diffuse.a *= _OpacityModifierValue;
    
    //TODO: some glass isn't rendering properly - why?
    half clip_val = diffuse.a;
    if (_AlphaTest > 0.5 || _ForceToAlpha > 0.5 || _Glass > 0.5)
    {
        clip(clip_val - 0.5);
    }
    
    o.Albedo = diffuse.rgb;
    o.Normal = normal;
    o.Metallic = 0.0;
    o.Smoothness = 0.0;
    o.Emission = emission; 
    o.Alpha = diffuse.a;
}
