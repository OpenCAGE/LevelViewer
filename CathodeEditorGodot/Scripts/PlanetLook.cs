using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;

/// <summary>
/// What a planet mesh (a CA_PLANET material: shaders/planet.gdshader) takes from its Planet entity, as the game's
/// PLANET_ENTITY::update_instance_gpu_constants gives its instance: the parallax scale it is grown by about its centre, the
/// Parallax_Position it is seen from, the sun it is lit from, its overbright, its light wrap and penumbra, its atmosphere
/// edge's transparency and falloff, and its detail map's scale.
///
/// The model sits in a SpaceVista composite (AYZ\SpaceVista\Gas_Giant, \Moon_1 ...) placed by the level, ten metres from the
/// composite's Parallax_Position sphere; the Planet entity there names its instance as planet_resource, and most of its
/// values are fed from the composite's variables, which the placement sets (the tow platform's gas giant: ParallaxScale 8.5
/// - forty degrees of sky across). A mesh with no Planet found draws at scale 1, lit from above.
///
/// It is drawn at the sky wherever its few metres of mesh are, so it is never culled, picked or greyed out.
/// </summary>
public static class PlanetLook
{
	private static readonly ShortGuid PlanetResourceParam = ShortGuidUtils.Generate("planet_resource");
	private static readonly ShortGuid ParallaxPositionParam = ShortGuidUtils.Generate("parallax_position");
	private static readonly ShortGuid SunPositionParam = ShortGuidUtils.Generate("sun_position");
	private static readonly ShortGuid PositionParam = ShortGuidUtils.Generate("position");

	public static bool IsPlanet(GeometryInstance3D instance)
	{
		return instance?.MaterialOverride is ShaderMaterial material && material.Shader != null
			&& material.Shader.ResourcePath == AlienSceneMaterials.PlanetShaderPath;
	}

