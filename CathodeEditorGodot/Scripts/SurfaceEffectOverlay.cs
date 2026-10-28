using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;

/// <summary>
/// What a SurfaceEffectBox or SurfaceEffectSphere paints onto the level around it, drawn as the game draws it (the
/// game's CA_EFFECT_OVERLAY, shaders/surface_effect_overlay.gdshader): ENG_REACTORCORE's and HAB_AIRPORT's frost, scorch
/// marks. Part of how the level looks, so drawn whatever the render filters say - the filter's box is the entity's own
/// preview (BoxPreview / SpherePreview). Not pickable, and not given the grey-out's material (the scene filter group keeps
/// it out of both): a click through the frost reaches the wall it is on. Outside the composite focus it is hidden instead
/// (LevelViewerCompositeFocus.ScopedOverlayGroupName) - left drawn, it painted its frost in full colour over greyed walls.
/// </summary>
public partial class SurfaceEffectOverlay : Node3D
{
	private const string ShaderPath = "res://shaders/surface_effect_overlay.gdshader";
	private static Shader _shader;

	private static readonly StringName TextureMapParam = new StringName("texture_map");
	private static readonly StringName UseTextureMapParam = new StringName("use_texture_map");
	private static readonly StringName IsSphereParam = new StringName("is_sphere");
	private static readonly StringName FalloffParam = new StringName("falloff");
	private static readonly StringName IntensityParam = new StringName("intensity");
	private static readonly StringName OpacityParam = new StringName("opacity");
	private static readonly StringName SurfaceWrapParam = new StringName("surface_wrap");
	private static readonly StringName TilingParam = new StringName("tiling");
	private static readonly StringName ColourTintParam = new StringName("colour_tint");
	private static readonly StringName ColourTintOuterParam = new StringName("colour_tint_outer");

	private static readonly ShortGuid InitialValueParam = ShortGuidUtils.Generate("initial_value");
	private static readonly ShortGuid TimedInitialValueParam = ShortGuidUtils.Generate("Initial_Value");
	private static readonly ShortGuid LhsParam = ShortGuidUtils.Generate("LHS");
	private static readonly ShortGuid RhsParam = ShortGuidUtils.Generate("RHS");

	private AlienScene _scene;
	private FunctionEntity _entity;
	//The composite the entity is in: what its script-fed parameters are wired to
	private Composite _composite;
	private MeshInstance3D _volume;
	private ShaderMaterial _material;

	public static bool IsSurfaceEffect(FunctionEntity function)
	{
		return function != null && function.function.IsFunctionType
			&& (function.function.AsFunctionType == FunctionType.SurfaceEffectBox || function.function.AsFunctionType == FunctionType.SurfaceEffectSphere);
	}

	/// <summary>Gives a surface effect's scene node its overlay; anything else is left alone.</summary>
	public static void TryAdd(AlienScene scene, FunctionEntity function, Composite composite, Node3D entityNode)
	{
		if (scene == null || entityNode == null || !IsSurfaceEffect(function))
			return;

		SurfaceEffectOverlay overlay = new SurfaceEffectOverlay { Name = "SurfaceEffectOverlay", _scene = scene, _entity = function, _composite = composite };
		overlay.AddToGroup(GroupName);
		overlay.AddToGroup(LevelViewerCompositeFocus.ScopedOverlayGroupName);
		entityNode.AddChild(overlay);
		overlay.Refresh();
	}

	private const string GroupName = "surface_effect_overlays";

	/// <summary>
	/// Redraws the overlays a float edit may feed (GetStartFloat): those in the composite of the entity edited - a variable
	/// or a maths entity wired into their radius or strength - and those inside the composite it instances, whose variables
	/// that instance's parameters set. ENG_ALIEN_NEST's conduit frost grows by both.
	/// </summary>
	public static void RefreshFedBy(AlienScene scene, Composite composite, Entity edited)
	{
		SceneTree tree = scene?.IsInsideTree() == true ? scene.GetTree() : null;
		if (tree == null || composite == null)
			return;

		ShortGuid instanced = edited is FunctionEntity function && !function.function.IsFunctionType ? function.function : ShortGuid.Invalid;
		foreach (Node node in tree.GetNodesInGroup(GroupName))
		{
			if (node is SurfaceEffectOverlay overlay && overlay._composite != null
				&& (overlay._composite == composite || overlay._composite.shortGUID == instanced))
				overlay.Refresh();
		}
	}

