using CATHODE;
using CATHODE.ShaderTypes;
using Godot;

/// <summary>
/// Reads Cathode pixel shader constants for simplified Godot materials.
/// </summary>
public static class AlienSceneShaderParams
{
	public readonly struct MaterialParams
	{
		public MaterialParams(int diffuseTintIndex, int diffuseUvMultIndex, string diffuseTintParameterName = "DIFFUSE_TINT")
		{
			DiffuseTintIndex = diffuseTintIndex;
			DiffuseUvMultIndex = diffuseUvMultIndex;
			DiffuseTintParameterName = diffuseTintParameterName;
		}

		public int DiffuseTintIndex { get; }
		public int DiffuseUvMultIndex { get; }
		public string DiffuseTintParameterName { get; }
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
				return new MaterialParams((int)CA_EFFECT_OVERLAY.PARAMETERS.COLOUR_TINT, -1, "COLOUR_TINT");
			case SHADER_LIST.CA_PLANET:
				return new MaterialParams((int)CA_PLANET.PARAMETERS.ATMOSPHERE_RIM_COLOUR, -1, "ATMOSPHERE_RIM_COLOUR");
			default:
				return new MaterialParams(-1, -1);
		}
	}

	public static Color GetDiffuseTint(Materials.Material material, Shaders.Shader shader, MaterialParams parameters, bool preserveAlpha = false)
	{
		if (!TryGetColour(material, shader, parameters.DiffuseTintIndex, parameters.DiffuseTintParameterName, preserveAlpha, out Color tint))
			tint = Colors.White;

		//A corpse carries the colour its character was dressed in as a constant, and the shader draws that
		//in place of the diffuse tint's rgb - the alpha is still the diffuse tint's
		if (HasFeature(shader, "HI_LOD_CUSTOM_CHARACTER_CORPSE_CONSTANTS")
			&& TryGetColour(material, shader, (int)CA_ENVIRONMENT.PARAMETERS.CUSTOM_TINT_COLOUR, "CUSTOM_TINT_COLOUR", false, out Color custom))
		{
			tint = new Color(custom.R, custom.G, custom.B, tint.A);
		}

		return tint;
	}

	/// <summary>
	/// The secondary diffuse layer's own tint. CA_SKIN has none: its layer multiplies in untinted.
	/// </summary>
	public static Color GetSecondaryDiffuseTint(Materials.Material material, Shaders.Shader shader)
	{
		return TryGetColour(material, shader, GetSecondaryDiffuseTintIndex(shader.Ubershader), "SECONDARY_DIFFUSE_TINT", false, out Color tint)
			? tint
			: Colors.White;
	}

	private static int GetSecondaryDiffuseTintIndex(SHADER_LIST ubershader)
	{
		switch (ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_TINT;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_TINT;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.PARAMETERS.SECONDARY_DIFFUSE_TINT;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_TINT;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.PARAMETERS.SECONDARY_DIFFUSE_TINT;
			default:
				return -1;
		}
	}

	private static bool HasFeature(Shaders.Shader shader, string featureName)
	{
		int? index = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, featureName);
		return index.HasValue && (shader.UbershaderFeatureFlags & (1L << index.Value)) != 0;
	}

	/// <summary>
	/// Reads a colour constant, clamped to 0-1. False when the material's shader does not carry it.
	/// </summary>
	private static bool TryGetColour(
		Materials.Material material,
		Shaders.Shader shader,
		int parameterIndex,
		string parameterName,
		bool preserveAlpha,
		out Color colour)
	{
		colour = Colors.White;
		if (parameterIndex < 0 || parameterIndex >= shader.PixelShaderParameterRemaps.Count)
			return false;

		int remappedIndex = shader.PixelShaderParameterRemaps[parameterIndex];
		if (remappedIndex == 255 || remappedIndex >= material.PixelShaderConstants.Count)
			return false;

		UberShaderParameterType? parameterType = ShaderUtility.GetParameterType(shader.Ubershader, parameterName);
		if (!parameterType.HasValue)
			return TryReadTintVector4(material, remappedIndex, preserveAlpha, out colour);

		float r = 0f;
		float g = 0f;
		float b = 0f;
		float a = 1f;

		switch (parameterType.Value)
		{
			case UberShaderParameterType.Float3:
			case UberShaderParameterType.Half3:
				if (remappedIndex < material.PixelShaderConstants.Count)
					r = material.PixelShaderConstants[remappedIndex];
				if (remappedIndex + 1 < material.PixelShaderConstants.Count)
					g = material.PixelShaderConstants[remappedIndex + 1];
				if (remappedIndex + 2 < material.PixelShaderConstants.Count)
					b = material.PixelShaderConstants[remappedIndex + 2];
				break;
			case UberShaderParameterType.Float4:
			case UberShaderParameterType.Half4:
				if (remappedIndex < material.PixelShaderConstants.Count)
					r = material.PixelShaderConstants[remappedIndex];
				if (remappedIndex + 1 < material.PixelShaderConstants.Count)
					g = material.PixelShaderConstants[remappedIndex + 1];
				if (remappedIndex + 2 < material.PixelShaderConstants.Count)
					b = material.PixelShaderConstants[remappedIndex + 2];
				if (remappedIndex + 3 < material.PixelShaderConstants.Count)
					a = material.PixelShaderConstants[remappedIndex + 3];
				break;
			default:
				return TryReadTintVector4(material, remappedIndex, preserveAlpha, out colour);
		}

		r = Mathf.Clamp(r, 0f, 1f);
		g = Mathf.Clamp(g, 0f, 1f);
		b = Mathf.Clamp(b, 0f, 1f);
		a = Mathf.Clamp(a, 0f, 1f);
		if (!preserveAlpha)
			a = 1f;

		colour = new Color(r, g, b, a);
		return true;
	}

	private static bool TryReadTintVector4(Materials.Material material, int remappedIndex, bool preserveAlpha, out Color colour)
	{
		colour = Colors.White;
		if (remappedIndex + 3 >= material.PixelShaderConstants.Count)
			return false;

		float r = Mathf.Clamp(material.PixelShaderConstants[remappedIndex], 0f, 1f);
		float g = Mathf.Clamp(material.PixelShaderConstants[remappedIndex + 1], 0f, 1f);
		float b = Mathf.Clamp(material.PixelShaderConstants[remappedIndex + 2], 0f, 1f);
		float a = Mathf.Clamp(material.PixelShaderConstants[remappedIndex + 3], 0f, 1f);
		if (!preserveAlpha)
			a = 1f;

		colour = new Color(r, g, b, a);
		return true;
	}

	public static Vector2 GetUvScale(Materials.Material material, Shaders.Shader shader, MaterialParams parameters)
	{
		float scale = GetFloat(shader, material, parameters.DiffuseUvMultIndex, 1f);
		return new Vector2(scale, scale);
	}

	public static float GetAlphaScissorThreshold(Materials.Material material, Shaders.Shader shader)
	{
		return Mathf.Clamp(GetFloat(shader, material, GetAlphaThresholdIndex(shader.Ubershader), 0.5f), 0f, 1f);
	}

	private static int GetAlphaThresholdIndex(SHADER_LIST ubershader)
	{
		switch (ubershader)
		{
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.PARAMETERS.ALPHATHRESHOLD_RANGE;
			default:
				return -1;
		}
	}

	/// <summary>The secondary diffuse layer's own tiling - it never shares the primary's.</summary>
	public static Vector2 GetSecondaryDiffuseUvScale(Materials.Material material, Shaders.Shader shader)
	{
		float scale = GetFloat(shader, material, GetSecondaryDiffuseUvMultIndex(shader.Ubershader), 1f);
		return new Vector2(scale, scale);
	}

	private static int GetSecondaryDiffuseUvMultIndex(SHADER_LIST ubershader)
	{
		switch (ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			case SHADER_LIST.CA_SKIN:
				return (int)CA_SKIN.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT;
			default:
				return -1;
		}
	}

	public static Vector2 GetSeparateAlphaUvScale(Materials.Material material, Shaders.Shader shader)
	{
		float scale = GetFloat(shader, material, GetSeparateAlphaUvMultIndex(shader.Ubershader), 1f);
		return new Vector2(scale, scale);
	}

	private static int GetSeparateAlphaUvMultIndex(SHADER_LIST ubershader)
	{
		switch (ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.PARAMETERS.SEPARATE_ALPHA_UV_MULT;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.PARAMETERS.SEPARATE_ALPHA_UV_MULT;
			default:
				return -1;
		}
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
}
