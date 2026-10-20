using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using System.Collections.Generic;

/// <summary>
/// A SimpleWater sheet drawn as the game draws it (CA_SIMPLEWATER, shaders/water_surface.gdshader): the alien nest's floor
/// is one, 140 m across, a finger's depth of black water over the resin with the room mirrored in it - drawn without
/// it, the nest's floor was matte brown where the game's is a black mirror. Part of how the level looks, so drawn whatever
/// the render filters say; not pickable and not greyed (the scene filter group), so a click goes through it to the floor.
///
/// The game's entity sets its material's constants as it runs (every value here wins over the material's), so everything
/// is read from the entity, at the values its parameter table gives one it doesn't set. Its ripples scroll and flow as
/// the game's do, and its caustics (WaterCaustics) shimmer on what is above or below it.
/// </summary>
public partial class WaterSurface : Node3D
{
	private const string ShaderPath = "res://shaders/water_surface.gdshader";
	private static Shader _shader;

	private static readonly StringName FogInitialParam = new StringName("fog_initial");
	private static readonly StringName FogMidParam = new StringName("fog_mid");
	private static readonly StringName FogEndParam = new StringName("fog_end");
	private static readonly StringName FogDepthsParam = new StringName("fog_depths");
	private static readonly StringName FresnelParam = new StringName("fresnel");
	private static readonly StringName ReflectiveParam = new StringName("reflective");
	private static readonly StringName SoftnessParam = new StringName("softness");
	internal static readonly StringName NormalMapParam = new StringName("normal_map");
	internal static readonly StringName UseNormalMapParam = new StringName("use_normal_map");
	internal static readonly StringName SecondaryNormalParam = new StringName("secondary_normal");
	internal static readonly StringName RippleParam = new StringName("ripple");
	internal static readonly StringName RippleStrengthParam = new StringName("ripple_strength");
	internal static readonly StringName FlowMapParam = new StringName("flow_map");
	internal static readonly StringName UseFlowParam = new StringName("use_flow");
	internal static readonly StringName FlowParam = new StringName("flow");
	internal static readonly StringName PerturbationParam = new StringName("perturbation");

	private AlienScene _scene;
	private FunctionEntity _entity;
	private MeshInstance3D _sheet;
	private ShaderMaterial _material;
	private WaterCaustics _caustics;

	public static bool IsWater(FunctionEntity function)
	{
		return function != null && function.function.IsFunctionType && function.function.AsFunctionType == FunctionType.SimpleWater;
	}

	/// <summary>The textures a water entity names, for the populate to convert with its models' (AlienScene.AddSurfaceEffectTextures).</summary>
	public static IEnumerable<string> TexturePaths(FunctionEntity function)
	{
		if (!IsWater(function))
			yield break;
		foreach (string name in new[] { "NORMAL_MAP", "FLOW_MAP" })
		{
			string path = (function.GetParameter(name)?.content as cString)?.value;
			if (!string.IsNullOrEmpty(path))
				yield return path;
		}
	}

	/// <summary>Gives a water entity's scene node its sheet; anything else is left alone.</summary>
	public static void TryAdd(AlienScene scene, FunctionEntity function, Node3D entityNode)
	{
		if (scene == null || entityNode == null || !IsWater(function))
			return;

		WaterSurface water = new WaterSurface { Name = "WaterSurface", _scene = scene, _entity = function };
		entityNode.AddChild(water);
		water.Refresh();
	}

	/// <summary>Redraws the sheet under an entity's scene node from its parameters as they are now, if it has one.</summary>
	public static void RefreshUnder(Node3D entityNode)
	{
		if (entityNode == null || !GodotObject.IsInstanceValid(entityNode))
			return;

		for (int i = 0, count = entityNode.GetChildCount(); i < count; i++)
		{
			if (entityNode.GetChild(i) is WaterSurface water)
				water.Refresh();
		}
	}

