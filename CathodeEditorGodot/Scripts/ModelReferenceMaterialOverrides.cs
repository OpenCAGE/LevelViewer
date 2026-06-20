using CATHODE;

using CATHODE.Scripting;

using CATHODE.Scripting.Internal;

using CATHODE.ShaderTypes;

using CathodeLib;

using Godot;

using System;

using System.Collections.Generic;



/// <summary>

/// ModelReference entity parameters that override resolved renderables or preview shading.

/// </summary>

public static class ModelReferenceMaterialOverrides

{

	public const string MaterialParameterName = "material";

	public const string VertexColourScaleParameterName = "vertex_colour_scale";

	public const string VertexOpacityScaleParameterName = "vertex_opacity_scale";

	public const string DiffuseColourScaleParameterName = "diffuse_colour_scale";

	public const string DiffuseOpacityScaleParameterName = "diffuse_opacity_scale";

	public const string ModelReferenceEntityMetaKey = "model_reference_entity_id";

	public const string OverrideParameterEntityMetaKey = "model_reference_override_entity_id";



	private static readonly ShortGuid MaterialParameterId = ShortGuidUtils.Generate(MaterialParameterName);

	private static readonly ShortGuid VertexColourScaleParameterId = ShortGuidUtils.Generate(VertexColourScaleParameterName);

	private static readonly ShortGuid VertexOpacityScaleParameterId = ShortGuidUtils.Generate(VertexOpacityScaleParameterName);

	private static readonly ShortGuid DiffuseColourScaleParameterId = ShortGuidUtils.Generate(DiffuseColourScaleParameterName);

	private static readonly ShortGuid DiffuseOpacityScaleParameterId = ShortGuidUtils.Generate(DiffuseOpacityScaleParameterName);



	public readonly struct EnvironmentColourScalars

	{

		public EnvironmentColourScalars(Vector4 vertex, Vector4 diffuse)

		{

			Vertex = vertex;

			Diffuse = diffuse;

		}



		public Vector4 Vertex { get; }

		public Vector4 Diffuse { get; }



		public bool IsDefault =>

			Mathf.IsEqualApprox(Vertex.X, 1f) && Mathf.IsEqualApprox(Vertex.Y, 1f)

			&& Mathf.IsEqualApprox(Vertex.Z, 1f) && Mathf.IsEqualApprox(Vertex.W, 1f)

			&& Mathf.IsEqualApprox(Diffuse.X, 1f) && Mathf.IsEqualApprox(Diffuse.Y, 1f)

			&& Mathf.IsEqualApprox(Diffuse.Z, 1f) && Mathf.IsEqualApprox(Diffuse.W, 1f);

	}



	public static bool IsModelReferenceOverrideParameter(ShortGuid parameterId)

	{

		return parameterId == MaterialParameterId

			|| parameterId == VertexColourScaleParameterId

			|| parameterId == VertexOpacityScaleParameterId

			|| parameterId == DiffuseColourScaleParameterId

			|| parameterId == DiffuseOpacityScaleParameterId;

	}

	/// <summary>
	/// True when a parameter entity (typically an alias) supplies a <c>material</c> override
	/// beyond what the prewarm cache already applied on the ModelReference entity.
	/// </summary>
	public static bool NeedsInstanceMaterialRemap(Entity parameterEntity, Entity fallbackEntity)
	{
		if (parameterEntity == null || ReferenceEquals(parameterEntity, fallbackEntity))
			return false;

		return !string.IsNullOrWhiteSpace(TryGetStringParameter(parameterEntity, fallbackEntity, MaterialParameterName));
	}



	/// <summary>

	/// Finds an alias in the scene that points at <paramref name="renderTarget"/> and resolves to

	/// <paramref name="modelReferenceEntity"/>.

	/// </summary>

	public static bool TryFindAliasParameterEntity(

		IReadOnlyDictionary<Node3D, Entity> nodeEntities,

		Node3D renderTarget,

		Entity modelReferenceEntity,

		Commands commands,

		out AliasEntity aliasEntity)

