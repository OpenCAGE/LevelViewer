using CATHODE;
using CATHODE.ShaderTypes;
using Godot;

/// <summary>
/// Simplified opaque level materials (diffuse texture + tint + UV scale).
/// </summary>
public static class AlienSceneMaterials
{
	private static Shader _wireframeShader;
	private static Shader _wireframeShaderDoubleSided;

	public readonly struct MaterialResult
	{
		public MaterialResult(StandardMaterial3D material, bool supported)
		{
			Material = material;
			Supported = supported;
		}

		public StandardMaterial3D Material { get; }
		public bool Supported { get; }
	}

	public static MaterialResult GetMaterial(Materials.Material material, AlienScene scene)
	{
		if (material == null || material.Shader == null)
			return Unsupported(material, "NULL");

		Shaders.Shader shader = material.Shader;
		string baseName = material.Name + " " + shader.Ubershader;
		int diffuseSampler = GetDiffuseSamplerIndex(shader);

		if (diffuseSampler < 0)
			return Unsupported(material, baseName + " (NO DIFFUSE SAMPLER)");

		return CreateOpaqueMaterial(material, shader, scene, baseName, diffuseSampler);
	}

	private static MaterialResult Unsupported(Materials.Material material, string name)
	{
		StandardMaterial3D mat = new StandardMaterial3D { ResourceName = name };
		return new MaterialResult(mat, false);
	}

	private static MaterialResult CreateOpaqueMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		string name,
		int diffuseSamplerIndex)
	{
		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);

		StandardMaterial3D godotMaterial = new StandardMaterial3D
		{
			ResourceName = name,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
			Roughness = 1f,
			Metallic = 0f,
			Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
			TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
			CullMode = BaseMaterial3D.CullModeEnum.Back,
		};

		godotMaterial.AlbedoColor = AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams);

		Vector2 uvScale = AlienSceneShaderParams.GetUvScale(material, shader, shaderParams);
		godotMaterial.Uv1Scale = new Vector3(uvScale.X, uvScale.Y, 1f);

		if (IsDoubleSided(shader))
			godotMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

		Texture2D diffuse = scene.GetSamplerTexture(material, shader, diffuseSamplerIndex);
		if (diffuse != null)
			godotMaterial.AlbedoTexture = diffuse;

		return new MaterialResult(godotMaterial, true);
	}

	public static ShaderMaterial CreateWireframeMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		string name,
		int diffuseSamplerIndex)
	{
		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);

		bool doubleSided = IsDoubleSided(shader);
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name + " (wireframe)",
			Shader = GetWireframeShader(doubleSided),
			RenderPriority = 1,
		};

		godotMaterial.SetShaderParameter("diffuse_tint", AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams));
		godotMaterial.SetShaderParameter("diffuse_uv_mult", AlienSceneShaderParams.GetUvScale(material, shader, shaderParams));

		Texture2D diffuse = scene.GetSamplerTexture(material, shader, diffuseSamplerIndex);
		godotMaterial.SetShaderParameter("use_diffuse_map", diffuse != null);
		if (diffuse != null)
			godotMaterial.SetShaderParameter("diffuse_map", diffuse);

		return godotMaterial;
	}

	private static Shader GetWireframeShader(bool doubleSided)
	{
		if (doubleSided)
		{
			if (_wireframeShaderDoubleSided == null)
				_wireframeShaderDoubleSided = GD.Load<Shader>("res://shaders/model_reference_wireframe_double_sided.gdshader");
			return _wireframeShaderDoubleSided;
		}

		if (_wireframeShader == null)
			_wireframeShader = GD.Load<Shader>("res://shaders/model_reference_wireframe.gdshader");
		return _wireframeShader;
	}

	public static int GetDiffuseSamplerIndex(Shaders.Shader shader)
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

	private static bool IsDoubleSided(Shaders.Shader shader)
	{
		int? index = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, "DOUBLE_SIDED");
		if (!index.HasValue)
			return false;

		return (shader.UbershaderFeatureFlags & (1L << index.Value)) != 0;
	}
}