	public void Refresh()
	{
		if (_entity == null)
			return;

		//A flat sheet on the entity, half_dimensions out each way across it
		Vector3 half = GetVector("half_dimensions", Vector3.Zero).Abs();
		Vector3 size = new Vector3(half.X * 2f, 1f, half.Z * 2f);
		//Shown as its mover has it, as the surface effects are (SurfaceEffectOverlay.Refresh)
		bool shown = _scene.GetMoverShown(GetParent() as Node3D, _entity) ?? GetBool("show_on_reset", true);
		bool drawn = shown && GetBool("enable_on_reset", true) && !GetBool("deleted", false) && size.X > 0.001f && size.Z > 0.001f;
		if (!drawn)
		{
			if (_sheet != null)
				_sheet.Visible = false;
			_caustics?.HideAll();
			return;
		}

		EnsureSheet();
		_sheet.Visible = true;
		_sheet.Scale = size;
		LevelViewerMeshUtil.ApplyDrawDistance(_sheet, size);

		//Colours are kept 0-255; the game's constants are 0-1. Unset values are the parameter table's.
		_material.SetShaderParameter(FogInitialParam, FogStop("DEPTH_FOG_INITIAL_COLOUR", "DEPTH_FOG_INITIAL_ALPHA"));
		_material.SetShaderParameter(FogMidParam, FogStop("DEPTH_FOG_MIDPOINT_COLOUR", "DEPTH_FOG_MIDPOINT_ALPHA"));
		_material.SetShaderParameter(FogEndParam, FogStop("DEPTH_FOG_END_COLOUR", "DEPTH_FOG_END_ALPHA"));
		_material.SetShaderParameter(FogDepthsParam, new Vector2(GetFloat("DEPTH_FOG_MIDPOINT_DEPTH", 0.5f), GetFloat("DEPTH_FOG_END_DEPTH", 1f)));
		_material.SetShaderParameter(FresnelParam, new Vector3(GetFloat("MIN_FRESNEL", 0f), GetFloat("MAX_FRESNEL", 5f), GetFloat("FRESNEL_POWER", 0.8f)));
		_material.SetShaderParameter(ReflectiveParam, GetBool("REFLECTIVE_MAPPING", true));
		_material.SetShaderParameter(SoftnessParam, Mathf.Max(GetFloat("softness_edge", 0.005f), 0f));

		//The ripples: the normal map read twice, scrolling, and the flow map warping them
		Texture2D normalMap = WaterTextures.Mipped(_scene.GetTextureByPath(GetString("NORMAL_MAP")));
		_material.SetShaderParameter(UseNormalMapParam, normalMap != null);
		_material.SetShaderParameter(NormalMapParam, normalMap);
		_material.SetShaderParameter(SecondaryNormalParam, GetBool("SECONDARY_NORMAL_MAPPING", false));
		_material.SetShaderParameter(RippleParam, new Vector4(GetFloat("SPEED", 0.01f), GetFloat("SCALE", 1f), GetFloat("SECONDARY_SPEED", -0.01f), GetFloat("SECONDARY_SCALE", 1f)));
		_material.SetShaderParameter(RippleStrengthParam, new Vector2(GetFloat("NORMAL_MAP_STRENGTH", 1f), GetFloat("SECONDARY_NORMAL_MAP_STRENGTH", 1f)));
		Texture2D flowMap = GetBool("FLOW_MAPPING", false) ? WaterTextures.Mipped(_scene.GetTextureByPath(GetString("FLOW_MAP"))) : null;
		_material.SetShaderParameter(UseFlowParam, flowMap != null);
		_material.SetShaderParameter(FlowMapParam, flowMap);
		_material.SetShaderParameter(FlowParam, new Vector4(GetFloat("CYCLE_TIME", 10f), GetFloat("FLOW_SPEED", 1f), GetFloat("FLOW_TEX_SCALE", 4f), GetFloat("FLOW_WARP_STRENGTH", 0.5f)));
		_material.SetShaderParameter(PerturbationParam, GetFloat("REFLECTION_PERTURBATION_STRENGTH", 1f));

		//The caustics, where it has them: their speed is the ripples' times their own scalar, as the game's
		bool refractions = GetBool("CAUSTIC_REFRACTIONS", false);
		bool reflections = GetBool("CAUSTIC_REFLECTIONS", false);
		if (refractions || reflections)
		{
			_caustics ??= WaterCaustics.Create(this);
			_caustics.Refresh(_scene, half, refractions, reflections, GetString("CAUSTIC_TEXTURE"), GetFloat("CAUSTIC_HEIGHT", 10f),
				GetFloat("CAUSTIC_TEXTURE_SCALE", 1f), GetFloat("CAUSTIC_INTENSITY", 1f), GetFloat("CAUSTIC_SURFACE_WRAP", 1f),
				GetFloat("SPEED", 0.01f) * GetFloat("CAUSTIC_SPEED_SCALAR", 20f));
		}
		else
			_caustics?.HideAll();
	}

