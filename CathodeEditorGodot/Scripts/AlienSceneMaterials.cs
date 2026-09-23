using CATHODE;
using CATHODE.ShaderTypes;
using OpenCAGE;
using System.Collections.Generic;
using Godot;

/// <summary>
/// ModelReference materials: soft shaded solid + optional wireframe overlay.
/// </summary>
public static class AlienSceneMaterials
{
	private const int OpaqueRenderPriority = 0;
	private const int TransparentRenderPriority = 1;
	private const int TransparentWireframeRenderPriority = 2;

	//Shader parameter names as StringNames, made once. A literal is a temporary whose native handle the
	//binding gives the engine with nothing keeping it alive across the icall (see LevelViewerPick), and a
	//level builds thousands of materials.
	private static readonly StringName FilterColourParam = new StringName("filter_colour");
	private static readonly StringName ZoneColourParam = new StringName("zone_colour");
	private static readonly StringName DiffuseTintParam = new StringName("diffuse_tint");
	private static readonly StringName VertexColourTintParam = new StringName("vertex_colour_tint");
	private static readonly StringName DiffuseUvMultParam = new StringName("diffuse_uv_mult");
	private static readonly StringName UseDiffuseMapParam = new StringName("use_diffuse_map");
	private static readonly StringName DiffuseMapParam = new StringName("diffuse_map");
	private static readonly StringName UseSeparateAlphaMapParam = new StringName("use_separate_alpha_map");
	private static readonly StringName SeparateAlphaFromGreenParam = new StringName("separate_alpha_from_green");
	private static readonly StringName SeparateAlphaUvMultParam = new StringName("separate_alpha_uv_mult");
	private static readonly StringName SeparateAlphaMapParam = new StringName("separate_alpha_map");
	private static readonly StringName AlphaFromLuminanceParam = new StringName("alpha_from_luminance");
	private static readonly StringName AlphaCutoutParam = new StringName("alpha_cutout");
	private static readonly StringName AlphaCutoutThresholdParam = new StringName("alpha_cutout_threshold");
	private static readonly StringName SecondaryDiffuseMapParam = new StringName("secondary_diffuse_map");
	private static readonly StringName SecondaryDiffuseUvMultParam = new StringName("secondary_diffuse_uv_mult");
	private static readonly StringName SecondaryDiffuseTintParam = new StringName("secondary_diffuse_tint");
	private static readonly StringName SecondaryDiffuseMaskedParam = new StringName("secondary_diffuse_masked");

	private static Shader _shadedShader;
	private static Shader _shadedShaderDoubleSided;
	private static Shader _shadedShaderTransparent;
	private static Shader _shadedShaderTransparentDoubleSided;
	//The shaded shaders with a secondary diffuse layer, built from them on first use: [transparent * 2 + doubleSided]
	private static readonly Shader[] _shadedSecondaryDiffuseShaders = new Shader[4];
	private static Shader _wireframeShader;
	private static Shader _wireframeShaderDoubleSided;
	private static Shader _wireframeShaderTransparent;
	private static Shader _wireframeShaderTransparentDoubleSided;

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

	/// <summary>
	/// Occlusion geometry: drawn by the engine only to cull other meshes, so it has no diffuse to
	/// shade and is normally not rendered at all here.
	/// </summary>
	public static bool IsOcclusionShader(Materials.Material material)
	{
		if (material == null || material.Shader == null)
			return false;

		return material.Shader.Ubershader == SHADER_LIST.CA_OCCLUSION_CULLING
			|| material.Shader.Ubershader == SHADER_LIST.CA_OCCLUSION_TEST;
	}

	private static readonly Dictionary<SceneFilterKind, Material> _sceneFilterMaterials =
		new Dictionary<SceneFilterKind, Material>();
	private static Shader _sceneFilterShader;
	private static Shader _sceneFilterShaderBackfaces;