	{

		aliasEntity = null;

		if (nodeEntities == null || renderTarget == null || modelReferenceEntity == null || commands == null)

			return false;



		AliasEntity bestAlias = null;

		int bestDepth = int.MaxValue;

		foreach (KeyValuePair<Node3D, Entity> entry in nodeEntities)

		{

			if (entry.Key is not EntityOverride aliasOverride)

				continue;

			if (entry.Value is not AliasEntity alias)

				continue;

			if (aliasOverride.PointedEntity != renderTarget)

				continue;

			if (!entry.Key.HasMeta(AlienScene.OwnerCompositeMetaKey))

				continue;



			Composite ownerComposite = commands.GetComposite(

				new ShortGuid(entry.Key.GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32()));

			if (ownerComposite == null)

				continue;



			(_, Entity targetEntity) = commands.Utils.GetResolvedTarget(

				commands.Utils.ResolveAlias(alias, ownerComposite));

			if (targetEntity != modelReferenceEntity)

				continue;



			int depth = GetSceneNodeDepth(entry.Key);

			if (depth >= bestDepth)

				continue;



			bestDepth = depth;

			bestAlias = alias;

		}



		if (bestAlias == null)

			return false;



		aliasEntity = bestAlias;

		return true;

	}

	/// <summary>
	/// Builds a render-target → alias map for fast bulk population.
	/// </summary>
	public static Dictionary<Node3D, Entity> BuildAliasParameterEntityIndex(
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		Commands commands)
	{
		Dictionary<Node3D, Entity> index = new Dictionary<Node3D, Entity>();
		if (nodeEntities == null || commands == null)
			return index;

		Dictionary<Node3D, int> bestDepthByRenderTarget = new Dictionary<Node3D, int>();
		foreach (KeyValuePair<Node3D, Entity> entry in nodeEntities)
		{
			if (entry.Key is not EntityOverride aliasOverride)
				continue;
			if (entry.Value is not AliasEntity alias)
				continue;
			Node3D renderTarget = aliasOverride.PointedEntity;
			if (renderTarget == null)
				continue;
			if (!entry.Key.HasMeta(AlienScene.OwnerCompositeMetaKey))
				continue;

			Composite ownerComposite = commands.GetComposite(
				new ShortGuid(entry.Key.GetMeta(AlienScene.OwnerCompositeMetaKey).AsUInt32()));
			if (ownerComposite == null)
				continue;

			(_, Entity targetEntity) = commands.Utils.GetResolvedTarget(
				commands.Utils.ResolveAlias(alias, ownerComposite));
			if (targetEntity is not FunctionEntity targetFunction
				|| targetFunction.function.AsFunctionType != FunctionType.ModelReference)
			{
				continue;
			}

			int depth = GetSceneNodeDepth(entry.Key);
			if (bestDepthByRenderTarget.TryGetValue(renderTarget, out int existingDepth) && depth >= existingDepth)
				continue;

			bestDepthByRenderTarget[renderTarget] = depth;
			index[renderTarget] = alias;
		}

		return index;
	}

	public static bool TryGetAliasParameterEntityFromIndex(
		IReadOnlyDictionary<Node3D, Entity> aliasParameterEntityByRenderTarget,
		Node3D renderTarget,
		Entity modelReferenceEntity,
		out Entity aliasEntity)
	{
		aliasEntity = null;
		if (aliasParameterEntityByRenderTarget == null
			|| renderTarget == null
			|| modelReferenceEntity == null)
		{
			return false;
		}

		if (!aliasParameterEntityByRenderTarget.TryGetValue(renderTarget, out Entity candidate)
			|| candidate is not AliasEntity alias)
		{
			return false;
		}

		aliasEntity = alias;
		return true;
	}

	/// <summary>
	/// Applies the optional <c>material</c> string parameter when the resource has exactly one renderable.

	/// </summary>

	public static void TryApplyMaterialParameterOverride(

		Level level,

		Entity parameterEntity,

		Entity fallbackEntity,

		IList<Tuple<int, int>> renderables)

	{

		if (level?.Materials == null || renderables == null || renderables.Count != 1)

			return;



		if (!TryResolveMaterialWriteIndex(level, parameterEntity, fallbackEntity, out int materialWriteIndex))

			return;



		renderables[0] = new Tuple<int, int>(renderables[0].Item1, materialWriteIndex);

	}