	/// <summary>Redraws the overlay under an entity's scene node from its parameters as they are now, if it has one.</summary>
	public static void RefreshUnder(Node3D entityNode)
	{
		if (entityNode == null || !GodotObject.IsInstanceValid(entityNode))
			return;

		for (int i = 0, count = entityNode.GetChildCount(); i < count; i++)
		{
			if (entityNode.GetChild(i) is SurfaceEffectOverlay overlay)
				overlay.Refresh();
		}
	}

	public void Refresh()
	{
		if (_entity == null)
			return;

		bool sphere = _entity.function.AsFunctionType == FunctionType.SurfaceEffectSphere;
		/* A box stands on its transform, as the level's other boxes do (BoxPreview): centred on it, HAB_AIRPORT's frost slabs
		   laid across the wall's corners frosted a sliver of the pipes the game frosts whole, and ENG_REACTORCORE's 16 m
		   tall ones sank half into the floor. Standing, both match the game. A sphere is centred on it */
		Vector3 size = sphere
			? Vector3.One * (GetStartFloat("radius", 0f) * 2f)
			: GetVector("half_dimensions", Vector3.Zero).Abs() * 2f;
		/* A volume of no size, or one the level starts hidden (a scorch a script shows when something burns), draws nothing.
		   Hidden as this instance resolves it, which its mover has; one the level was loaded with and builds no mover for is
		   left out of it (HAB_CORPORATEPENT's walls: the frozen set pieces, frost boxes and all, with none built). One added
		   or shown/hidden since, or in a composite opened on its own, goes by its own flag (AlienScene.GetMoverShown) */
		bool shown = _scene.GetMoverShown(GetParent() as Node3D, _entity) ?? GetBool("show_on_reset", true);
		bool drawn = shown && GetBool("enable_on_reset", true) && !GetBool("deleted", false)
			&& size.X > 0.001f && size.Y > 0.001f && size.Z > 0.001f;
		if (!drawn)
		{
			if (_volume != null)
				_volume.Visible = false;
			return;
		}

		EnsureVolume(sphere);
		_volume.Visible = true;
		_volume.Scale = size;
		LevelViewerMeshUtil.ApplyDrawDistance(_volume, size);
		_volume.Position = sphere ? Vector3.Zero : new Vector3(0f, size.Y * 0.5f, 0f);

		Texture2D texture = _scene.GetTextureByPath(GetString("TEXTURE_MAP"));
		_material.SetShaderParameter(UseTextureMapParam, texture != null);
		if (texture != null)
			_material.SetShaderParameter(TextureMapParam, texture);
		_material.SetShaderParameter(IsSphereParam, sphere);
		_material.SetShaderParameter(FalloffParam, GetVector("FALLOFF", Vector3.One));
		_material.SetShaderParameter(IntensityParam, GetStartFloat("INTENSITY", 1f));
		_material.SetShaderParameter(OpacityParam, GetStartFloat("OPACITY", 1f));
		_material.SetShaderParameter(SurfaceWrapParam, GetFloat("SURFACE_WRAP", 0f));
		_material.SetShaderParameter(TilingParam, new Vector3(GetFloat("TILING_ZY", 1f), GetFloat("TILING_ZX", 1f), GetFloat("TILING_XY", 1f)));
		//Colours are kept 0-255; the game's constants are 0-1
		_material.SetShaderParameter(ColourTintParam, GetVector("COLOUR_TINT", Vector3.One * 255f) / 255f);
		_material.SetShaderParameter(ColourTintOuterParam, GetVector("COLOUR_TINT_OUTER", Vector3.One * 255f) / 255f);
	}

	private void EnsureVolume(bool sphere)
	{
		Mesh mesh = sphere
			? PreviewVisualUtility.GetSharedMesh("surface_effect:sphere", () => new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 24, Rings = 12 })
			: PreviewVisualUtility.GetSharedMesh("surface_effect:box", () => new BoxMesh { Size = Vector3.One });
		if (_volume != null && GodotObject.IsInstanceValid(_volume))
		{
			_volume.Mesh = mesh;
			return;
		}

