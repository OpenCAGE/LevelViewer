using System;
using CATHODE;
using CATHODE.ShaderTypes;
using Godot;

/// <summary>
/// Simplified level materials — everything uses the opaque render path (no transparent queue / alpha blend).
/// Alpha-tested content uses AlphaScissor, which still draws in the opaque pass with depth write.
/// </summary>
public static class AlienSceneMaterials
{
	public readonly struct MaterialResult
	{
		public MaterialResult(StandardMaterial3D material, bool supported, bool alphaScissor)
		{
			Material = material;
			Supported = supported;
			AlphaScissor = alphaScissor;
		}

		public StandardMaterial3D Material { get; }
		public bool Supported { get; }
		public bool AlphaScissor { get; }
	}

	public static MaterialResult GetMaterial(Materials.Material material, AlienScene scene)
	{
		if (material == null || material.Shader == null)
			return Unsupported(material, "NULL");

		Shaders.Shader shader = material.Shader;
		string baseName = material.Name + " " + shader.Ubershader;
		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);
		int diffuseSampler = GetDiffuseSamplerIndex(shader);

		if (diffuseSampler < 0)
			return Unsupported(material, baseName + " (NO DIFFUSE SAMPLER)");

		bool alphaScissor = NeedsAlphaScissor(shader);
		return CreateDiffuseMaterial(material, shader, scene, shaderParams, baseName, diffuseSampler, alphaScissor);
	}

	private static MaterialResult Unsupported(Materials.Material material, string name)
	{
		StandardMaterial3D mat = new StandardMaterial3D { ResourceName = name };
		return new MaterialResult(mat, false, false);
	}

	private static MaterialResult CreateDiffuseMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		AlienSceneShaderParams.MaterialParams shaderParams,
		string name,
		int diffuseSamplerIndex,
		bool alphaScissor)
	{
		StandardMaterial3D godotMaterial = new StandardMaterial3D
		{
			ResourceName = name,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
			SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
			Roughness = 1f,
			Metallic = 0f,
			VertexColorUseAsAlbedo = false,
			TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
			CullMode = BaseMaterial3D.CullModeEnum.Back,
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly,
			RenderPriority = 0,
			Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
		};

		Color tint = AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams);
		tint.A = 1f;
		godotMaterial.AlbedoColor = tint;

		Vector2 uvScale = AlienSceneShaderParams.GetUvScale(material, shader, shaderParams);
		godotMaterial.Uv1Scale = new Vector3(uvScale.X, uvScale.Y, 1f);

		if (alphaScissor)
		{
			godotMaterial.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
			godotMaterial.AlphaScissorThreshold = 0.5f;
		}

		if (IsDoubleSided(shader))
			godotMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

		Texture2D diffuse = scene.GetDiffuseTexture(material, shader, diffuseSamplerIndex);
		if (diffuse != null)
			godotMaterial.AlbedoTexture = diffuse;

		return new MaterialResult(godotMaterial, true, alphaScissor);
	}

	private static int GetDiffuseSamplerIndex(Shaders.Shader shader)
	{
		switch (shader.Ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_SKIN:
				return (int)CA_SKIN.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_HAIR:
				return (int)CA_HAIR.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_EYE:
				return (int)CA_EYE.SAMPLERS.IRIS_MAP;
			case SHADER_LIST.CA_SKIN_OCCLUSION:
				return (int)CA_SKIN_OCCLUSION.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_SKYDOME:
				return (int)CA_SKYDOME.SAMPLERS.SKYDOME_MAP;
			case SHADER_LIST.CA_SURFACE_EFFECTS:
				return (int)CA_SURFACE_EFFECTS.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_EFFECT_OVERLAY:
				return (int)CA_EFFECT_OVERLAY.SAMPLERS.TEXTURE_MAP;
			case SHADER_LIST.CA_TERRAIN:
				return (int)CA_TERRAIN.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_NONINTERACTIVE_WATER:
				return (int)CA_NONINTERACTIVE_WATER.SAMPLERS.NORMAL_MAP;
			case SHADER_LIST.CA_SIMPLEWATER:
				return (int)CA_SIMPLEWATER.SAMPLERS.NORMAL_MAP;
			case SHADER_LIST.CA_PLANET:
				return (int)CA_PLANET.SAMPLERS.ATMOSPHERE_MAP;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_LOW_LOD_CHARACTER:
				return (int)CA_LOW_LOD_CHARACTER.SAMPLERS.DIFFUSE_MAP;
			case SHADER_LIST.CA_SPACESUIT_VISOR:
				return (int)CA_SPACESUIT_VISOR.SAMPLERS.NORMAL_MAP;
			case SHADER_LIST.CA_CAMERA_MAP:
				return (int)CA_CAMERA_MAP.SAMPLERS.DIFFUSE_MAP;
			default:
				return -1;
		}
	}

	/// <summary>
	/// Any alpha-related ubershader feature → scissor in the opaque pass (never alpha blend / transparent queue).
	/// </summary>
	private static bool NeedsAlphaScissor(Shaders.Shader shader)
	{
		switch (shader.Ubershader)
		{
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
			case SHADER_LIST.CA_EFFECT_OVERLAY:
			case SHADER_LIST.CA_NONINTERACTIVE_WATER:
			case SHADER_LIST.CA_SIMPLEWATER:
			case SHADER_LIST.CA_SPACESUIT_VISOR:
				return true;
			case SHADER_LIST.CA_ENVIRONMENT:
				return HasAnyFeature(shader,
					CA_ENVIRONMENT.FEATURES.ALPHA_TEST,
					CA_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA,
					CA_ENVIRONMENT.FEATURES.GLASS,
					CA_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE,
					CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA);
			case SHADER_LIST.CA_CHARACTER:
				return HasAnyFeature(shader,
					CA_CHARACTER.FEATURES.ALPHA_TEST,
					CA_CHARACTER.FEATURES.FORCE_TO_ALPHA,
					CA_CHARACTER.FEATURES.ALPHABLEND_NOISE,
					CA_CHARACTER.FEATURES.SEPARATE_ALPHA);
			case SHADER_LIST.CA_SURFACE_EFFECTS:
				return HasAnyFeature(shader,
					CA_SURFACE_EFFECTS.FEATURES.ALPHA_TEST,
					CA_SURFACE_EFFECTS.FEATURES.FORCE_TO_ALPHA,
					CA_SURFACE_EFFECTS.FEATURES.ALPHA_LIGHTING);
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return HasAnyFeature(shader,
					CA_LIGHTMAP_ENVIRONMENT.FEATURES.ALPHA_TEST,
					CA_LIGHTMAP_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA,
					CA_LIGHTMAP_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE,
					CA_LIGHTMAP_ENVIRONMENT.FEATURES.SEPARATE_ALPHA);
			case SHADER_LIST.CA_STREAMER:
				return HasAnyFeature(shader,
					CA_STREAMER.FEATURES.ALPHA_TEST,
					CA_STREAMER.FEATURES.FORCE_TO_ALPHA,
					CA_STREAMER.FEATURES.ALPHABLEND_NOISE,
					CA_STREAMER.FEATURES.SEPARATE_ALPHA);
			case SHADER_LIST.CA_LOW_LOD_CHARACTER:
				return HasAnyFeature(shader,
					CA_LOW_LOD_CHARACTER.FEATURES.ALPHA_TEST,
					CA_LOW_LOD_CHARACTER.FEATURES.FORCE_TO_ALPHA);
			default:
				return false;
		}
	}

	private static bool HasAnyFeature(Shaders.Shader shader, params Enum[] features)
	{
		for (int i = 0; i < features.Length; i++)
		{
			if ((shader.UbershaderFeatureFlags & (1L << Convert.ToInt32(features[i]))) != 0)
				return true;
		}

		return false;
	}

	private static bool IsDoubleSided(Shaders.Shader shader)
	{
		int? index = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, "DOUBLE_SIDED");
		if (!index.HasValue)
			return false;

		return (shader.UbershaderFeatureFlags & (1L << index.Value)) != 0;
	}
}
