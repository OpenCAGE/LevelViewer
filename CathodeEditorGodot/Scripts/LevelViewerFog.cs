using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using System.Collections.Generic;

/// <summary>
/// The level's fog, drawn as the game's atmosphere filter draws it (shaders/atmosphere_fog.gdshader): a full-screen pass over
/// the opaque scene, by view depth.
///
/// A FogSetting only fogs the frame once something fires its trigger, and the levels' scripts fire theirs as the player goes
/// on: TECH_HUB's and SCI_HUB's are never fired, the tow platform's when the player is cocooned, the android theatre's by a
/// flicker, LV426's and challenge map 9's when a variable feeding them is changed (their variables start at values that would
/// black the level out - challenge map 9's density variable starts at 1). What is drawn is the one the level fires as it
/// starts, from a ThinkOnce or a Thinker that starts on reset (HAB_CORPORATEPENT, the alien nest), with its values as the
/// script feeds them then: a VariableVector's colour, a platform constant's PC value, a variable's initial value, float maths.
/// None fired, no fog, as in the game. Drawn in the main view only: composite previews render their own layer.
///
/// A plain class holding a MeshInstance3D, as LevelViewerGalaxy is.
/// </summary>
public static class LevelViewerFog
{
	private const string ShaderPath = "res://shaders/atmosphere_fog.gdshader";

	private static readonly StringName FogSettingParam = new StringName("fog_setting");
	private static readonly StringName FogNearParam = new StringName("fog_near");
	private static readonly StringName FogFarParam = new StringName("fog_far");

	private static readonly ShortGuid TriggerParam = ShortGuidUtils.Generate("trigger");
	private static readonly ShortGuid StartOnResetParam = ShortGuidUtils.Generate("start_on_reset");
	private static readonly ShortGuid OnThinkParam = ShortGuidUtils.Generate("on_think");

	private static MeshInstance3D _quad;
	private static ShaderMaterial _material;
	//The setting drawn and the composite it is in: an edit to either, or to what feeds it, redraws it (RefreshIfIn)
	private static FunctionEntity _fog;
	private static Composite _fogComposite;

	/// <summary>Draws the fog the level starts with, or none (a null or level-less script).</summary>
	public static void SetFog(Node host, Commands commands)
	{
		_fog = null;
		_fogComposite = null;
		if (commands != null)
			FindStartingFog(commands, out _fog, out _fogComposite);
		Apply(host);
		if (_fog != null)
			ViewerLog.Print("[Fog] " + _fogComposite.name + " :: " + ((_fog.GetParameter("name")?.content as cString)?.value ?? _fog.shortGUID.ToString()) + ".");
	}

	/// <summary>Redraws the fog after an edit in <paramref name="composite"/>, if that is where the fog drawn is set.</summary>
	public static void RefreshIfIn(Node host, Composite composite)
	{
		if (_fog != null && composite != null && composite == _fogComposite)
			Apply(host);
	}

	private static void Apply(Node host)
	{
		if (_fog == null)
		{
			if (_quad != null && GodotObject.IsInstanceValid(_quad))
				_quad.Visible = false;
			return;
		}

		MeshInstance3D quad = EnsureQuad(host);
		if (quad == null)
			return;

		float linearDistance = Float(_fog, "linear_distance", 0f);
		float linearDensity = Float(_fog, "linear_density", 0f);
		float exponentialDensity = Float(_fog, "exponential_density", 0f);
		float maxDistance = Float(_fog, "max_distance", 850f);
		_material.SetShaderParameter(FogSettingParam, new Vector4(linearDistance, linearDensity, exponentialDensity, maxDistance));
		_material.SetShaderParameter(FogNearParam, Colour(_fog, "near_colour"));
		_material.SetShaderParameter(FogFarParam, Colour(_fog, "far_colour"));
		quad.Visible = linearDensity > 0f || exponentialDensity > 0f;
	}

	private static MeshInstance3D EnsureQuad(Node host)
	{
		if (_quad != null && GodotObject.IsInstanceValid(_quad))
			return _quad;
		if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
			return null;
		Node parent = host.GetTree().CurrentScene ?? host.GetParent();
		if (parent == null)
			return null;

		_material = new ShaderMaterial
		{
			Shader = GD.Load<Shader>(ShaderPath),
			//Over the opaque scene and the stars, under the glass and water drawn after it, as the game's filter runs before them
			RenderPriority = (int)Material.RenderPriorityMin + 1,
		};
		_quad = new MeshInstance3D
		{
			Name = "LevelFog",
			Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
			MaterialOverride = _material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			GIMode = GeometryInstance3D.GIModeEnum.Disabled,
			//The shader puts it over the screen wherever the camera is: never cull it
			CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
			Visible = false,
		};
		_quad.AddToGroup(LevelViewerPick.SceneFilterGroupName);
		parent.AddChild(_quad);
		return _quad;
	}

