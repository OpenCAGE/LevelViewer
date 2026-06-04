using System;
using System.Collections.Generic;
using System.Text;
using CATHODE;
using CATHODE.ShaderTypes;
using Godot;

/// <summary>
/// ModelReference materials: soft shaded solid + optional wireframe overlay.
/// </summary>
public static class AlienSceneMaterials
{
	private const int OpaqueRenderPriority = 0;
	private const int TransparentRenderPriority = 1;
	private const int TransparentWireframeRenderPriority = 2;

	private static Shader _shadedShader;
	private static Shader _shadedShaderDoubleSided;
	private static Shader _shadedShaderTransparent;
	private static Shader _shadedShaderTransparentDoubleSided;
	private static Shader _wireframeShader;
	private static Shader _wireframeShaderDoubleSided;
	private static Shader _wireframeShaderTransparent;
	private static Shader _wireframeShaderTransparentDoubleSided;

	private static readonly HashSet<string> LoggedSignBackgroundMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static readonly string[] AlphaBlendFeatureNames =
	{
		"USE_ALPHA_AS_BLENDFACTOR",
		"FORCE_TO_ALPHA",
		"GLASS",
		"FOG_ALPHA",
		"VERTEX_ALPHA_OPACITY_ONLY",
	};

	public readonly struct MaterialResult
	{
		public MaterialResult(ShaderMaterial material, bool supported)
		{
			Material = material;
			Supported = supported;
		}

		public ShaderMaterial Material { get; }
		public bool Supported { get; }
	}

	public static MaterialResult GetMaterial(Materials.Material material, AlienScene scene)
	{
		if (material == null || material.Shader == null)
			return Unsupported(material, "NULL");

		Shaders.Shader shader = material.Shader;
		string baseName = material.Name + " " + shader.Ubershader;
		int diffuseSampler = GetDiffuseSamplerIndex(shader);
		if (diffuseSampler < 0 && !TryGetSeparateAlphaMap(material, shader, scene, out _))
			return Unsupported(material, baseName + " (NO DIFFUSE SAMPLER)");

		return CreateShadedMaterial(material, shader, scene, baseName, diffuseSampler);
	}

	private static MaterialResult Unsupported(Materials.Material material, string name)
	{
		ShaderMaterial mat = new ShaderMaterial { ResourceName = name };
		return new MaterialResult(mat, false);
	}