	private Vector4 FogStop(string colour, string alpha)
	{
		Vector3 c = GetVector(colour, Vector3.Zero) / 255f;
		return new Vector4(c.X, c.Y, c.Z, Mathf.Clamp(GetFloat(alpha, 1f), 0f, 1f));
	}

	private void EnsureSheet()
	{
		if (_sheet != null && GodotObject.IsInstanceValid(_sheet))
			return;

		_shader ??= GD.Load<Shader>(ShaderPath);
		//First among the see-through things: it paints over what is under it from the screen the opaque level left
		_material = new ShaderMaterial { Shader = _shader, RenderPriority = -20 };
		Mesh mesh = PreviewVisualUtility.GetSharedMesh("water_surface:plane", () => new PlaneMesh { Size = Vector2.One });
		_sheet = new MeshInstance3D { Name = "Sheet", Mesh = mesh, MaterialOverride = _material };
		LevelViewerMeshUtil.ConfigureMeshInstance(_sheet);
		_sheet.AddToGroup(LevelViewerPick.SceneFilterGroupName);
		AddChild(_sheet);
		//A plane the water's mirror can be taken from: up its own Y
		LevelViewerPlanarReflection.Register(_sheet, true);
	}

	private Parameter Get(string name) => _entity.GetParameter(name);

	private float GetFloat(string name, float fallback)
	{
		ParameterData content = Get(name)?.content;
		if (content is cFloat f)
			return f.value;
		if (content is cInteger i)
			return i.value;
		return fallback;
	}

	private bool GetBool(string name, bool fallback) => Get(name)?.content is cBool b ? b.value : fallback;

	private string GetString(string name) => (Get(name)?.content as cString)?.value;

	private Vector3 GetVector(string name, Vector3 fallback)
	{
		if (Get(name)?.content is cVector3 v)
			return new Vector3(v.value.X, v.value.Y, v.value.Z);
		return fallback;
	}
}

/// <summary>
/// The water's own copies of its ripple textures, with mips: the viewer keeps block-compressed textures as their top level
/// alone, and a normal map read without mips sparkles across the far water (the nest's tiles 512 texels over 9 m).
/// </summary>
public static class WaterTextures
{
	private static readonly Dictionary<Texture2D, Texture2D> _mipped = new Dictionary<Texture2D, Texture2D>();

	public static Texture2D Mipped(Texture2D source)
	{
		if (source == null || !GodotObject.IsInstanceValid(source))
			return null;
		if (_mipped.TryGetValue(source, out Texture2D known) && known != null && GodotObject.IsInstanceValid(known))
			return known;

		Texture2D result = source;
		Image image = source.GetImage();
		if (image != null && !image.IsEmpty())
		{
			if (image.IsCompressed())
				image.Decompress();
			if (!image.IsCompressed() && !image.HasMipmaps())
			{
				image.GenerateMipmaps();
				result = ImageTexture.CreateFromImage(image);
				result.ResourceName = source.ResourceName + " (mips)";
			}
		}
		//Dead sources go with each new one: a level's textures are all replaced by the next level's
		List<Texture2D> dead = null;
		foreach (Texture2D key in _mipped.Keys)
		{
			if (!GodotObject.IsInstanceValid(key))
				(dead ??= new List<Texture2D>()).Add(key);
		}
		if (dead != null)
		{
			foreach (Texture2D key in dead)
				_mipped.Remove(key);
		}
		_mipped[source] = result;
		return result;
	}
}