	/* The FogSetting the level fires as it starts: outside the archetypes and effects (gas clouds, damage over time - fired by
	   what happens), with its trigger fed by a ThinkOnce or a Thinker that starts on reset. The first found, in script
	   order. */
	private static void FindStartingFog(Commands commands, out FunctionEntity fog, out Composite owner)
	{
		fog = null;
		owner = null;
		foreach (Composite composite in commands.Entries)
		{
			if (composite?.functions == null || IsLibrary(composite.name))
				continue;
			foreach (FunctionEntity function in composite.functions)
			{
				if (!function.function.IsFunctionType || function.function.AsFunctionType != FunctionType.FogSetting)
					continue;
				if (!IsFiredAtStart(composite, function))
					continue;
				fog = function;
				owner = composite;
				return;
			}
		}
	}

	private static bool IsLibrary(string name)
	{
		return name == null || name.Contains("Archetypes") || name.Contains("Required_Assets") || name.Contains("FX_Library");
	}

	private static bool IsFiredAtStart(Composite composite, FunctionEntity fog)
	{
		foreach (Entity source in composite.GetEntities())
		{
			foreach (EntityConnector link in source.childLinks)
			{
				if (link.linkedEntityID != fog.shortGUID || link.linkedParamID != TriggerParam)
					continue;
				if (source is not FunctionEntity function || !function.function.IsFunctionType)
					continue;
				switch (function.function.AsFunctionType)
				{
					case FunctionType.ThinkOnce:
						if (link.thisParamID == OnThinkParam && (function.GetParameter(StartOnResetParam)?.content as cBool)?.value != false)
							return true;
						break;
					case FunctionType.Thinker:
						if (link.thisParamID == OnThinkParam && (function.GetParameter(StartOnResetParam)?.content as cBool)?.value == true)
							return true;
						break;
				}
			}
		}
		return false;
	}

	//A value as the script feeds it at the start (the one wired in, else the one typed)
	private static float Float(FunctionEntity entity, string name, float fallback)
	{
		return ResolveFloat(entity, ShortGuidUtils.Generate(name), 0) ?? fallback;
	}

	private static float? ResolveFloat(Entity entity, ShortGuid param, int depth)
	{
		if (entity == null || depth > 8)
			return null;
		foreach (EntityConnector link in entity.childLinks)
		{
			if (link.thisParamID == param)
			{
				float? fed = Output(_fogComposite?.GetEntityByID(link.linkedEntityID), depth + 1);
				if (fed.HasValue)
					return fed;
			}
		}
		return AsFloat(entity.GetParameter(param)?.content);
	}

	private static float? Output(Entity source, int depth)
	{
		if (source is VariableEntity variable)
			return AsFloat(variable.parameters.Count != 0 ? variable.parameters[0].content : null);
		if (source is not FunctionEntity function || !function.function.IsFunctionType)
			return null;
		switch (function.function.AsFunctionType)
		{
			case FunctionType.VariableFloat:
				return ResolveFloat(function, ShortGuidUtils.Generate("initial_value"), depth) ?? 0f;
			//What the PC build takes (it is the NextGen platform)
			case FunctionType.PlatformConstantFloat:
				return ResolveFloat(function, ShortGuidUtils.Generate("NextGen"), depth) ?? 0f;
			case FunctionType.FloatMultiply:
			case FunctionType.FloatAdd:
			case FunctionType.FloatSubtract:
			case FunctionType.FloatDivide:
				float lhs = ResolveFloat(function, ShortGuidUtils.Generate("LHS"), depth) ?? 0f;
				float rhs = ResolveFloat(function, ShortGuidUtils.Generate("RHS"), depth) ?? 0f;
				return function.function.AsFunctionType switch
				{
					FunctionType.FloatMultiply => lhs * rhs,
					FunctionType.FloatAdd => lhs + rhs,
					FunctionType.FloatSubtract => lhs - rhs,
					_ => rhs == 0f ? 0f : lhs / rhs,
				};
		}
		return null;
	}

	//A colour, 0-255: typed, or fed from a VariableVector's initial value
	private static Vector3 Colour(FunctionEntity entity, string name)
	{
		ShortGuid param = ShortGuidUtils.Generate(name);
		foreach (EntityConnector link in entity.childLinks)
		{
			if (link.thisParamID != param || _fogComposite?.GetEntityByID(link.linkedEntityID) is not FunctionEntity source
				|| !source.function.IsFunctionType || source.function.AsFunctionType != FunctionType.VariableVector)
				continue;
			return new Vector3(
				ResolveFloat(source, ShortGuidUtils.Generate("initial_x"), 1) ?? 0f,
				ResolveFloat(source, ShortGuidUtils.Generate("initial_y"), 1) ?? 0f,
				ResolveFloat(source, ShortGuidUtils.Generate("initial_z"), 1) ?? 0f);
		}
		return entity.GetParameter(param)?.content is cVector3 v ? new Vector3(v.value.X, v.value.Y, v.value.Z) : new Vector3(255f, 255f, 255f);
	}

	private static float? AsFloat(ParameterData content)
	{
		if (content is cFloat f)
			return f.value;
		if (content is cInteger i)
			return i.value;
		return null;
	}
}