	public static bool TryResolveMaterialWriteIndex(

		Level level,

		Entity parameterEntity,

		Entity fallbackEntity,

		out int materialWriteIndex)

	{

		materialWriteIndex = -1;

		if (level?.Materials == null)

			return false;



		string materialName = TryGetStringParameter(parameterEntity, fallbackEntity, MaterialParameterName);

		if (string.IsNullOrWhiteSpace(materialName))

			return false;



		Materials.Material material = ModelReferenceMaterialMapping.FindMaterialByName(level.Materials, materialName);

		if (material == null)

			return false;



		materialWriteIndex = level.Materials.GetWriteIndex(material);

		return materialWriteIndex >= 0;

	}



	public static bool TryGetEnvironmentColourScalars(

		Entity parameterEntity,

		Entity fallbackEntity,

		Materials.Material material,

		out EnvironmentColourScalars scalars)

	{

		scalars = default;

		if (material?.Shader == null)

			return false;



		if (material.Shader.Ubershader != SHADER_LIST.CA_ENVIRONMENT)

			return false;



		Vector3 vertexColourScale = TryGetVector3Parameter(

			parameterEntity,

			fallbackEntity,

			VertexColourScaleParameterName,

			Vector3.One);

		float vertexOpacityScale = TryGetFloatParameter(

			parameterEntity,

			fallbackEntity,

			VertexOpacityScaleParameterName,

			1f);

		Vector3 diffuseColourScale = TryGetVector3Parameter(

			parameterEntity,

			fallbackEntity,

			DiffuseColourScaleParameterName,

			Vector3.One * 255f);

		float diffuseOpacityScale = TryGetFloatParameter(

			parameterEntity,

			fallbackEntity,

			DiffuseOpacityScaleParameterName,

			1f);



		Vector4 vertex = new Vector4(vertexColourScale.X, vertexColourScale.Y, vertexColourScale.Z, vertexOpacityScale);

		Vector4 diffuse = new Vector4(

			diffuseColourScale.X / 255f,

			diffuseColourScale.Y / 255f,

			diffuseColourScale.Z / 255f,

			diffuseOpacityScale);

		scalars = new EnvironmentColourScalars(vertex, diffuse);

		return true;

	}



	private static int GetSceneNodeDepth(Node node)

	{

		int depth = 0;

		Node current = node;

		while (current != null)

		{

			depth++;

			current = current.GetParent();

		}



		return depth;

	}



	private static string TryGetStringParameter(Entity primary, Entity fallback, string name)

	{

		string value = TryGetStringParameter(primary, name);

		if (!string.IsNullOrWhiteSpace(value))

			return value;



		if (fallback != null && !ReferenceEquals(primary, fallback))

			return TryGetStringParameter(fallback, name);



		return null;

	}



	private static string TryGetStringParameter(Entity entity, string name)

	{

		Parameter parameter = entity?.GetParameter(name);

		if (parameter?.content is not cString value || string.IsNullOrEmpty(value.value))

			return null;



		return value.value;

	}



	private static float TryGetFloatParameter(Entity primary, Entity fallback, string name, float defaultValue)

	{

		if (TryGetFloatParameter(primary, name, out float value))

			return value;



		if (fallback != null

			&& !ReferenceEquals(primary, fallback)

			&& TryGetFloatParameter(fallback, name, out value))

		{

			return value;

		}



		return defaultValue;

	}



	private static bool TryGetFloatParameter(Entity entity, string name, out float value)

	{

		value = 0f;

		if (entity?.GetParameter(name)?.content is not cFloat floatValue)

			return false;



		value = floatValue.value;

		return true;

	}



	private static Vector3 TryGetVector3Parameter(Entity primary, Entity fallback, string name, Vector3 defaultValue)

	{

		if (TryGetVector3Parameter(primary, name, out Vector3 value))

			return value;



		if (fallback != null

			&& !ReferenceEquals(primary, fallback)

			&& TryGetVector3Parameter(fallback, name, out value))

		{

			return value;

		}



		return defaultValue;

	}



	private static bool TryGetVector3Parameter(Entity entity, string name, out Vector3 value)

	{

		value = Vector3.Zero;

		if (entity?.GetParameter(name)?.content is not cVector3 vectorValue)

			return false;



		value = vectorValue.value;

		return true;

	}

}