	/// <summary>
	/// Flat filter colour with the preview's fake directional shading, shared by every mesh of that
	/// category. Unlit geometry in one colour reads as a single silhouette from any distance, which is
	/// useless for judging shape - the N.L term is what makes the surfaces legible.
	/// </summary>
	public static Material GetSceneFilterMaterial(SceneFilterKind kind)
	{
		if (_sceneFilterMaterials.TryGetValue(kind, out Material existing) && GodotObject.IsInstanceValid(existing))
			return existing;

		Color colour = new Color(0.9f, 0.12f, 0.12f);
		if (RenderFilterDefinitions.TryGetSceneFilter(kind, out SceneFilterDefinition definition))
			colour = new Color(definition.R, definition.G, definition.B);

		bool backfacesOnly = kind == SceneFilterKind.OcclusionMeshes;
		Shader shader = GetSceneFilterShader(backfacesOnly);

		Material material;
		if (shader != null)
		{
			ShaderMaterial shaded = new ShaderMaterial
			{
				ResourceName = kind + " filter",
				Shader = shader,
			};
			shaded.SetShaderParameter(FilterColourParam, colour);
			material = shaded;
		}
		else
		{
			//Shader missing from the export - fall back to flat colour rather than nothing at all
			ViewerLog.PrintErr("[Filters] scene filter shader missing; " + kind + " will draw unshaded.");
			material = new StandardMaterial3D
			{
				ResourceName = kind + " filter (unshaded fallback)",
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = colour,
				CullMode = backfacesOnly
					? BaseMaterial3D.CullModeEnum.Front
					: BaseMaterial3D.CullModeEnum.Disabled,
			};
		}

		_sceneFilterMaterials[kind] = material;
		return material;
	}

	private static readonly Dictionary<long, ShaderMaterial> _zoneTintMaterials = new Dictionary<long, ShaderMaterial>();
	private static readonly HashSet<Material> _zoneTintMaterialSet = new HashSet<Material>();
	private static Shader _zoneTintShader;
	private static Shader _zoneTintShaderDoubleSided;

	/// <summary>
	/// The flat zone colour a mesh is drawn in while Show Zones is on, shared by every mesh of that
	/// zone. Replaces the mesh's material outright: the textures are not wanted here, and drawing the
	/// tint as an overlay instead costs a second draw call on every mesh in the level.
	/// </summary>
	/// <remarks>
	/// One material per colour and cull mode, never per mesh. A level names a few hundred zones, so
	/// this stays a few hundred materials however much geometry they cover - and per-mesh materials on
	/// a scene this size is what exhausted the RenderingServer's RID pool once already.
	/// </remarks>
	public static Material GetZoneTintMaterial(Color colour, bool doubleSided)
	{
		long key = ((long)colour.ToRgba32() << 1) | (doubleSided ? 1L : 0L);
		if (_zoneTintMaterials.TryGetValue(key, out ShaderMaterial cached) && GodotObject.IsInstanceValid(cached))
			return cached;

		Shader shader = GetZoneTintShader(doubleSided);
		if (shader == null)
			return null;

		ShaderMaterial material = new ShaderMaterial
		{
			ResourceName = "zone tint",
			Shader = shader,
		};
		material.SetShaderParameter(ZoneColourParam, colour);

		_zoneTintMaterials[key] = material;
		_zoneTintMaterialSet.Add(material);
		return material;
	}

	/// <summary>Whether this is one of ours, so a restore can tell it apart from a later override.</summary>
	public static bool IsZoneTintMaterial(Material material)
	{
		return material != null && _zoneTintMaterialSet.Contains(material);
	}

	private static Shader GetZoneTintShader(bool doubleSided)
	{
		if (doubleSided)
		{
			if (_zoneTintShaderDoubleSided == null)
				_zoneTintShaderDoubleSided = GD.Load<Shader>("res://shaders/zone_tint_double_sided.gdshader");
			if (_zoneTintShaderDoubleSided == null)
				ViewerLog.PrintErr("[Zones] Missing zone_tint_double_sided.gdshader - the project needs exporting again.");
			return _zoneTintShaderDoubleSided;
		}

		if (_zoneTintShader == null)
			_zoneTintShader = GD.Load<Shader>("res://shaders/zone_tint.gdshader");
		if (_zoneTintShader == null)
			ViewerLog.PrintErr("[Zones] Missing zone_tint.gdshader - the project needs exporting again.");
		return _zoneTintShader;
	}

