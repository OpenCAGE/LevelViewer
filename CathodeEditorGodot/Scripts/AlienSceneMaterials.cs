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
	internal static readonly StringName DiffuseTintParam = new StringName("diffuse_tint");
	private static readonly StringName VertexColourTintParam = new StringName("vertex_colour_tint");
	private static readonly StringName UseVertexColourParam = new StringName("use_vertex_colour");
	private static readonly StringName DiffuseUvMultParam = new StringName("diffuse_uv_mult");
	private static readonly StringName UseDiffuseMapParam = new StringName("use_diffuse_map");
	private static readonly StringName DiffuseMapParam = new StringName("diffuse_map");
	private static readonly StringName UseSeparateAlphaMapParam = new StringName("use_separate_alpha_map");
	private static readonly StringName SeparateAlphaFromGreenParam = new StringName("separate_alpha_from_green");
	private static readonly StringName SeparateAlphaFromRedParam = new StringName("separate_alpha_from_red");
	private static readonly StringName SeparateAlphaUvMultParam = new StringName("separate_alpha_uv_mult");
	private static readonly StringName SeparateAlphaMapParam = new StringName("separate_alpha_map");
	private static readonly StringName AlphaFromLuminanceParam = new StringName("alpha_from_luminance");
	private static readonly StringName AlphaSdfParam = new StringName("alpha_sdf");
	private static readonly StringName AlphaCutoutParam = new StringName("alpha_cutout");
	private static readonly StringName AlphaCutoutThresholdParam = new StringName("alpha_cutout_threshold");
	private static readonly StringName SecondaryDiffuseMapParam = new StringName("secondary_diffuse_map");
	private static readonly StringName SecondaryDiffuseUvMultParam = new StringName("secondary_diffuse_uv_mult");
	private static readonly StringName SecondaryDiffuseTintParam = new StringName("secondary_diffuse_tint");
	private static readonly StringName SecondaryDiffuseMaskedParam = new StringName("secondary_diffuse_masked");
	private static readonly StringName GlassTintParam = new StringName("glass_tint");
	private static readonly StringName GlassDensityParam = new StringName("glass_density");
	private static readonly StringName GlassLightnessParam = new StringName("glass_lightness");
	private static readonly StringName GlassFresnelParam = new StringName("glass_fresnel");
	private static readonly StringName GlassVertexAlphaParam = new StringName("glass_vertex_alpha");
	private static readonly StringName DirtModeParam = new StringName("dirt_mode");
	private static readonly StringName DirtMapParam = new StringName("dirt_map");
	private static readonly StringName DirtUvMultParam = new StringName("dirt_uv_mult");
	private static readonly StringName DirtAmountParam = new StringName("dirt_amount");
	private static readonly StringName DirtSpecPowerParam = new StringName("dirt_spec_power");
	private static readonly StringName DirtAoAmountParam = new StringName("dirt_ao_amount");
	private static readonly StringName UseAlphablendNoiseParam = new StringName("use_alphablend_noise");
	private static readonly StringName AlphablendNoiseMapParam = new StringName("alphablend_noise_map");
	private static readonly StringName AlphablendNoiseUvMultParam = new StringName("alphablend_noise_uv_mult");
	private static readonly StringName AlphablendNoisePowerParam = new StringName("alphablend_noise_power");
	private static readonly StringName AlphaFadeModeParam = new StringName("alpha_fade_mode");
	internal static readonly StringName EmissiveParam = new StringName("emissive");
	internal static readonly StringName EmissiveTintParam = new StringName("emissive_tint");
	internal static readonly StringName EmissiveStrengthParam = new StringName("emissive_strength");
	private static readonly StringName EmissiveFromLumaParam = new StringName("emissive_from_luma");
	private static readonly StringName EmissiveLumaTintParam = new StringName("emissive_luma_tint");
	private static readonly StringName UseVertexLanesParam = new StringName("use_vertex_lanes");
	private static readonly StringName VertexColourScaleParam = new StringName("vertex_colour_scale");
	private static readonly StringName UseVertexAoParam = new StringName("use_vertex_ao");
	private static readonly StringName VertexAoTintParam = new StringName("vertex_ao_tint");
	private static readonly StringName UseSpecularParam = new StringName("use_specular");
	private static readonly StringName SpecularMapParam = new StringName("specular_map");
	private static readonly StringName SpecularUvMultParam = new StringName("specular_uv_mult");
	private static readonly StringName SpecularTintParam = new StringName("specular_tint");
	private static readonly StringName SpecularPowerParam = new StringName("specular_power");
	private static readonly StringName FresnelIntensityParam = new StringName("fresnel_intensity");
	internal static readonly StringName UseEnvironmentParam = new StringName("use_environment");
	private static readonly StringName EnvironmentMapMultParam = new StringName("environment_map_mult");
	private static readonly StringName ReflectivePlasticParam = new StringName("reflective_plastic");
	private static readonly StringName UseEnvironmentCubeParam = new StringName("use_environment_cube");
	private static readonly StringName EnvironmentCubeParam = new StringName("environment_cube");
	//The room's cube, on the copies AlienScene makes for instances the level gives one (WithMoverLook)
	internal static readonly StringName UseEnvironmentRoomCubeParam = new StringName("use_environment_room_cube");
	internal static readonly StringName EnvironmentRoomCubeParam = new StringName("environment_room_cube");

	private static Shader _shadedShader;
	private static Shader _shadedShaderDoubleSided;
	private static Shader _shadedShaderTransparent;
	private static Shader _shadedShaderTransparentDoubleSided;
	//The shaded shaders with a secondary diffuse layer and/or glass, built from them on first use, by variant
	//(doubleSided + transparent * 2 + secondary diffuse * 4 + glass * 8)
	private static readonly Dictionary<int, Shader> _shadedVariantShaders = new Dictionary<int, Shader>();
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
	private static Shader _zoneTintShaderTransparent;

	/// <summary>
	/// The flat zone colour a mesh is drawn in while Show Zones is on, shared by every mesh of that
	/// zone. Replaces the mesh's material outright: the textures are not wanted here, and drawing the
	/// tint as an overlay instead costs a second draw call on every mesh in the level.
	/// </summary>
	/// <remarks>
	/// One material per colour and cull mode, never per mesh. A level names a few hundred zones, so
	/// this stays a few hundred materials however much geometry they cover - and per-mesh materials on
	/// a scene this size is what exhausted the RenderingServer's RID pool once already.
	///
	/// A colour with alpha below 1 is see-through, for the translucent box and sphere volumes: drawn as they are, in the
	/// zone's colour, rather than filled solid over the room they sit in.
	/// </remarks>
	public static Material GetZoneTintMaterial(Color colour, bool doubleSided)
	{
		long key = ((long)colour.ToRgba32() << 1) | (doubleSided ? 1L : 0L);
		if (_zoneTintMaterials.TryGetValue(key, out ShaderMaterial cached) && GodotObject.IsInstanceValid(cached))
			return cached;

		bool transparent = colour.A < 0.999f;
		Shader shader = transparent ? GetZoneTintTransparentShader() : GetZoneTintShader(doubleSided);
		if (shader == null)
			return null;

		ShaderMaterial material = new ShaderMaterial
		{
			ResourceName = "zone tint",
			Shader = shader,
			//Sorted with the volumes it stands in for (PreviewVisualUtility's transparent priority)
			RenderPriority = transparent ? 1 : 0,
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

	/// <summary>
	/// Let go of every zone tint. Colours are per zone placement, so each level shown with zones on added a set of its own
	/// and nothing ever removed them. Only once no mesh still wears one (the zone overlay restored, the level going): until
	/// then <see cref="IsZoneTintMaterial"/> is how a restore recognises them. The shaders stay - two, for the process.
	/// </summary>
	public static void ClearZoneTintMaterials()
	{
		_zoneTintMaterials.Clear();
		_zoneTintMaterialSet.Clear();
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

	private static Shader GetZoneTintTransparentShader()
	{
		if (_zoneTintShaderTransparent == null)
			_zoneTintShaderTransparent = GD.Load<Shader>("res://shaders/zone_tint_transparent.gdshader");
		if (_zoneTintShaderTransparent == null)
			ViewerLog.PrintErr("[Zones] Missing zone_tint_transparent.gdshader - the project needs exporting again.");
		return _zoneTintShaderTransparent;
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
		if (shader.Ubershader == SHADER_LIST.CA_NONINTERACTIVE_WATER || shader.Ubershader == SHADER_LIST.CA_SIMPLEWATER)
			return CreateWaterMaterial(material, shader, scene, baseName);
		if (shader.Ubershader == SHADER_LIST.CA_PLANET)
			return CreatePlanetMaterial(material, shader, scene, baseName);
		int diffuseSampler = GetDiffuseSamplerIndex(shader);
		if (diffuseSampler < 0 && !TryGetSeparateAlphaMap(material, shader, scene, out _))
			return Unsupported(material, baseName + " (NO DIFFUSE SAMPLER)");

		return CreateShadedMaterial(material, shader, scene, baseName, diffuseSampler, environmentScalars);
	}

	/* Water (CA_NONINTERACTIVE_WATER puddles, CA_SIMPLEWATER) drawn as water (shaders/water_surface.gdshader, as
	   WaterSurface draws a SimpleWater sheet): dark with depth by its DEPTH_FOG_* stops, mirroring at its fresnel. Drawn as
	   a surface, its normal map stood in for a colour: MUTHUR core's wet floor was flat yellow where the game's is a dark
	   puddle with the room in it. */
	private static Shader _waterShader;
	public const string WaterShaderPath = "res://shaders/water_surface.gdshader";

	private static MaterialResult CreateWaterMaterial(Materials.Material material, Shaders.Shader shader, AlienScene scene, string name)
	{
		_waterShader ??= GD.Load<Shader>(WaterShaderPath);
		ShaderMaterial water = new ShaderMaterial { ResourceName = name, Shader = _waterShader, RenderPriority = -20 };
		bool simple = shader.Ubershader == SHADER_LIST.CA_SIMPLEWATER;
		string[] parameterNames = System.Enum.GetNames(simple ? typeof(CA_SIMPLEWATER.PARAMETERS) : typeof(CA_NONINTERACTIVE_WATER.PARAMETERS));
		//SCALE and SECONDARY_SCALE are the vertex stage's alone
		float[] Read(string parameter, int width, bool vertexStage = false)
		{
			int index = System.Array.IndexOf(parameterNames, parameter);
			List<int> remaps = vertexStage ? shader.VertexShaderParameterRemaps : shader.PixelShaderParameterRemaps;
			List<float> constants = vertexStage ? material.VertexShaderConstants : material.PixelShaderConstants;
			if (index < 0 || remaps == null || index >= remaps.Count)
				return null;
			int remap = remaps[index];
			if (remap == 255 || constants == null || remap + width > constants.Count)
				return null;
			float[] values = new float[width];
			for (int i = 0; i < width; i++)
				values[i] = constants[remap + i];
			return values;
		}
		float One(string parameter, float fallback, bool vertexStage = false) => Read(parameter, 1, vertexStage)?[0] ?? fallback;
		Vector4 Stop(string colour, string alpha, float fallbackAlpha)
		{
			float[] c = Read(colour, 3) ?? new float[3];
			return new Vector4(c[0], c[1], c[2], Mathf.Clamp(One(alpha, fallbackAlpha), 0f, 1f));
		}
		water.SetShaderParameter("fog_initial", Stop("DEPTH_FOG_INITIAL_COLOUR", "DEPTH_FOG_INITIAL_ALPHA", 0f));
		water.SetShaderParameter("fog_mid", Stop("DEPTH_FOG_MIDPOINT_COLOUR", "DEPTH_FOG_MIDPOINT_ALPHA", 0.5f));
		water.SetShaderParameter("fog_end", Stop("DEPTH_FOG_END_COLOUR", "DEPTH_FOG_END_ALPHA", 1f));
		water.SetShaderParameter("fog_depths", new Vector2(One("DEPTH_FOG_MIDPOINT_DEPTH", 0.5f), One("DEPTH_FOG_END_DEPTH", 1f)));
		water.SetShaderParameter("fresnel", new Vector3(One("MIN_FRESNEL", 0f), One("MAX_FRESNEL", 1f), One("FRESNEL_POWER", 1f)));
		water.SetShaderParameter("reflective", HasShaderFeature(shader, "REFLECTIVE_MAPPING"));
		water.SetShaderParameter("use_mesh_normal", true);
		/* Its soft edge is the entity's (softness_edge, an instance constant the material doesn't carry): a centimetre, as
		   the levels' puddles have it. A puddle lies flush on the floor, so its fog fades out to nothing there and it is the
		   reflection alone - without it, MUTHUR core's puddle's white first stop painted it white. */
		water.SetShaderParameter("softness", 0.01f);
		string[] samplerNames = System.Enum.GetNames(simple ? typeof(CA_SIMPLEWATER.SAMPLERS) : typeof(CA_NONINTERACTIVE_WATER.SAMPLERS));
		Texture2D Sampler(string sampler)
		{
			int index = System.Array.IndexOf(samplerNames, sampler);
			return index >= 0 ? scene?.GetSamplerTexture(material, shader, index) : null;
		}
		//The puddle's outline (ALPHA_MASKING: its mesh is a square, the mask's alpha the shape)
		if (HasShaderFeature(shader, "ALPHA_MASKING"))
		{
			Texture2D mask = Sampler("ALPHA_MASK");
			if (mask != null)
			{
				water.SetShaderParameter("alpha_mask", mask);
				water.SetShaderParameter("use_alpha_mask", true);
			}
		}
		//Its ripples, as a sheet's (WaterSurface) but from the material: CA_NONINTERACTIVE_WATER's second layer reads a map of its own
		Texture2D normalMap = WaterTextures.Mipped(Sampler("NORMAL_MAP"));
		water.SetShaderParameter(WaterSurface.UseNormalMapParam, normalMap != null);
		water.SetShaderParameter(WaterSurface.NormalMapParam, normalMap);
		bool secondary = HasShaderFeature(shader, "SECONDARY_NORMAL_MAPPING");
		water.SetShaderParameter(WaterSurface.SecondaryNormalParam, secondary);
		Texture2D secondaryMap = secondary && !simple ? WaterTextures.Mipped(Sampler("SECONDARY_NORMAL_MAP")) : null;
		water.SetShaderParameter("use_secondary_map", secondaryMap != null);
		water.SetShaderParameter("secondary_normal_map", secondaryMap);
		water.SetShaderParameter(WaterSurface.RippleParam, new Vector4(One("SPEED", 0.01f), One("SCALE", 1f, true), One("SECONDARY_SPEED", -0.01f), One("SECONDARY_SCALE", 1f, true)));
		water.SetShaderParameter(WaterSurface.RippleStrengthParam, new Vector2(One("NORMAL_MAP_STRENGTH", 1f), One("SECONDARY_NORMAL_MAP_STRENGTH", 1f)));
		Texture2D flowMap = HasShaderFeature(shader, "FLOW_UV_ANIMATION") ? WaterTextures.Mipped(Sampler("FLOW_MAP")) : null;
		water.SetShaderParameter(WaterSurface.UseFlowParam, flowMap != null);
		water.SetShaderParameter(WaterSurface.FlowMapParam, flowMap);
		water.SetShaderParameter(WaterSurface.FlowParam, new Vector4(One("CYCLE_TIME", 10f), One("FLOW_SPEED", 1f), One("FLOW_TEX_SCALE", 4f), One("FLOW_WARP_STRENGTH", 0.5f)));
		water.SetShaderParameter(WaterSurface.PerturbationParam, One("REFLECTION_PERTURBATION_STRENGTH", 1f));
		return new MaterialResult(water, true);
	}

	/* Planets (CA_PLANET: the gas giant and moons through the station's windows) drawn as the game draws them
	   (shaders/planet.gdshader). Drawn as a model, each was a two metre ball floating outside the hull, or nothing. What
	   places and sizes one is its Planet entity's, per instance (PlanetLook). */
	public const string PlanetShaderPath = "res://shaders/planet.gdshader";
	private static Shader _planetShader;

	private static MaterialResult CreatePlanetMaterial(Materials.Material material, Shaders.Shader shader, AlienScene scene, string name)
	{
		_planetShader ??= GD.Load<Shader>(PlanetShaderPath);
		bool detail = HasShaderFeature(shader, "DETAIL_MAPPING");
		//The planets before the glows round them, as their sort keys have them
		ShaderMaterial planet = new ShaderMaterial { ResourceName = name, Shader = _planetShader, RenderPriority = (int)Material.RenderPriorityMin + (detail ? 2 : 3) };
		string[] parameterNames = System.Enum.GetNames(typeof(CA_PLANET.PARAMETERS));
		float[] Read(string parameter, int width, bool vertexStage)
		{
			int index = System.Array.IndexOf(parameterNames, parameter);
			List<int> remaps = vertexStage ? shader.VertexShaderParameterRemaps : shader.PixelShaderParameterRemaps;
			List<float> constants = vertexStage ? material.VertexShaderConstants : material.PixelShaderConstants;
			if (index < 0 || remaps == null || index >= remaps.Count || remaps[index] == 255 || constants == null || remaps[index] + width > constants.Count)
				return null;
			float[] values = new float[width];
			for (int i = 0; i < width; i++)
				values[i] = constants[remaps[index] + i];
			return values;
		}
		float One(string parameter, float fallback, bool vertexStage = false) => Read(parameter, 1, vertexStage)?[0] ?? fallback;
		Vector3 Three(string parameter)
		{
			float[] c = Read(parameter, 3, false);
			return c == null ? Vector3.Zero : new Vector3(c[0], c[1], c[2]);
		}
		planet.SetShaderParameter("rim_transparency", One("ATMOSPHERE_RIM_TRANSPARENCY", 1f));
		planet.SetShaderParameter("overbright", One("OVERBRIGHT_SCALAR", 1f));
		planet.SetShaderParameter("detail_tex_scalar", One("DETAIL_TEX_SCALAR", 1f));
		planet.SetShaderParameter("rim_fresnel", One("ATMOSPHERE_RIM_FRESNEL", 1f));
		planet.SetShaderParameter("rim_colour", Three("ATMOSPHERE_RIM_COLOUR"));
		planet.SetShaderParameter("rim_brightness", One("ATMOSPHERE_RIM_BRIGHTNESS", 0f));
		planet.SetShaderParameter("horizon_booster", One("HORIZON_BOOSTER", 0f));
		planet.SetShaderParameter("unlit_rim_colour", Three("UNLIT_ATMOSPHERE_RIM_COLOUR"));
		planet.SetShaderParameter("unlit_rim_brightness", One("UNLIT_ATMOSPHERE_RIM_BRIGHTNESS", 0f));
		planet.SetShaderParameter("penumbra_falloff_power", One("PENUMBRA_FALLOFF_POWER", 1f));
		planet.SetShaderParameter("light_wrap_angle", One("LIGHT_WRAP_ANGLE", 0f, vertexStage: true));
		planet.SetShaderParameter("scroll_speed", new Vector2(
			HasShaderFeature(shader, "SCROLLING_UV") ? One("SCROLL_SPEED", 0f, vertexStage: true) : 0f,
			HasShaderFeature(shader, "DETAIL_SCROLLING_UV") ? One("DETAIL_SCROLL_SPEED", 0f, vertexStage: true) : 0f));
		planet.SetShaderParameter("flow", new Vector4(One("CYCLE_TIME", 1f), One("FLOW_SPEED", 0f), One("FLOW_TEX_SCALE", 1f), One("FLOW_WARP_STRENGTH", 0f)));
		Texture2D flowMap = HasShaderFeature(shader, "FLOW_UV_ANIMATION") ? scene?.GetSamplerTexture(material, shader, (int)CA_PLANET.SAMPLERS.FLOW_MAP) : null;
		planet.SetShaderParameter("use_flow", flowMap != null);
		if (flowMap != null)
			planet.SetShaderParameter("flow_map", flowMap);
		Texture2D atmosphere = scene?.GetSamplerTexture(material, shader, (int)CA_PLANET.SAMPLERS.ATMOSPHERE_MAP);
		if (atmosphere != null)
			planet.SetShaderParameter("atmosphere_map", atmosphere);
		Texture2D detailMap = detail ? scene?.GetSamplerTexture(material, shader, (int)CA_PLANET.SAMPLERS.DETAIL_MAP) : null;
		planet.SetShaderParameter("use_detail_map", detailMap != null);
		if (detailMap != null)
			planet.SetShaderParameter("detail_map", detailMap);
		return new MaterialResult(planet, true);
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
		bool glass = AlienSceneShaderParams.TryGetGlass(material, shader, out Color glassTint, out float glassDensity, out float glassLightness, out float glassFresnel)
			&& useTransparentBlend;
		ShaderMaterial godotMaterial = new ShaderMaterial
		{
			ResourceName = name,
			Shader = GetShadedShader(doubleSided, useTransparentBlend, secondaryDiffuse != null, glass),
			RenderPriority = useTransparentBlend ? TransparentRenderPriority : OpaqueRenderPriority,
		};
		if (glass)
		{
			godotMaterial.SetShaderParameter(GlassTintParam, Raw(glassTint));
			godotMaterial.SetShaderParameter(GlassDensityParam, glassDensity);
			godotMaterial.SetShaderParameter(GlassLightnessParam, glassLightness);
			godotMaterial.SetShaderParameter(GlassFresnelParam, glassFresnel);
			//Retail's coverage is the alpha times the vertex alpha, where the mesh carries vertex colours
			godotMaterial.SetShaderParameter(GlassVertexAlphaParam, HasShaderFeature(shader, "VERTEX_COLOUR"));
		}

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
		ApplySurfaceLayerParameters(godotMaterial, material, shader, scene, environmentScalars);
		return new MaterialResult(godotMaterial, true);
	}

	/// <summary>
	/// What CA_ENVIRONMENT draws over its tinted diffuse that the preview can show (model_reference_surface.gdshaderinc):
	/// the dirt map, faded by the vertex colours and the alpha-blend noise; the vertex AO; and a stand-in for its specular
	/// and environment reflection. Each only where the material has it.
	/// </summary>
	/// <remarks>
	/// Without the dirt, a lerp-dirt material showed its base where the game shows the dirt map, and a multiply-dirt one
	/// stayed clean; without the reflection stand-in, the dark metals - TECH_HUB's grey ceilings have a diffuse tint of
	/// 0.016, the grey is all reflection - drew black.
	/// </remarks>
	private static void ApplySurfaceLayerParameters(
		ShaderMaterial godotMaterial,
		Materials.Material material,
		Shaders.Shader shader,
		AlienScene scene,
		ModelReferenceMaterialOverrides.EnvironmentColourScalars? environmentScalars)
	{
		if (shader.Ubershader != SHADER_LIST.CA_ENVIRONMENT)
			return;

		bool vertexColour = HasShaderFeature(shader, "VERTEX_COLOUR");
		bool noise = HasShaderFeature(shader, "ALPHABLEND_NOISE");
		bool vertexAo = HasShaderFeature(shader, "VERTEX_AMBIENT_OCCLUSION");

		int dirtMode = 0;
		if (HasShaderFeature(shader, "DIRT_MAPPING"))
		{
			Texture2D dirt = scene.GetSamplerTexture(material, shader, (int)CA_ENVIRONMENT.SAMPLERS.DIRT_MAP);
			if (dirt != null)
			{
				dirtMode = HasShaderFeature(shader, "DIRT_BLEND_MULTIPLY") ? 2 : 1;
				godotMaterial.SetShaderParameter(DirtMapParam, dirt);
				godotMaterial.SetShaderParameter(DirtUvMultParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.DIRT_UV_MULT, 1f));
				float dirtPower = AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.DIRT_BLEND_MULT_SPEC_POWER, 1f);
				godotMaterial.SetShaderParameter(DirtAmountParam, Mathf.Clamp(dirtPower, 0f, 1f));
				godotMaterial.SetShaderParameter(DirtSpecPowerParam, dirtPower);
				godotMaterial.SetShaderParameter(DirtAoAmountParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.DIRT_AO_AMOUNT, 0f));
			}
		}
		godotMaterial.SetShaderParameter(DirtModeParam, dirtMode);

		/* What the game fades the alpha of a cut-out, decal or blended surface by (its clip and its blend both): with
		   vertex colours, the alpha-blend noise blend - ALPHABLEND_NOISE without dirt, so a scrape or grime overlay like the
		   doors' DECAL_Scrapes shows only where the mesh paints it, where the viewer drew all of it - or else the vertex alpha
		   (a plain vertex-coloured material already takes that through its tint). Not the vertex alpha where dirt or the noise
		   has the vertex colour for itself: the game's clip goes back to the texture and tint alone there, and taking it
		   faded out the floor grills (HAB_AIRPORT's, the tow platform's), leaving the lit panels under them bare. */
		int alphaFade = 0;
		if (vertexColour)
		{
			bool dirt = HasShaderFeature(shader, "DIRT_MAPPING");
			bool separateClip = HasShaderFeature(shader, "SEPARATE_ALPHA") && HasShaderFeature(shader, "ALPHA_TEST")
				&& HasShaderFeature(shader, "SPECULAR_MAPPING") && !HasShaderFeature(shader, "DECAL");
			if (noise && !dirt && !HasShaderFeature(shader, "EMISSIVE") && !separateClip)
				alphaFade = 2;
			else if (!ModelReferenceMaterialOverrides.TintsWithVertexColour(shader) && !noise && !dirt)
				alphaFade = 1;
		}
		godotMaterial.SetShaderParameter(AlphaFadeModeParam, alphaFade);

		//The noise moves the dirt's edge, or the alpha fade's; without either (or vertex colours to carry its blend) it does nothing here
		Texture2D noiseMap = noise && vertexColour && (dirtMode != 0 || alphaFade == 2)
			? scene.GetSamplerTexture(material, shader, (int)CA_ENVIRONMENT.SAMPLERS.ALPHABLEND_NOISE_MAP)
			: null;
		godotMaterial.SetShaderParameter(UseAlphablendNoiseParam, noiseMap != null);
		if (noiseMap != null)
		{
			godotMaterial.SetShaderParameter(AlphablendNoiseMapParam, noiseMap);
			godotMaterial.SetShaderParameter(AlphablendNoiseUvMultParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_UV_MULT, 1f));
			godotMaterial.SetShaderParameter(AlphablendNoisePowerParam, AlienSceneShaderParams.GetEnvironmentVertexValues(material, shader, CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_POWER, 1, 1f)[0]);
		}

		//The vertex colours are blend amounts and an AO term here, not a tint (ModelReferenceMaterialOverrides.TintsWithVertexColour)
		bool lanes = vertexColour && (dirtMode != 0 || vertexAo);
		godotMaterial.SetShaderParameter(UseVertexLanesParam, lanes);
		Vector4 scale = environmentScalars.HasValue ? environmentScalars.Value.VertexScale : Vector4.One;
		godotMaterial.SetShaderParameter(VertexColourScaleParam, scale);
		godotMaterial.SetShaderParameter(UseVertexAoParam, vertexAo && vertexColour);
		if (vertexAo && vertexColour)
		{
			float[] aoTint = AlienSceneShaderParams.GetEnvironmentVertexValues(material, shader, CA_ENVIRONMENT.PARAMETERS.VERT_AO_TINT, 3, 1f);
			godotMaterial.SetShaderParameter(VertexAoTintParam, new Vector3(aoTint[0], aoTint[1], aoTint[2]));
		}

		Texture2D specular = HasShaderFeature(shader, "SPECULAR_MAPPING")
			? scene.GetSamplerTexture(material, shader, (int)CA_ENVIRONMENT.SAMPLERS.SPECULAR_MAP)
			: null;
		godotMaterial.SetShaderParameter(UseSpecularParam, specular != null);
		if (specular != null)
		{
			godotMaterial.SetShaderParameter(SpecularMapParam, specular);
			godotMaterial.SetShaderParameter(SpecularUvMultParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.SPECULAR_UV_MULT, 1f));
			godotMaterial.SetShaderParameter(SpecularTintParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.SPECULAR_TINT, 0f));
			godotMaterial.SetShaderParameter(SpecularPowerParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.SPECULAR_POWER, 0f));
			godotMaterial.SetShaderParameter(FresnelIntensityParam, AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.FRESNEL_INTENSITY, 1f));
			bool environment = HasShaderFeature(shader, "ENVIRONMENT_MAPPING");
			godotMaterial.SetShaderParameter(UseEnvironmentParam, environment);
			godotMaterial.SetShaderParameter(EnvironmentMapMultParam, environment
				? AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.ENVIRONMENT_MAP_MULT, 1f)
				: 1f);
			godotMaterial.SetShaderParameter(ReflectivePlasticParam, HasShaderFeature(shader, "REFLECTIVE_PLASTIC"));
			//The cube it reflects: chrome and brass are mostly that reflection, tinted by their own colour
			Cubemap environmentCube = environment ? scene.GetSamplerCubemap(material, shader, (int)CA_ENVIRONMENT.SAMPLERS.ENVIRONMENT_MAP) : null;
			godotMaterial.SetShaderParameter(UseEnvironmentCubeParam, environmentCube != null);
			if (environmentCube != null)
				godotMaterial.SetShaderParameter(EnvironmentCubeParam, environmentCube);
		}

		/* EMISSIVE: the surface glows in what it shows - see ApplyEmissiveTint. Lights' panels, screens and signs. An instance
		   of it the level placed has its own EMISSIVE_TINT in place of this one (AlienScene.WithMoverLook), so the material
		   keeps what it was built from for that. */
		bool emissive = HasShaderFeature(shader, "EMISSIVE");
		godotMaterial.SetShaderParameter(EmissiveParam, emissive);
		if (emissive)
		{
			float[] own = AlienSceneShaderParams.GetEnvironmentPixelValues(material, shader, CA_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT, 4);
			float mult = AlienSceneShaderParams.GetEnvironmentFloat(material, shader, CA_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT, 1f);
			Vector4 tint = own == null ? Vector4.One : new Vector4(own[0], own[1], own[2], own[3]);
			ApplyEmissiveTint(godotMaterial, tint, mult);
			godotMaterial.SetMeta(EmissiveMultMeta, mult);
			//Only one with the constant takes an instance's: the game sets it on the material it draws, where it has one
			if (own != null)
				godotMaterial.SetMeta(EmissiveTintMeta, tint);
		}
		else
			ApplyEmissiveTint(godotMaterial, new Vector4(1f, 1f, 1f, 0f), 0f);
	}

	//What an EMISSIVE material was built from, for an instance's own EMISSIVE_TINT in its place (AlienScene.WithMoverLook)
	internal static readonly StringName EmissiveMultMeta = new StringName("cathode_emissive_mult");
	internal static readonly StringName EmissiveTintMeta = new StringName("cathode_emissive_tint");

	/* An EMISSIVE_TINT as CA_ENVIRONMENT reads it: the surface glows in what it shows, times the tint's colour, at a strength
	   of w times EMISSIVE_MULT - or |w| as it stands, where w is negative. A negative lane in the colour makes the glow the
	   diffuse's brightness in |colour| instead (a light fitting coloured to its light). */
	internal static void ApplyEmissiveTint(ShaderMaterial godotMaterial, Vector4 tint, float mult)
	{
		bool fromLuma = tint.X < 0f || tint.Y < 0f || tint.Z < 0f;
		godotMaterial.SetShaderParameter(EmissiveTintParam, fromLuma ? Vector3.One : new Vector3(tint.X, tint.Y, tint.Z));
		godotMaterial.SetShaderParameter(EmissiveStrengthParam, tint.W >= 0f ? tint.W * mult : -tint.W);
		godotMaterial.SetShaderParameter(EmissiveFromLumaParam, fromLuma);
		godotMaterial.SetShaderParameter(EmissiveLumaTintParam, new Vector3(Mathf.Abs(tint.X), Mathf.Abs(tint.Y), Mathf.Abs(tint.Z)));
	}

	/// <summary>Whether a material is CA_ENVIRONMENT with EMISSIVE: one the game gives each instance its own glow on.</summary>
	internal static bool IsEmissiveEnvironment(Materials.Material material)
	{
		Shaders.Shader shader = material?.Shader;
		return shader != null && shader.Ubershader == SHADER_LIST.CA_ENVIRONMENT && HasShaderFeature(shader, "EMISSIVE");
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
		godotMaterial.SetShaderParameter(SecondaryDiffuseTintParam, Raw(AlienSceneShaderParams.GetSecondaryDiffuseTint(material, shader)));
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
		diffuseTint.A *= AlienSceneShaderParams.GetDecalOpacity(material, shader);
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

		godotMaterial.SetShaderParameter(DiffuseTintParam, Raw(diffuseTint));
		godotMaterial.SetShaderParameter(VertexColourTintParam, Raw(vertexColourTint));
		//The mesh's own vertex colours tint the diffuse too, where the game's do (a mesh without them is white)
		godotMaterial.SetShaderParameter(UseVertexColourParam, ModelReferenceMaterialOverrides.TintsWithVertexColour(shader));
		godotMaterial.SetShaderParameter(DiffuseUvMultParam, AlienSceneShaderParams.GetUvScale(material, shader, shaderParams));

		godotMaterial.SetShaderParameter(UseDiffuseMapParam, diffuse != null);
		if (diffuse != null)
			godotMaterial.SetShaderParameter(DiffuseMapParam, diffuse);

		bool useSeparateAlpha = separateAlphaMap != null;
		godotMaterial.SetShaderParameter(UseSeparateAlphaMapParam, useSeparateAlpha);
		//CA_ENVIRONMENT's "green channel" flag samples the map's first channel in retail; the decal shader's samples green
		bool separateAlphaChannel = useSeparateAlpha && HasShaderFeature(shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL");
		bool separateAlphaFromRed = separateAlphaChannel && shader.Ubershader == SHADER_LIST.CA_ENVIRONMENT;
		godotMaterial.SetShaderParameter(SeparateAlphaFromGreenParam, separateAlphaChannel && !separateAlphaFromRed);
		godotMaterial.SetShaderParameter(SeparateAlphaFromRedParam, separateAlphaFromRed);
		godotMaterial.SetShaderParameter(SeparateAlphaUvMultParam, AlienSceneShaderParams.GetSeparateAlphaUvScale(material, shader));
		if (useSeparateAlpha)
			godotMaterial.SetShaderParameter(SeparateAlphaMapParam, separateAlphaMap);

		//A greyscale separate mask is its own alpha. A cut-out's diffuse without alpha is not: the game reads its .w as 1 and
		//cuts nothing there (only the vertex fade can), where taking the brightness cut holes in dark texels
		bool alphaFromLuminance = useSeparateAlpha && !AlienSceneTextures.HasTransparency(separateAlphaMap);

		godotMaterial.SetShaderParameter(AlphaFromLuminanceParam, alphaFromLuminance);
		godotMaterial.SetShaderParameter(AlphaSdfParam, HasShaderFeature(shader, "SIGNED_DISTANCE_FIELD"));
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
	/// Alpha-test cutout on the opaque shader path (discard, not alpha blending): every shader
	/// with the ALPHA_TEST feature, as the game's permutation clips.
	/// </summary>
	public static bool ShouldUseAlphaCutout(Shaders.Shader shader)
	{
		/* The ALPHA_TEST permutations clip in the pixel shader (clip(alpha * fade - 0.5), decoded masters): D3D11 has no
		   fixed-function alpha test, and the AlphaTestEnable render state is a D3D9 leftover no retail ALPHA_TEST shader sets
		   (0 of 114; 47 others set it, at an AlphaRef of 0). Gated on that state, every cut-out drew solid - the doors'
		   DECAL_Scrapes overlays, which the game fades out by their vertex colours, covered the doors in grime (19 Oct 2026).
		   Not signed-distance-field text, though: it stays blended, as the viewer doesn't sharpen the field as the game does,
		   and a hard cut at 0.5 of the mip-blurred field broke up the signs' thin strokes. */
		return shader != null && HasShaderFeature(shader, "ALPHA_TEST") && !HasShaderFeature(shader, "SIGNED_DISTANCE_FIELD");
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
	/// The shaded shader, with a secondary diffuse layer multiplied over the tinted diffuse and/or retail's glass
	/// transmission (transparent only) if asked for.
	/// </summary>
	/// <remarks>
	/// The variants are built here from the exported shaders' own source rather than shipped as shaders of their own,
	/// so they need no re-export and stay in step with the plain ones. The layer goes in just ahead of the vertex colour
	/// tint - retail applies it straight after the diffuse tint - and the glass straight after the plain alpha is set;
	/// if either line has gone from the source, that variant falls back to the shader without it and logs.
	/// </remarks>
	private static Shader GetShadedShader(bool doubleSided, bool transparent, bool secondaryDiffuse, bool glass = false)
	{
		Shader shader = GetShadedShader(doubleSided, transparent);
		glass = glass && transparent;
		if ((!secondaryDiffuse && !glass) || shader == null)
			return shader;

		int variant = (doubleSided ? 1 : 0) + (transparent ? 2 : 0) + (secondaryDiffuse ? 4 : 0) + (glass ? 8 : 0);
		if (!_shadedVariantShaders.TryGetValue(variant, out Shader built))
		{
			built = shader;
			if (glass)
				built = BuildGlassShader(built) ?? built;
			if (secondaryDiffuse)
				built = BuildSecondaryDiffuseShader(built) ?? built;
			_shadedVariantShaders[variant] = built;
		}
		return built;
	}

	private const string GlassInsertAfter = "ALPHA = alpha;";

	private const string GlassDeclarations = @"global uniform float viewer_glass_sheen;
uniform vec4 glass_tint = vec4(1.0);
uniform float glass_density = 0.0;
uniform float glass_lightness = 0.0;
uniform float glass_fresnel = 1.0;
uniform bool glass_vertex_alpha = false;

";

	// Retail's CA_ENVIRONMENT GLASS (decoded from the forward-alpha master): Beer-Lambert transmission through glass_tint
	// at glass_density along a path that lengthens towards grazing. What it lets through (luma) is how see-through the pane
	// is, and what it absorbs tints it, scaled by glass_lightness. Coverage - the separate alpha (or diffuse alpha) times the
	// mesh's vertex alpha - is solid diffuse instead (smudges, frames). Retail's fresnel comes from a lookup table: Schlick
	// stands in, and with no environment map to reflect, the fresnel's share draws as a soft grey sheen (viewer_glass_sheen:
	// 0.09 scored closest to the game on MUTHUR core, TECH_HUB, the android lab and the hospital - 0.18 left MUTHUR's server
	// racks' glass doors milky where the game's are dark).
	private const string GlassBlock = @"
	{
		float g_cover = clamp(alpha * (glass_vertex_alpha ? COLOR.a : 1.0), 0.0, 1.0);
		float g_nv = abs(dot(normalize(NORMAL), normalize(VIEW)));
		float g_path = g_nv * g_nv * 0.255 + 0.745;
		vec3 g_log = log2(max(glass_tint.rgb, vec3(0.001))) * glass_density;
		vec3 g_through = exp2(g_log / g_path);
		vec3 g_through_head_on = exp2(g_log);
		vec3 g_luma = vec3(0.299, 0.587, 0.114);
		float g_fresnel = clamp(glass_fresnel * (0.04 + 0.96 * pow(1.0 - g_nv, 5.0)), 0.0, 1.0);
		float g_keep = 1.0 - g_fresnel;
		float g_clear = 1.0 - g_cover;
		float g_alpha = clamp((1.0 - dot(g_luma, g_through) * g_keep) * g_clear + g_cover, 0.0, 1.0);
		vec3 g_colour = g_clear * (1.0 - dot(g_luma, g_through_head_on)) * g_through * g_keep * glass_lightness;
		vec3 g_sum = color.rgb * g_cover + g_colour + vec3(viewer_glass_sheen) * g_fresnel * g_clear;
		// a glow keeps the share it had before the glass (the covered part's, as the plain path blends it at its alpha)
		ALBEDO = model_reference_apply_preview_shading(g_sum / max(g_alpha, 0.001), ndl)
			+ model_reference_emission(color.rgb) * g_cover / max(g_alpha, 0.001);
		ALPHA = g_alpha;
	}";

	private static Shader BuildGlassShader(Shader plain)
	{
		string code = plain.Code;
		int fragment = string.IsNullOrEmpty(code) ? -1 : code.IndexOf("void fragment()", System.StringComparison.Ordinal);
		int anchor = fragment < 0 ? -1 : code.IndexOf(GlassInsertAfter, fragment, System.StringComparison.Ordinal);
		if (anchor < 0)
		{
			ViewerLog.PrintErr("[Materials] " + plain.ResourcePath + " has no '" + GlassInsertAfter + "' line to add glass after; glass will draw as plain alpha.");
			return null;
		}

		code = code
			.Insert(anchor + GlassInsertAfter.Length, GlassBlock.Replace("\r\n", "\n"))
			.Insert(fragment, GlassDeclarations.Replace("\r\n", "\n"));

		//Picking, zones and composite focus tell double-sided and transparent shaders by their path: keep the plain one's
		Shader shader = new Shader { Code = code };
		shader.ResourcePath = plain.ResourcePath.Replace(".gdshader", "_glass.gdshader");
		return shader;
	}

	private const string SecondaryDiffuseInsertBefore = "color = model_reference_apply_vertex_colour_tint(";

	private const string SecondaryDiffuseDeclarations = @"uniform sampler2D secondary_diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform vec2 secondary_diffuse_uv_mult = vec2(1.0);
uniform vec4 secondary_diffuse_tint = vec4(1.0);
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

	/* The game's colour constants (tints, scales) are multipliers it uses as they are. Handed to a shader as a Color, Godot
	   decodes them from sRGB on the way to the GPU - even into a uniform without source_color - so a tint of 0.33 arrived as
	   0.09: Torrens' mustard vinyl seats drew brick red, every mid tint dark. As a Vector4 they go as they are. */
	private static Vector4 Raw(Color c) => new Vector4(c.R, c.G, c.B, c.A);

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
