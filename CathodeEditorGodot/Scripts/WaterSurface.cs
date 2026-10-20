using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;

/// <summary>
/// A SimpleWater sheet drawn as the game draws it (CA_SIMPLEWATER, shaders/water_surface.gdshader): the alien nest's floor
/// is one, 140 m across, a finger's depth of black water over the resin with the room mirrored in it - drawn without
/// it, the nest's floor was matte brown where the game's is a black mirror. Part of how the level looks, so drawn whatever
/// the render filters say; not pickable and not greyed (the scene filter group), so a click goes through it to the floor.
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

	private AlienScene _scene;
	private FunctionEntity _entity;
	private MeshInstance3D _sheet;
	private ShaderMaterial _material;

	public static bool IsWater(FunctionEntity function)
	{
		return function != null && function.function.IsFunctionType && function.function.AsFunctionType == FunctionType.SimpleWater;
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
			return;
		}

		EnsureSheet();
		_sheet.Visible = true;
		_sheet.Scale = size;
		LevelViewerMeshUtil.ApplyDrawDistance(_sheet, size);

		//Colours are kept 0-255; the game's constants are 0-1
		_material.SetShaderParameter(FogInitialParam, FogStop("DEPTH_FOG_INITIAL_COLOUR", "DEPTH_FOG_INITIAL_ALPHA", 0f));
		_material.SetShaderParameter(FogMidParam, FogStop("DEPTH_FOG_MIDPOINT_COLOUR", "DEPTH_FOG_MIDPOINT_ALPHA", 0.5f));
		_material.SetShaderParameter(FogEndParam, FogStop("DEPTH_FOG_END_COLOUR", "DEPTH_FOG_END_ALPHA", 1f));
		_material.SetShaderParameter(FogDepthsParam, new Vector2(GetFloat("DEPTH_FOG_MIDPOINT_DEPTH", 0.5f), GetFloat("DEPTH_FOG_END_DEPTH", 1f)));
		_material.SetShaderParameter(FresnelParam, new Vector3(GetFloat("MIN_FRESNEL", 0f), GetFloat("MAX_FRESNEL", 1f), GetFloat("FRESNEL_POWER", 1f)));
		_material.SetShaderParameter(ReflectiveParam, GetBool("REFLECTIVE_MAPPING", true));
		_material.SetShaderParameter(SoftnessParam, Mathf.Max(GetFloat("softness_edge", 0f), 0f));
	}

	private Vector4 FogStop(string colour, string alpha, float fallbackAlpha)
	{
		Vector3 c = GetVector(colour, Vector3.Zero) / 255f;
		return new Vector4(c.X, c.Y, c.Z, Mathf.Clamp(GetFloat(alpha, fallbackAlpha), 0f, 1f));
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

	private Vector3 GetVector(string name, Vector3 fallback)
	{
		if (Get(name)?.content is cVector3 v)
			return new Vector3(v.value.X, v.value.Y, v.value.Z);
		return fallback;
	}
}