	/// <summary>
	/// True when a material draws both faces. Anything replacing it has to do the same, or a
	/// single-sided sheet the level draws from both sides turns into a hole.
	/// </summary>
	public static bool IsDoubleSidedMaterial(Material material)
	{
		if (material is StandardMaterial3D standard)
			return standard.CullMode == BaseMaterial3D.CullModeEnum.Disabled;

		if (material is ShaderMaterial shaderMaterial && shaderMaterial.Shader != null)
		{
			string path = shaderMaterial.Shader.ResourcePath;
			return !string.IsNullOrEmpty(path) && path.Contains("double_sided");
		}

		return false;
	}

	/// <summary>
	/// True when a material only draws back faces (the occlusion filter). Picking and highlight
	/// overlays have to honour this, or they act on a near surface that was never drawn.
	/// </summary>
	public static bool IsBackFaceOnlyMaterial(Material material)
	{
		if (material is StandardMaterial3D standard)
			return standard.CullMode == BaseMaterial3D.CullModeEnum.Front;

		if (material is ShaderMaterial shaderMaterial && shaderMaterial.Shader != null)
		{
			string path = shaderMaterial.Shader.ResourcePath;
			return !string.IsNullOrEmpty(path) && path.Contains("scene_filter_shaded_backfaces");
		}

		return false;
	}

	private static Shader GetSceneFilterShader(bool backfacesOnly)
	{
		if (backfacesOnly)
		{
			if (_sceneFilterShaderBackfaces == null)
				_sceneFilterShaderBackfaces = GD.Load<Shader>("res://shaders/scene_filter_shaded_backfaces.gdshader");
			return _sceneFilterShaderBackfaces;
		}

		if (_sceneFilterShader == null)
			_sceneFilterShader = GD.Load<Shader>("res://shaders/scene_filter_shaded.gdshader");
		return _sceneFilterShader;
	}

	public static MaterialResult GetMaterial(
		Materials.Material material,
		AlienScene scene,
		ModelReferenceMaterialOverrides.EnvironmentColourScalars? environmentScalars = null)
	{
		if (material == null || material.Shader == null)
			return Unsupported(material, "NULL");

		Shaders.Shader shader = material.Shader;
		string baseName = material.Name + " " + shader.Ubershader;
		int diffuseSampler = GetDiffuseSamplerIndex(shader);
		if (diffuseSampler < 0 && !TryGetSeparateAlphaMap(material, shader, scene, out _))
			return Unsupported(material, baseName + " (NO DIFFUSE SAMPLER)");

		return CreateShadedMaterial(material, shader, scene, baseName, diffuseSampler, environmentScalars);
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
		int diffuseSamplerIndex,
		ModelReferenceMaterialOverrides.EnvironmentColourScalars? environmentScalars = null)
	{
		bool doubleSided = IsDoubleSided(shader);
		ResolveMaterialTextures(material, shader, scene, diffuseSamplerIndex, out Texture2D diffuse, out Texture2D separateAlphaMap);
		Texture2D secondaryDiffuse = TryGetSecondaryDiffuseMap(material, shader, scene);
		bool useTransparentBlend = ShouldUseTransparentBlend(shader, separateAlphaMap);
		bool useAlphaCutout = ShouldUseAlphaCutout(shader);
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name,
			Shader = GetShadedShader(doubleSided, useTransparentBlend, secondaryDiffuse != null),
			RenderPriority = useTransparentBlend ? TransparentRenderPriority : OpaqueRenderPriority,
		};