	private static MaterialResult CreateShadedMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		string name,
		int diffuseSamplerIndex)
	{
		bool doubleSided = IsDoubleSided(shader);
		ResolveMaterialTextures(material, shader, scene, diffuseSamplerIndex, out Texture2D diffuse, out Texture2D separateAlphaMap);
		bool useAlpha = ShouldUseAlpha(shader, diffuse, separateAlphaMap);
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name,
			Shader = GetShadedShader(doubleSided, useAlpha),
			RenderPriority = useAlpha ? TransparentRenderPriority : OpaqueRenderPriority,
		};

		LogSignBackgroundMaterialDiagnostics(material, shader, scene, diffuseSamplerIndex, diffuse, separateAlphaMap, useAlpha, doubleSided);
		ApplyDiffuseParameters(godotMaterial, material, shader, useAlpha, diffuse, separateAlphaMap);
		return new MaterialResult(godotMaterial, true);
	}

	private static void LogSignBackgroundMaterialDiagnostics(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		int diffuseSamplerIndex,
		Texture2D diffuse,
		Texture2D separateAlphaMap,
		bool useAlpha,
		bool doubleSided)
	{
		string materialName = material?.Name;
		if (string.IsNullOrEmpty(materialName) ||
			materialName.IndexOf("SIGN_Background_B", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return;
		}

		if (!LoggedSignBackgroundMaterials.Add(materialName))
			return;

		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);
		Color diffuseTint = AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams, useAlpha);
		bool useSeparateAlpha = separateAlphaMap != null;

		bool alphaFromLuminance = false;
		if (useSeparateAlpha)
			alphaFromLuminance = !AlienSceneTextures.HasTransparency(separateAlphaMap);
		else if (useAlpha && diffuse != null && !AlienSceneTextures.HasTransparency(diffuse))
			alphaFromLuminance = true;

		StringBuilder enabledFeatures = new StringBuilder();
		AppendEnabledFeature(enabledFeatures, shader, "USE_ALPHA_AS_BLENDFACTOR");
		AppendEnabledFeature(enabledFeatures, shader, "FORCE_TO_ALPHA");
		AppendEnabledFeature(enabledFeatures, shader, "GLASS");
		AppendEnabledFeature(enabledFeatures, shader, "SEPARATE_ALPHA");
		AppendEnabledFeature(enabledFeatures, shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL");
		AppendEnabledFeature(enabledFeatures, shader, "ALPHA_TEST");
		AppendEnabledFeature(enabledFeatures, shader, "FOG_ALPHA");
		AppendEnabledFeature(enabledFeatures, shader, "VERTEX_ALPHA_OPACITY_ONLY");
		AppendEnabledFeature(enabledFeatures, shader, "SECONDARY_DIFFUSE_MAPPING");
		AppendEnabledFeature(enabledFeatures, shader, "DOUBLE_SIDED");

		int separateAlphaSampler = GetSeparateAlphaSamplerIndex(shader);
		int secondaryDiffuseSampler = GetSecondaryDiffuseSamplerIndex(shader);

		GD.Print(
			"[SIGN_Background_B material] " +
			$"name=\"{materialName}\" ubershader={shader.Ubershader} doubleSided={doubleSided} " +
			$"useAlpha={useAlpha} useAlphaBlend={HasAlphaBlendingEnabled(shader)} alphaTest={HasShaderFeature(shader, "ALPHA_TEST")} " +
			$"diffuseHasTransparency={diffuse != null && AlienSceneTextures.HasTransparency(diffuse)} " +
			$"separateHasTransparency={separateAlphaMap != null && AlienSceneTextures.HasTransparency(separateAlphaMap)} " +
			$"useSeparateAlphaMap={useSeparateAlpha} alphaFromLuminance={alphaFromLuminance} alphaCutout={useAlpha && HasShaderFeature(shader, "ALPHA_TEST")} " +
			$"diffuseTint=({diffuseTint.R:F3},{diffuseTint.G:F3},{diffuseTint.B:F3},{diffuseTint.A:F3}) " +
			$"diffuseUvMult={AlienSceneShaderParams.GetUvScale(material, shader, shaderParams)} " +
			$"separateAlphaUvMult={AlienSceneShaderParams.GetSeparateAlphaUvScale(material, shader)} " +
			$"diffuseTexture=\"{diffuse?.ResourceName ?? "(null)"}\" separateAlphaTexture=\"{separateAlphaMap?.ResourceName ?? "(null)"}\" " +
			$"enabledFeatures=[{enabledFeatures}]");

		GD.Print($"[SIGN_Background_B material] sampler diffuseIndex={diffuseSamplerIndex} separateAlphaIndex={separateAlphaSampler} secondaryDiffuseIndex={secondaryDiffuseSampler}");
		LogSamplerBinding(material, shader, scene, diffuseSamplerIndex, "DIFFUSE");
		if (separateAlphaSampler >= 0)
			LogSamplerBinding(material, shader, scene, separateAlphaSampler, "SEPARATE_ALPHA");
		if (secondaryDiffuseSampler >= 0)
			LogSamplerBinding(material, shader, scene, secondaryDiffuseSampler, "SECONDARY_DIFFUSE");

		LogTextureReferenceSlots(material);
	}

	private static void AppendEnabledFeature(StringBuilder builder, Shaders.Shader shader, string featureName)
	{
		if (!HasShaderFeature(shader, featureName))
			return;

		if (builder.Length > 0)
			builder.Append(", ");
		builder.Append(featureName);
	}

	private static void LogSamplerBinding(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		int samplerIndex,
		string label)
	{
		if (samplerIndex < 0)
		{
			GD.Print($"[SIGN_Background_B material] {label}: sampler index unavailable for {shader.Ubershader}");
			return;
		}

		if (shader.SamplerRemaps.Count <= samplerIndex)
		{
			GD.Print($"[SIGN_Background_B material] {label}: sampler={samplerIndex} remap=(out of range, remapCount={shader.SamplerRemaps.Count})");
			return;
		}

		int textureSlot = shader.SamplerRemaps[samplerIndex];
		if (textureSlot == 255)
		{
			GD.Print($"[SIGN_Background_B material] {label}: sampler={samplerIndex} remap=255 (unbound)");
			return;
		}

		if (textureSlot >= material.TextureReferences.Count)
		{
			GD.Print($"[SIGN_Background_B material] {label}: sampler={samplerIndex} remap={textureSlot} (texture slot out of range, refCount={material.TextureReferences.Count})");
			return;
		}

		TexturePtr texturePtr = material.TextureReferences[textureSlot];
		string ptrSummary = DescribeTexturePtr(texturePtr);
		string texPartSummary = DescribeTextureDataParts(texturePtr?.Texture);
		Texture2D resolved = scene.GetSamplerTexture(material, shader, samplerIndex);
		GD.Print(
			$"[SIGN_Background_B material] {label}: sampler={samplerIndex} remap={textureSlot} ptr={ptrSummary} {texPartSummary} resolved=\"{resolved?.ResourceName ?? "(null)"}\"");
	}

	private static void LogTextureReferenceSlots(Materials.Material material)
	{
		StringBuilder slots = new StringBuilder();
		for (int i = 0; i < material.TextureReferences.Count; i++)
		{
			if (i > 0)
				slots.Append(" | ");
			slots.Append($"[{i}]={DescribeTexturePtr(material.TextureReferences[i])}");
		}

		GD.Print($"[SIGN_Background_B material] textureSlots ({material.TextureReferences.Count}): {slots}");
	}

	private static string DescribeTexturePtr(TexturePtr texturePtr)
	{
		if (texturePtr == null)
			return "null-ptr";
		if (texturePtr.Location == TexturePtr.Source.NONE)
			return "NONE";
		if (texturePtr.Texture == null)
			return $"{texturePtr.Location}/null-tex";

		return $"{texturePtr.Location}/{texturePtr.Texture.Name}";
	}

	private static string DescribeTextureDataParts(Textures.TEX4 texture)
	{
		if (texture == null)
			return "texParts=(no-tex4)";

		int streamedBytes = texture.TextureStreamed?.Content?.Length ?? 0;
		int persistentBytes = texture.TexturePersistent?.Content?.Length ?? 0;
		Textures.TEX4.Texture chosen = AlienSceneTextures.GetTextureDataPart(texture);
		string chosenLabel = chosen == texture.TextureStreamed ? "streamed"
			: chosen == texture.TexturePersistent ? "persistent"
			: "none";
		return $"texParts(streamed={streamedBytes}b,persistent={persistentBytes}b,chosen={chosenLabel})";
	}

	public static ShaderMaterial CreateWireframeMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		string name,
		int diffuseSamplerIndex)
	{
		bool doubleSided = IsDoubleSided(shader);
		ResolveMaterialTextures(material, shader, scene, diffuseSamplerIndex, out Texture2D diffuse, out Texture2D separateAlphaMap);
		bool useAlpha = ShouldUseAlpha(shader, diffuse, separateAlphaMap);
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name + " (wireframe)",
			Shader = GetWireframeShader(doubleSided, useAlpha),
			RenderPriority = useAlpha ? TransparentWireframeRenderPriority : TransparentRenderPriority,
		};

		ApplyDiffuseParameters(godotMaterial, material, shader, useAlpha, diffuse, separateAlphaMap);
		return godotMaterial;
	}

	private static bool TryGetSeparateAlphaMap(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		out Texture2D separateAlphaMap)
	{
		separateAlphaMap = null;
		if (!HasShaderFeature(shader, "SEPARATE_ALPHA"))
			return false;

		int separateAlphaSampler = GetSeparateAlphaSamplerIndex(shader);
		if (separateAlphaSampler < 0)
			return false;

		separateAlphaMap = scene.GetSamplerTexture(material, shader, separateAlphaSampler);
		return separateAlphaMap != null;
	}

	private static void ResolveMaterialTextures(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		int diffuseSamplerIndex,
		out Texture2D diffuse,
		out Texture2D separateAlphaMap)
	{
		diffuse = diffuseSamplerIndex >= 0
			? scene.GetSamplerTexture(material, shader, diffuseSamplerIndex)
			: null;

		if (diffuse == null)
			diffuse = TryGetSecondaryDiffuseMap(material, shader, scene);

		TryGetSeparateAlphaMap(material, shader, scene, out separateAlphaMap);
	}

	private static Texture2D TryGetSecondaryDiffuseMap(Materials.Material material, Shaders.Shader shader, AlienScene scene)
	{
		if (!HasShaderFeature(shader, "SECONDARY_DIFFUSE_MAPPING"))
			return null;

		int secondarySampler = GetSecondaryDiffuseSamplerIndex(shader);
		if (secondarySampler < 0)
			return null;

		return scene.GetSamplerTexture(material, shader, secondarySampler);
	}

	public static int GetSecondaryDiffuseSamplerIndex(Shaders.Shader shader)
	{
		switch (shader.Ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			default:
				return -1;
		}
	}

	private static void ApplyDiffuseParameters(
		ShaderMaterial godotMaterial,
		Materials.Material material,
		Shaders.Shader shader,
		bool useAlpha,
		Texture2D diffuse,
		Texture2D separateAlphaMap)
	{
		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);
		godotMaterial.SetShaderParameter("diffuse_tint", AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams, useAlpha));
		godotMaterial.SetShaderParameter("diffuse_uv_mult", AlienSceneShaderParams.GetUvScale(material, shader, shaderParams));

		godotMaterial.SetShaderParameter("use_diffuse_map", diffuse != null);
		if (diffuse != null)
			godotMaterial.SetShaderParameter("diffuse_map", diffuse);

		bool useSeparateAlpha = separateAlphaMap != null;
		godotMaterial.SetShaderParameter("use_separate_alpha_map", useSeparateAlpha);
		godotMaterial.SetShaderParameter(
			"separate_alpha_from_green",
			useSeparateAlpha && HasShaderFeature(shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL"));
		godotMaterial.SetShaderParameter("separate_alpha_uv_mult", AlienSceneShaderParams.GetSeparateAlphaUvScale(material, shader));
		if (useSeparateAlpha)
			godotMaterial.SetShaderParameter("separate_alpha_map", separateAlphaMap);

		bool alphaFromLuminance = false;
		if (useSeparateAlpha)
			alphaFromLuminance = !AlienSceneTextures.HasTransparency(separateAlphaMap);
		else if (useAlpha && diffuse != null && !AlienSceneTextures.HasTransparency(diffuse))
			alphaFromLuminance = true;

		godotMaterial.SetShaderParameter("alpha_from_luminance", alphaFromLuminance);
		godotMaterial.SetShaderParameter("alpha_cutout", useAlpha && HasShaderFeature(shader, "ALPHA_TEST"));
		godotMaterial.SetShaderParameter("alpha_cutout_threshold", AlienSceneShaderParams.GetAlphaScissorThreshold(material, shader));
	}

	/// <summary>
	/// Whether this material should use the transparent/cutout shader path.
	/// </summary>
	public static bool ShouldUseAlpha(Shaders.Shader shader, Texture2D diffuse, Texture2D separateAlphaMap)
	{
		if (shader == null)
			return false;

		if (shader.Ubershader == SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT)
			return ShouldUseAlphaLightmapEnvironment(shader);

		if (separateAlphaMap != null)
			return true;

		if (HasAlphaBlendingEnabled(shader))
			return true;

		if (HasShaderFeature(shader, "ALPHA_TEST"))
			return true;

		return diffuse != null
			&& UbershaderCanSupportAlpha(shader.Ubershader)
			&& AlienSceneTextures.HasTransparency(diffuse);
	}

	/// <summary>
	/// CA_LIGHTMAP_ENVIRONMENT: opaque unless cutout or a per-instance blend feature is enabled.
	/// Ignores bound separate-alpha maps, pipeline requirement flags, and diffuse alpha noise.
	/// </summary>
	private static bool ShouldUseAlphaLightmapEnvironment(Shaders.Shader shader)
	{
		if (HasShaderFeature(shader, "ALPHA_TEST"))
			return true;

		return HasAlphaBlendingFeatureFlags(shader);
	}

	/// <summary>
	/// Per-instance feature flags that request alpha blending (not pipeline requirement bits).
	/// </summary>
	private static bool HasAlphaBlendingFeatureFlags(Shaders.Shader shader)
	{
		if (shader == null)
			return false;

		foreach (string featureName in AlphaBlendFeatureNames)
		{
			if (HasShaderFeature(shader, featureName))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Shader requirement flags and per-material blend features from CathodeLib.
	/// </summary>
	public static bool HasAlphaBlendingEnabled(Shaders.Shader shader)
	{
		if (shader == null)
			return false;

		if ((shader.UbershaderRequirementFlags & (1L << (int)SHADER_REQUIREMENTS.FORCE_TO_ALPHA)) != 0 ||
			(shader.UbershaderRequirementFlags & (1L << (int)SHADER_REQUIREMENTS.EARLY_ALPHA)) != 0 ||
			(shader.UbershaderRequirementFlags & (1L << (int)SHADER_REQUIREMENTS.POST_ALPHA)) != 0 ||
			(shader.UbershaderRequirementFlags & (1L << (int)SHADER_REQUIREMENTS.LOWRES_ALPHA)) != 0 ||
			(shader.UbershaderRequirementFlags & (1L << (int)SHADER_REQUIREMENTS.FORCE_TO_HI_ALPHA)) != 0)
		{
			return true;
		}

		return HasAlphaBlendingFeatureFlags(shader);
	}

	public static bool UbershaderCanSupportAlpha(SHADER_LIST ubershader)
	{
		List<string> features = ShaderUtility.GetShaderFunctionality(ubershader, ShaderIndexType.FEATURES);
		if (features.Contains("USE_ALPHA_AS_BLENDFACTOR") ||
			features.Contains("FORCE_TO_ALPHA") ||
			features.Contains("GLASS") ||
			features.Contains("SEPARATE_ALPHA") ||
			features.Contains("ALPHA_TEST"))
		{
			return true;
		}

		foreach (string featureName in AlphaBlendFeatureNames)
		{
			if (features.Contains(featureName))
				return true;
		}

		return false;
	}

	private static bool HasShaderFeature(Shaders.Shader shader, string featureName)
	{
		int? index = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, featureName);
		if (!index.HasValue)
			return false;

		return (shader.UbershaderFeatureFlags & (1L << index.Value)) != 0;
	}

	public static int GetSeparateAlphaSamplerIndex(Shaders.Shader shader)
	{
		switch (shader.Ubershader)
		{
			case SHADER_LIST.CA_ENVIRONMENT:
				return (int)CA_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP;
			case SHADER_LIST.CA_DECAL_ENVIRONMENT:
				return (int)CA_DECAL_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP;
			case SHADER_LIST.CA_CHARACTER:
				return (int)CA_CHARACTER.SAMPLERS.SEPARATE_ALPHA_MAP;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.SAMPLERS.SEPARATE_ALPHA_MAP;
			default:
				return -1;
		}
	}

	private static Shader GetShadedShader(bool doubleSided, bool transparent)
	{
		if (transparent)
		{
			if (doubleSided)
			{
				if (_shadedShaderTransparentDoubleSided == null)
					_shadedShaderTransparentDoubleSided = GD.Load<Shader>("res://shaders/model_reference_shaded_transparent_double_sided.gdshader");
				return _shadedShaderTransparentDoubleSided;
			}

			if (_shadedShaderTransparent == null)
				_shadedShaderTransparent = GD.Load<Shader>("res://shaders/model_reference_shaded_transparent.gdshader");
			return _shadedShaderTransparent;
		}

		if (doubleSided)
		{
			if (_shadedShaderDoubleSided == null)
				_shadedShaderDoubleSided = GD.Load<Shader>("res://shaders/model_reference_shaded_double_sided.gdshader");
			return _shadedShaderDoubleSided;
		}

		if (_shadedShader == null)
			_shadedShader = GD.Load<Shader>("res://shaders/model_reference_shaded.gdshader");
		return _shadedShader;
	}

	private static Shader GetWireframeShader(bool doubleSided, bool transparent)
	{
		if (transparent)
		{
			if (doubleSided)
			{
				if (_wireframeShaderTransparentDoubleSided == null)
					_wireframeShaderTransparentDoubleSided = GD.Load<Shader>("res://shaders/model_reference_wireframe_transparent_double_sided.gdshader");
				return _wireframeShaderTransparentDoubleSided;
			}

			if (_wireframeShaderTransparent == null)
				_wireframeShaderTransparent = GD.Load<Shader>("res://shaders/model_reference_wireframe_transparent.gdshader");
			return _wireframeShaderTransparent;
		}

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
