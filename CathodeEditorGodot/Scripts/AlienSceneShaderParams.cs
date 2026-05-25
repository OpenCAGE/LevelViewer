using CATHODE;
using CATHODE.ShaderTypes;
using Godot;

/// <summary>
/// Reads Cathode pixel shader constants for simplified Godot materials (matches Unity OpenCAGE shader tints).
/// </summary>
public static class AlienSceneShaderParams
{
	public readonly struct MaterialParams
	{
		public MaterialParams(int diffuseTintIndex, int diffuseUvMultIndex)
		{
			DiffuseTintIndex = diffuseTintIndex;
			DiffuseUvMultIndex = diffuseUvMultIndex;
		}

		public int DiffuseTintIndex { get; }
		public int DiffuseUvMultIndex { get; }
	}

	public static MaterialParams GetParams(SHADER_LIST ubershader)
	{
		switch (ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return new MaterialParams((int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return new MaterialParams((int)CA_DECAL_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT, (int)CA_DECAL_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_CHARACTER:
				return new MaterialParams((int)CA_CHARACTER.PARAMETERS.DIFFUSE_TINT, (int)CA_CHARACTER.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_SKIN:
				return new MaterialParams((int)CA_SKIN.PARAMETERS.DIFFUSE_TINT, (int)CA_SKIN.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_HAIR:
				return new MaterialParams((int)CA_HAIR.PARAMETERS.DIFFUSE_TINT, (int)CA_HAIR.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_TERRAIN:
				return new MaterialParams((int)CA_TERRAIN.PARAMETERS.DIFFUSE_TINT, (int)CA_TERRAIN.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_SURFACE_EFFECTS:
				return new MaterialParams((int)CA_SURFACE_EFFECTS.PARAMETERS.DIFFUSE_TINT, (int)CA_SURFACE_EFFECTS.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return new MaterialParams((int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT, (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_STREAMER:
				return new MaterialParams((int)CA_STREAMER.PARAMETERS.DIFFUSE_TINT, (int)CA_STREAMER.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_LOW_LOD_CHARACTER:
				return new MaterialParams((int)CA_LOW_LOD_CHARACTER.PARAMETERS.DIFFUSE_TINT, (int)CA_LOW_LOD_CHARACTER.PARAMETERS.DIFFUSE_UV_MULT);
			case SHADER_LIST.CA_EFFECT_OVERLAY:
				return new MaterialParams((int)CA_EFFECT_OVERLAY.PARAMETERS.COLOUR_TINT, -1);
			case SHADER_LIST.CA_PLANET:
				return new MaterialParams((int)CA_PLANET.PARAMETERS.ATMOSPHERE_RIM_COLOUR, -1);
			default:
				return new MaterialParams(-1, -1);
		}
	}

	public static Color GetDiffuseTint(Materials.Material material, Shaders.Shader shader, MaterialParams parameters)
	{
		Vector4 tint = GetVector4(shader, material, parameters.DiffuseTintIndex, Vector4.One);
		return new Color(tint.X, tint.Y, tint.Z, tint.W);
	}

	public static Vector2 GetUvScale(Materials.Material material, Shaders.Shader shader, MaterialParams parameters)
	{
		float scale = GetFloat(shader, material, parameters.DiffuseUvMultIndex, 1f);
		return new Vector2(scale, scale);
	}

	private static float GetFloat(Shaders.Shader shader, Materials.Material material, int index, float fallback)
	{
		if (index < 0 || shader.PixelShaderParameterRemaps.Count <= index)
			return fallback;

		int remap = shader.PixelShaderParameterRemaps[index];
		if (remap == 255 || remap >= material.PixelShaderConstants.Count)
			return fallback;

		return material.PixelShaderConstants[remap];
	}

	private static Vector4 GetVector4(Shaders.Shader shader, Materials.Material material, int index, Vector4 fallback)
	{
		if (index < 0 || shader.PixelShaderParameterRemaps.Count <= index)
			return fallback;

		int remap = shader.PixelShaderParameterRemaps[index];
		if (remap == 255 || remap + 3 >= material.PixelShaderConstants.Count)
			return fallback;

		return new Vector4(
			material.PixelShaderConstants[remap],
			material.PixelShaderConstants[remap + 1],
			material.PixelShaderConstants[remap + 2],
			material.PixelShaderConstants[remap + 3]);
	}
}