		ApplyDiffuseParameters(
			godotMaterial,
			material,
			shader,
			useTransparentBlend,
			useAlphaCutout,
			diffuse,
			separateAlphaMap,
			environmentScalars);
		if (secondaryDiffuse != null)
			ApplySecondaryDiffuseParameters(godotMaterial, material, shader, secondaryDiffuse);
		return new MaterialResult(godotMaterial, true);
	}

	public static ShaderMaterial CreateWireframeMaterial(
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		string name,
		int diffuseSamplerIndex,
		ModelReferenceMaterialOverrides.EnvironmentColourScalars? environmentScalars = null)
	{
		bool doubleSided = IsDoubleSided(shader);
		ResolveMaterialTextures(material, shader, scene, diffuseSamplerIndex, out Texture2D diffuse, out Texture2D separateAlphaMap);
		bool useTransparentBlend = ShouldUseTransparentBlend(shader, separateAlphaMap);
		bool useAlphaCutout = ShouldUseAlphaCutout(shader);
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name + " (wireframe)",
			Shader = GetWireframeShader(doubleSided, useTransparentBlend),
			RenderPriority = useTransparentBlend ? TransparentWireframeRenderPriority : TransparentRenderPriority,
		};

		ApplyDiffuseParameters(
			godotMaterial,
			material,
			shader,
			useTransparentBlend,
			useAlphaCutout,
			diffuse,
			separateAlphaMap,
			environmentScalars);
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

		TryGetSeparateAlphaMap(material, shader, scene, out separateAlphaMap);
	}

	/// <summary>
	/// The secondary diffuse map, when the material multiplies one over its diffuse.
	/// </summary>
	/// <remarks>
	/// It is never a stand-in for the diffuse. On a corpse's shirt the diffuse is a fabric tiled five
	/// times over and this is the shirt's own creases-and-blood sheet, laid out once over its UVs and
	/// black wherever the shirt has none - drawn as the diffuse, at the diffuse's tiling, it covered the
	/// shirt in black blotches and scattered blood (issue 702).
	/// </remarks>
	private static Texture2D TryGetSecondaryDiffuseMap(Materials.Material material, Shaders.Shader shader, AlienScene scene)
	{
		if (!HasShaderFeature(shader, "SECONDARY_DIFFUSE_MAPPING"))
			return null;

		//CA_SKIN's layer always multiplies and has no blend flag to say so. Anything else has to say it
		//does: CA_TERRAIN's layer, for one, is a vertex-weighted lerp towards it instead
		if (shader.Ubershader != SHADER_LIST.CA_SKIN && !HasShaderFeature(shader, "SECONDARY_DIFFUSE_BLEND_MULTIPLY"))
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
			case SHADER_LIST.CA_SKIN:
				return (int)CA_SKIN.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
				return (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			case SHADER_LIST.CA_STREAMER:
				return (int)CA_STREAMER.SAMPLERS.SECONDARY_DIFFUSE_MAP;
			default:
				return -1;
		}
	}

	/// <summary>
	/// The retail secondary diffuse: its squared colour times its own tint, multiplied over the tinted
	/// diffuse at its own tiling. On a vertex-coloured CA_ENVIRONMENT or CA_CHARACTER surface its alpha
	/// is coverage - the layer washes to white where it is transparent - and anywhere else it multiplies
	/// in whole.
	/// </summary>
	private static void ApplySecondaryDiffuseParameters(
		ShaderMaterial godotMaterial,
		Materials.Material material,
		Shaders.Shader shader,
		Texture2D secondaryDiffuse)
	{
		godotMaterial.SetShaderParameter(SecondaryDiffuseMapParam, secondaryDiffuse);
		godotMaterial.SetShaderParameter(SecondaryDiffuseUvMultParam, AlienSceneShaderParams.GetSecondaryDiffuseUvScale(material, shader));
		godotMaterial.SetShaderParameter(SecondaryDiffuseTintParam, AlienSceneShaderParams.GetSecondaryDiffuseTint(material, shader));
		godotMaterial.SetShaderParameter(
			SecondaryDiffuseMaskedParam,
			shader.Ubershader != SHADER_LIST.CA_SKIN && HasShaderFeature(shader, "VERTEX_COLOUR"));
	}

	private static void ApplyDiffuseParameters(
		ShaderMaterial godotMaterial,
		Materials.Material material,
		Shaders.Shader shader,
		bool useTransparentBlend,
		bool useAlphaCutout,
		Texture2D diffuse,
		Texture2D separateAlphaMap,
		ModelReferenceMaterialOverrides.EnvironmentColourScalars? environmentScalars = null)
	{
		bool preserveDiffuseAlpha = useTransparentBlend || useAlphaCutout;
		AlienSceneShaderParams.MaterialParams shaderParams = AlienSceneShaderParams.GetParams(shader.Ubershader);
		Color diffuseTint = AlienSceneShaderParams.GetDiffuseTint(material, shader, shaderParams, preserveDiffuseAlpha);
		Color vertexColourTint = Colors.White;
		if (environmentScalars.HasValue)
		{
			ModelReferenceMaterialOverrides.EnvironmentColourScalars scalars = environmentScalars.Value;
			diffuseTint = new Color(
				diffuseTint.R * scalars.Diffuse.X,
				diffuseTint.G * scalars.Diffuse.Y,
				diffuseTint.B * scalars.Diffuse.Z,
				diffuseTint.A * scalars.Diffuse.W);
			vertexColourTint = new Color(scalars.Vertex.X, scalars.Vertex.Y, scalars.Vertex.Z, scalars.Vertex.W);
		}

		godotMaterial.SetShaderParameter(DiffuseTintParam, diffuseTint);
		godotMaterial.SetShaderParameter(VertexColourTintParam, vertexColourTint);
		godotMaterial.SetShaderParameter(DiffuseUvMultParam, AlienSceneShaderParams.GetUvScale(material, shader, shaderParams));

		godotMaterial.SetShaderParameter(UseDiffuseMapParam, diffuse != null);
		if (diffuse != null)
			godotMaterial.SetShaderParameter(DiffuseMapParam, diffuse);

		bool useSeparateAlpha = separateAlphaMap != null;
		godotMaterial.SetShaderParameter(UseSeparateAlphaMapParam, useSeparateAlpha);
		godotMaterial.SetShaderParameter(
			SeparateAlphaFromGreenParam,
			useSeparateAlpha && HasShaderFeature(shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL"));
		godotMaterial.SetShaderParameter(SeparateAlphaUvMultParam, AlienSceneShaderParams.GetSeparateAlphaUvScale(material, shader));
		if (useSeparateAlpha)
			godotMaterial.SetShaderParameter(SeparateAlphaMapParam, separateAlphaMap);

		bool alphaFromLuminance = false;
		if (useSeparateAlpha)
			alphaFromLuminance = !AlienSceneTextures.HasTransparency(separateAlphaMap);
		else if (useAlphaCutout && diffuse != null && !AlienSceneTextures.HasTransparency(diffuse))
			alphaFromLuminance = true;

		godotMaterial.SetShaderParameter(AlphaFromLuminanceParam, alphaFromLuminance);
		godotMaterial.SetShaderParameter(AlphaCutoutParam, useAlphaCutout);
		godotMaterial.SetShaderParameter(AlphaCutoutThresholdParam, AlienSceneShaderParams.GetAlphaScissorThreshold(material, shader));
	}

	/// <summary>
	/// Alpha-blended transparent shader (separate-alpha maps, pipeline blend requirements, decals).
	/// Does not include ALPHA_TEST cutout-only materials — those stay on the opaque shader with discard.
	/// </summary>
	public static bool ShouldUseTransparentBlend(Shaders.Shader shader, Texture2D separateAlphaMap)
	{
		if (shader == null)
			return false;

		if (shader.Ubershader == SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT)
			return ShouldUseAlphaLightmapEnvironment(shader);

		if (separateAlphaMap != null)
			return true;

		if (HasAlphaBlendingEnabled(shader))
			return true;

		if (HasShaderFeature(shader, "DECAL"))
			return true;

		return false;
	}

	/// <summary>
	/// Alpha-test cutout on the opaque shader path (discard, not alpha blending).
	/// The ALPHA_TEST <em>feature</em> only selects a shader permutation that supports
	/// alpha testing; cutout is only active when the material render state enables it
	/// (D3D AlphaTestEnable). OpenCAGE's MaterialApplier ignores the feature flag for
	/// transparency — this matches that behaviour.
	/// </summary>
	public static bool ShouldUseAlphaCutout(Shaders.Shader shader)
	{
		if (shader == null || !HasShaderFeature(shader, "ALPHA_TEST"))
			return false;

		return IsRenderStateEnabled(shader, Shaders.RenderState.AlphaTestEnable);
	}

	/// <summary>Whether the shader permutation includes alpha-test code (not necessarily active at runtime).</summary>
	public static bool HasAlphaTestShaderFeature(Shaders.Shader shader) =>
		HasShaderFeature(shader, "ALPHA_TEST");

	/// <summary>
	/// Whether this material needs any alpha-aware shader path (blend and/or cutout).
	/// </summary>
	public static bool ShouldUseAlpha(Shaders.Shader shader, Texture2D separateAlphaMap)
	{
		return ShouldUseTransparentBlend(shader, separateAlphaMap) || ShouldUseAlphaCutout(shader);
	}

	/// <summary>
	/// CA_LIGHTMAP_ENVIRONMENT: opaque unless a per-instance alpha-blend feature is enabled.
	/// </summary>
	private static bool ShouldUseAlphaLightmapEnvironment(Shaders.Shader shader)
	{
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

	private static bool HasShaderFeature(Shaders.Shader shader, string featureName)
	{
		int? index = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, featureName);
		if (!index.HasValue)
			return false;

		return (shader.UbershaderFeatureFlags & (1L << index.Value)) != 0;
	}

	public static bool TryGetRenderStateValue(Shaders.Shader shader, Shaders.RenderState state, out int value)
	{
		value = 0;
		if (shader?.RenderStates?.Entries == null)
			return false;

		int stateId = (int)state;
		for (int i = 0; i < shader.RenderStates.Entries.Count; i++)
		{
			Shaders.StateBlock.Entry entry = shader.RenderStates.Entries[i];
			if (entry.StateId != stateId)
				continue;

			value = entry.Value;
			return true;
		}

		return false;
	}

	public static bool IsRenderStateEnabled(Shaders.Shader shader, Shaders.RenderState state)
	{
		return TryGetRenderStateValue(shader, state, out int value) && value != 0;
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

	/// <summary>
	/// The shaded shader, with a secondary diffuse layer multiplied over the tinted diffuse if asked for.
	/// </summary>
	/// <remarks>
	/// The layer variants are built here from the exported shaders' own source rather than shipped as
	/// shaders of their own, so they need no re-export and stay in step with the plain ones. The layer
	/// goes in just ahead of the vertex colour tint - retail applies it straight after the diffuse tint -
	/// and if that line has gone from the source the plain shader is used and the layer is not drawn.
	/// </remarks>
	private static Shader GetShadedShader(bool doubleSided, bool transparent, bool secondaryDiffuse)
	{
		Shader shader = GetShadedShader(doubleSided, transparent);
		if (!secondaryDiffuse || shader == null)
			return shader;

		int variant = (transparent ? 2 : 0) + (doubleSided ? 1 : 0);
		if (_shadedSecondaryDiffuseShaders[variant] == null)
			_shadedSecondaryDiffuseShaders[variant] = BuildSecondaryDiffuseShader(shader) ?? shader;
		return _shadedSecondaryDiffuseShaders[variant];
	}

	private const string SecondaryDiffuseInsertBefore = "color = model_reference_apply_vertex_colour_tint(";

	private const string SecondaryDiffuseDeclarations = @"uniform sampler2D secondary_diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform vec2 secondary_diffuse_uv_mult = vec2(1.0);
uniform vec4 secondary_diffuse_tint : source_color = vec4(1.0);
uniform bool secondary_diffuse_masked = false;

vec3 model_reference_secondary_diffuse(vec2 uv) {
	vec4 layer = texture(secondary_diffuse_map, uv * secondary_diffuse_uv_mult);
	vec3 tinted = layer.rgb * secondary_diffuse_tint.rgb;
	return secondary_diffuse_masked ? mix(vec3(1.0), tinted, layer.a) : tinted;
}

";

	private static Shader BuildSecondaryDiffuseShader(Shader plain)
	{
		string code = plain.Code;
		int fragment = string.IsNullOrEmpty(code) ? -1 : code.IndexOf("void fragment()", System.StringComparison.Ordinal);
		int insertAt = fragment < 0 ? -1 : code.IndexOf(SecondaryDiffuseInsertBefore, fragment, System.StringComparison.Ordinal);
		if (insertAt < 0)
		{
			ViewerLog.PrintErr("[Materials] " + plain.ResourcePath + " has no vertex colour tint line to add a secondary diffuse layer ahead of; those layers will not draw.");
			return null;
		}

		code = code
			.Insert(insertAt, "color.rgb *= model_reference_secondary_diffuse(UV);\n\t")
			.Insert(fragment, SecondaryDiffuseDeclarations);

		//The double-sided and transparent checks elsewhere (picking, zones, composite focus) go by the
		//shader's path, so this one keeps the plain shader's with a suffix
		Shader shader = new Shader { Code = code };
		shader.ResourcePath = plain.ResourcePath.Replace(".gdshader", "_secondary_diffuse.gdshader");
		return shader;
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