		_shader ??= GD.Load<Shader>(ShaderPath);
		//Early among the see-through things, as the game's lands in its g-buffer before any of them draw
		_material = new ShaderMaterial { Shader = _shader, RenderPriority = -10 };
		_volume = new MeshInstance3D { Name = "Volume", Mesh = mesh, MaterialOverride = _material };
		LevelViewerMeshUtil.ConfigureMeshInstance(_volume);
		_volume.AddToGroup(LevelViewerPick.SceneFilterGroupName);
		AddChild(_volume);
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

	/* A parameter the script feeds starts at what that gives, not at the value typed on the entity: ENG_ALIEN_NEST's
	   conduits grow a 20 m frost sphere from nothing when they vent (radius = FloatMultiply(VariableFloat, 10), the
	   variable 0 until then), and the typed 10 drew it over the whole nest. Variables (as the composite's instance sets
	   them), timed interpolators and the four float operators are followed - CathodeLib's Instancing evaluates the rest of
	   the language - and anything else leaves the typed value. */
	private float GetStartFloat(string name, float fallback)
	{
		float? fed = _composite == null ? null : ResolveInput(_entity, ShortGuidUtils.Generate(name), 0);
		return fed ?? GetFloat(name, fallback);
	}

	private float? ResolveInput(Entity entity, ShortGuid param, int depth)
	{
		if (depth > 8)
			return null;
		foreach (EntityConnector link in entity.childLinks)
		{
			if (link.thisParamID == param)
				return ResolveOutput(_composite.GetEntityByID(link.linkedEntityID), link.linkedParamID, depth + 1);
		}
		return null;
	}

	//An input: the link into it, or its own value (absent: 0, as the game starts it)
	private float? ResolveValue(Entity entity, ShortGuid param, int depth)
	{
		float? fed = ResolveInput(entity, param, depth);
		if (fed.HasValue || entity.childLinks.Exists(l => l.thisParamID == param))
			return fed;
		return AsFloat(entity.GetParameter(param)?.content) ?? 0f;
	}

	private float? ResolveOutput(Entity source, ShortGuid output, int depth)
	{
		switch (source)
		{
			case VariableEntity variable:
				//The composite's instance sets it, or it keeps its own value
				FunctionEntity instance = GetParent()?.GetParent() is Node3D instanceNode && _scene.NodeEntities.TryGetValue(instanceNode, out Entity owner) ? owner as FunctionEntity : null;
				return AsFloat(instance?.GetParameter(output)?.content) ?? AsFloat(variable.GetParameter(output)?.content);
			case FunctionEntity function when function.function.IsFunctionType:
				switch (function.function.AsFunctionType)
				{
					case FunctionType.VariableFloat:
						return ResolveValue(function, InitialValueParam, depth);
					//Not started yet: where it starts from (as CathodeLib's Instancing takes it)
					case FunctionType.FloatLinearInterpolateTimed:
						return ResolveValue(function, TimedInitialValueParam, depth);
					case FunctionType.FloatMultiply:
					case FunctionType.FloatAdd:
					case FunctionType.FloatSubtract:
					case FunctionType.FloatDivide:
						float? lhs = ResolveValue(function, LhsParam, depth), rhs = ResolveValue(function, RhsParam, depth);
						if (!lhs.HasValue || !rhs.HasValue)
							return null;
						return function.function.AsFunctionType switch
						{
							FunctionType.FloatMultiply => lhs * rhs,
							FunctionType.FloatAdd => lhs + rhs,
							FunctionType.FloatSubtract => lhs - rhs,
							_ => rhs.Value == 0f ? 0f : lhs / rhs,
						};
				}
				return null;
		}
		return null;
	}

	private static float? AsFloat(ParameterData content)
	{
		if (content is cFloat f)
			return f.value;
		if (content is cInteger i)
			return i.value;
		return null;
	}

	private bool GetBool(string name, bool fallback) => Get(name)?.content is cBool b ? b.value : fallback;

	private string GetString(string name) => Get(name)?.content is cString s ? s.value : null;

	private Vector3 GetVector(string name, Vector3 fallback)
	{
		if (Get(name)?.content is cVector3 v)
			return new Vector3(v.value.X, v.value.Y, v.value.Z);
		return fallback;
	}
}