	/// <summary>Gives a planet mesh its Planet entity's values (after it is in the tree); anything else is left alone.</summary>
	public static void TryApply(AlienScene scene, MeshInstance3D mesh)
	{
		if (scene == null || mesh == null || !IsPlanet(mesh))
			return;

		mesh.CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f));
		mesh.VisibilityRangeEnd = 0f;
		mesh.VisibilityRangeEndMargin = 0f;
		if (!mesh.IsInGroup(LevelViewerPick.SceneFilterGroupName))
			mesh.AddToGroup(LevelViewerPick.SceneFilterGroupName);

		//Up from the mesh: the model's composite instance, then the SpaceVista composite instance holding the Planet
		Node3D modelInstanceNode = null, vistaNode = null;
		Entity modelInstance = null;
		FunctionEntity vista = null;
		for (Node node = mesh.GetParent(); node != null; node = node.GetParent())
		{
			if (node is not Node3D node3D || !scene.NodeEntities.TryGetValue(node3D, out Entity entity)
				|| entity is not FunctionEntity function || function.function.IsFunctionType)
				continue;
			if (modelInstanceNode == null)
			{
				modelInstanceNode = node3D;
				modelInstance = entity;
				continue;
			}
			vistaNode = node3D;
			vista = function;
			break;
		}
		Composite composite = vista == null ? null : scene.Content?.Level?.Commands?.GetComposite(vista.function);
		FunctionEntity planet = composite == null ? null : FindPlanet(composite, modelInstance);
		if (planet == null)
			return;

		Resolver values = new Resolver(composite, vista);
		mesh.SetInstanceShaderParameter("parallax_scale", values.Float(planet, "parallax_scale", 1f));
		mesh.SetInstanceShaderParameter("instance_overbright", values.Float(planet, "overbright_scalar", 1f));
		mesh.SetInstanceShaderParameter("light_wrap_angle_scalar", values.Float(planet, "light_wrap_angle_scalar", 1f));
		mesh.SetInstanceShaderParameter("penumbra_falloff_scalar", values.Float(planet, "penumbra_falloff_power_scalar", 1f));
		mesh.SetInstanceShaderParameter("edge_transparency", values.Float(planet, "atmosphere_edge_transparency", 1f));
		mesh.SetInstanceShaderParameter("edge_falloff_power", values.Float(planet, "atmosphere_edge_falloff_power", 1f));
		mesh.SetInstanceShaderParameter("detail_uv_scale", values.Float(planet, "detail_uv_scale", 1f));
		mesh.SetInstanceShaderParameter("instance_flow", new Vector4(values.Float(planet, "flow_cycle_time", 1f), values.Float(planet, "flow_speed", 1f),
			values.Float(planet, "flow_tex_scale", 1f), values.Float(planet, "flow_warp_strength", 1f)));
		mesh.SetInstanceShaderParameter("instance_scroll", new Vector2(values.Float(planet, "atmosphere_scroll_speed", 0f), values.Float(planet, "atmosphere_detail_scroll_speed", 0f)));

		/* Both positions go to the shader from the planet's centre: composed by hand (a bulk populate builds out of the tree),
		   the content's recentring above them all cancels out */
		Vector3 centre = WorldOf(mesh).Origin;
		Vector3? anchor = values.Position(scene, planet, ParallaxPositionParam, vistaNode);
		mesh.SetInstanceShaderParameter("anchor_offset", (anchor ?? WorldOf(vistaNode).Origin) - centre);
		Vector3? sun = values.Position(scene, planet, SunPositionParam, vistaNode);
		mesh.SetInstanceShaderParameter("sun_offset", sun.HasValue ? sun.Value - centre : Vector3.Up * 1000f);
	}

	private static Transform3D WorldOf(Node3D node)
	{
		Transform3D world = Transform3D.Identity;
		for (Node current = node; current is Node3D node3D; current = current.GetParent())
			world = node3D.Transform * world;
		return world;
	}

	private static FunctionEntity FindPlanet(Composite composite, Entity modelInstance)
	{
		if (modelInstance == null)
			return null;
		foreach (FunctionEntity function in composite.functions)
		{
			if (!function.function.IsFunctionType || function.function.AsFunctionType != FunctionType.Planet)
				continue;
			foreach (EntityConnector link in function.childLinks)
			{
				if (link.thisParamID == PlanetResourceParam && link.linkedEntityID == modelInstance.shortGUID)
					return function;
			}
		}
		return null;
	}

	/* A Planet's value as the script feeds it: wired from one of the composite's variables (as the placement sets it, or the
	   variable's own value), a float sum, or a positioned entity (the Parallax_Position sphere) - or the one typed on it */
	private sealed class Resolver
	{
		private readonly Composite _composite;
		private readonly FunctionEntity _placement;

		public Resolver(Composite composite, FunctionEntity placement)
		{
			_composite = composite;
			_placement = placement;
		}

		public float Float(Entity entity, string name, float fallback) => Float(entity, ShortGuidUtils.Generate(name), 0) ?? fallback;

		private float? Float(Entity entity, ShortGuid param, int depth)
		{
			if (entity == null || depth > 8)
				return null;
			foreach (EntityConnector link in entity.childLinks)
			{
				if (link.thisParamID != param)
					continue;
				Entity source = _composite.GetEntityByID(link.linkedEntityID);
				if (source is VariableEntity)
					return AsFloat(_placement?.GetParameter(link.linkedParamID)?.content) ?? AsFloat(source.GetParameter(link.linkedParamID)?.content);
				if (source is FunctionEntity function && function.function.IsFunctionType)
				{
					switch (function.function.AsFunctionType)
					{
						case FunctionType.FloatMultiply:
						case FunctionType.FloatAdd:
						case FunctionType.FloatSubtract:
						case FunctionType.FloatDivide:
							float lhs = Float(function, ShortGuidUtils.Generate("LHS"), depth + 1) ?? 0f;
							float rhs = Float(function, ShortGuidUtils.Generate("RHS"), depth + 1) ?? 0f;
							return function.function.AsFunctionType switch
							{
								FunctionType.FloatMultiply => lhs * rhs,
								FunctionType.FloatAdd => lhs + rhs,
								FunctionType.FloatSubtract => lhs - rhs,
								_ => rhs == 0f ? 0f : lhs / rhs,
							};
						case FunctionType.VariableFloat:
							return Float(function, ShortGuidUtils.Generate("initial_value"), depth + 1);
					}
				}
			}
			return AsFloat(entity.GetParameter(param)?.content);
		}

		/* Where a position comes from, in the viewer's space: an entity's position (the Parallax_Position sphere), or one of
		   the composite's variables - as the level wires it into the placement (the tow platform's sun is an entity beside
		   the placement, so in the level's space), as the placement sets it, or the variable's own value (the composite's
		   space) - or the one typed on it */
		public Vector3? Position(AlienScene scene, Entity entity, ShortGuid param, Node3D vistaNode)
		{
			Transform3D vistaSpace = WorldOf(vistaNode);
			foreach (EntityConnector link in entity.childLinks)
			{
				if (link.thisParamID != param)
					continue;
				Entity source = _composite.GetEntityByID(link.linkedEntityID);
				if (source is VariableEntity)
				{
					if (_placement != null)
					{
						foreach (EntityConnector fed in _placement.childLinks)
						{
							if (fed.thisParamID == link.linkedParamID && FindSibling(scene, vistaNode, fed.linkedEntityID) is Node3D wired)
								return WorldOf(wired).Origin;
						}
					}
					ParameterData value = _placement?.GetParameter(link.linkedParamID)?.content ?? source.GetParameter(link.linkedParamID)?.content;
					if (value is cTransform variable)
						return vistaSpace * CathodeCoordinates.PositionToGodot(variable.position);
					continue;
				}
				if (source?.GetParameter(PositionParam)?.content is cTransform placed)
					return vistaSpace * CathodeCoordinates.PositionToGodot(placed.position);
			}
			return entity.GetParameter(param)?.content is cTransform own ? vistaSpace * CathodeCoordinates.PositionToGodot(own.position) : null;
		}

		//An entity's node beside the placement (in the composite the placement is in)
		private static Node3D FindSibling(AlienScene scene, Node3D vistaNode, ShortGuid id)
		{
			Node parent = vistaNode?.GetParent();
			if (parent == null)
				return null;
			for (int i = 0, count = parent.GetChildCount(); i < count; i++)
			{
				if (parent.GetChild(i) is Node3D sibling && scene.NodeEntities.TryGetValue(sibling, out Entity entity) && entity.shortGUID == id)
					return sibling;
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
	}
}
