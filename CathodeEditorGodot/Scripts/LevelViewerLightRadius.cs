using CATHODE;
using CATHODE.Enums;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using OpenCAGE;
using System.Collections.Generic;

/// <summary>
/// Selected-only LightReference range overlay (omni sphere, spot cone, strip length).
/// Shown only when the LightReference render filter is enabled.
/// </summary>
public static class LevelViewerLightRadius
{
	private const float MinDistance = 0.05f;
	private const float LineWidth = 0.01f;
	private static readonly Vector3 LightForward = Vector3.Back;

	private static Node3D _visualRoot;
	private static Node3D _attachNode;

	public static bool ShouldShow(FunctionEntity entity, uint ownerCompositeId)
	{
		if (entity == null || !entity.function.IsFunctionType)
			return false;

		if (entity.function.AsFunctionType != FunctionType.LightReference)
			return false;

		return PreviewVisualUtility.IsPreviewVisible(entity, ownerCompositeId);
	}

	public static void Apply(Node3D entityNode, FunctionEntity entity, uint ownerCompositeId)
	{
		if (!ShouldShow(entity, ownerCompositeId) || entityNode == null || !GodotObject.IsInstanceValid(entityNode))
		{
			Clear();
			return;
		}

		if (_attachNode != entityNode)
		{
			Clear();
			_attachNode = entityNode;
			_visualRoot = new Node3D { Name = "LightRadiusVisual" };
			entityNode.AddChild(_visualRoot);
		}

		RebuildVisual(entity);
	}

	public static void RefreshIfAttached(FunctionEntity entity, uint ownerCompositeId)
	{
		if (_visualRoot == null || _attachNode == null)
			return;

		if (!ShouldShow(entity, ownerCompositeId))
		{
			Clear();
			return;
		}

		RebuildVisual(entity);
	}

	public static void Clear()
	{
		if (_visualRoot != null && GodotObject.IsInstanceValid(_visualRoot))
			_visualRoot.QueueFree();

		_visualRoot = null;
		_attachNode = null;
	}

	private static void RebuildVisual(FunctionEntity entity)
	{
		if (_visualRoot == null)
			return;

		ClearVisualChildren(_visualRoot);

		LightVisualParams parameters = LightVisualParams.Read(entity);
		Color color = PreviewVisualUtility.GetOpaquePreviewColor(entity);
		color.A = 0.9f;

		switch (parameters.Type)
		{
			case LIGHT_TYPE.OMNI:
				BuildOmni(_visualRoot, parameters, color);
				break;
			case LIGHT_TYPE.SPOT:
				BuildSpot(_visualRoot, parameters, color);
				break;
			case LIGHT_TYPE.STRIP:
				BuildStrip(_visualRoot, parameters, color);
				break;
		}
	}

	private static void BuildOmni(Node3D parent, LightVisualParams parameters, Color color)
	{
		float endRadius = Mathf.Max(parameters.EndAttenuation, MinDistance);
		BuildCircle(parent, Vector3.Up, endRadius, color, "OmniXY");
		BuildCircle(parent, Vector3.Right, endRadius, color, "OmniXZ");
		BuildCircle(parent, Vector3.Forward, endRadius, color, "OmniYZ");

		float startRadius = Mathf.Clamp(parameters.StartAttenuation, MinDistance, endRadius - 0.01f);
		if (startRadius > MinDistance)
		{
			Color inner = color;
			inner.A *= 0.55f;
			BuildCircle(parent, Vector3.Up, startRadius, inner, "OmniStartXY");
			BuildCircle(parent, Vector3.Right, startRadius, inner, "OmniStartXZ");
			BuildCircle(parent, Vector3.Forward, startRadius, inner, "OmniStartYZ");
		}
	}

	private static void BuildSpot(Node3D parent, LightVisualParams parameters, Color color)
	{
		float endDistance = Mathf.Max(parameters.EndAttenuation, MinDistance);
		float nearDistance = Mathf.Clamp(parameters.NearDist, 0f, endDistance - 0.01f);
		float outerHalfAngleRad = Mathf.DegToRad(parameters.OuterConeAngle * 0.5f);
		float innerHalfAngleRad = Mathf.DegToRad(parameters.InnerConeAngle * 0.5f);

		BuildCone(parent, nearDistance, endDistance, outerHalfAngleRad, color, "SpotOuter");
		if (innerHalfAngleRad > 0.001f && innerHalfAngleRad < outerHalfAngleRad - 0.001f)
		{
			Color inner = color;
			inner.A *= 0.55f;
			BuildCone(parent, nearDistance, endDistance, innerHalfAngleRad, inner, "SpotInner");
		}
	}

	private static void BuildStrip(Node3D parent, LightVisualParams parameters, Color color)
	{
		float length = Mathf.Max(parameters.StripLength, MinDistance);
		Vector3 start = Vector3.Zero;
		Vector3 end = LightForward * length;
		PreviewVisualUtility.CreateLineSegment("StripAxis", parent, start, end, LineWidth * 1.35f, color);

		float cross = Mathf.Clamp(parameters.EndAttenuation * 0.15f, LineWidth * 2f, 0.75f);
		BuildStripEndCap(parent, start, cross, color, "StripStartCap");
		BuildStripEndCap(parent, end, cross, color, "StripEndCap");
	}

	private static void BuildStripEndCap(Node3D parent, Vector3 center, float size, Color color, string prefix)
	{
		Vector3 right = Vector3.Right * size;
		Vector3 up = Vector3.Up * size;
		PreviewVisualUtility.CreateLineSegment(prefix + "A", parent, center - right, center + right, LineWidth, color);
		PreviewVisualUtility.CreateLineSegment(prefix + "B", parent, center - up, center + up, LineWidth, color);
	}

	private static void BuildCone(
		Node3D parent,
		float nearDistance,
		float endDistance,
		float halfAngleRad,
		Color color,
		string prefix)
	{
		Vector3 apex = LightForward * nearDistance;
		Vector3 center = LightForward * endDistance;
		float endRadius = Mathf.Tan(halfAngleRad) * (endDistance - nearDistance);
		if (endRadius < MinDistance)
			endRadius = MinDistance;

		BuildCircle(parent, LightForward, endRadius, color, prefix + "End", center);

		const int spokeCount = 8;
		for (int i = 0; i < spokeCount; i++)
		{
			float angle = i / (float)spokeCount * Mathf.Tau;
			Vector3 offset = ConeBasisOffset(LightForward, angle, endRadius);
			PreviewVisualUtility.CreateLineSegment(
				$"{prefix}Spoke{i}",
				parent,
				apex,
				center + offset,
				LineWidth,
				color);
		}
	}

	private static void BuildCircle(
		Node3D parent,
		Vector3 planeNormal,
		float radius,
		Color color,
		string name,
		Vector3 center = default)
	{
		planeNormal = planeNormal.Normalized();
		if (planeNormal.LengthSquared() < 0.0001f)
			planeNormal = Vector3.Up;

		Vector3 tangent = PreviewVisualUtility.GetSafeLookUpVector(planeNormal);
		Vector3 bitangent = planeNormal.Cross(tangent).Normalized();
		tangent = bitangent.Cross(planeNormal).Normalized();

		const int segments = 48;
		Vector3 previous = center + tangent * radius;
		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * Mathf.Tau;
			Vector3 point = center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius;
			PreviewVisualUtility.CreateLineSegment($"{name}_{i}", parent, previous, point, LineWidth, color);
			previous = point;
		}
	}

	private static Vector3 ConeBasisOffset(Vector3 axis, float angle, float radius)
	{
		axis = axis.Normalized();
		Vector3 tangent = PreviewVisualUtility.GetSafeLookUpVector(axis);
		Vector3 bitangent = axis.Cross(tangent).Normalized();
		tangent = bitangent.Cross(axis).Normalized();
		return (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius;
	}

	private static void ClearVisualChildren(Node3D root)
	{
		foreach (Node child in root.GetChildren().ToArray())
		{
			root.RemoveChild(child);
			child.QueueFree();
		}
	}

	private readonly struct LightVisualParams
	{
		public readonly LIGHT_TYPE Type;
		public readonly float StartAttenuation;
		public readonly float EndAttenuation;
		public readonly float NearDist;
		public readonly float InnerConeAngle;
		public readonly float OuterConeAngle;
		public readonly float StripLength;

		public static LightVisualParams Read(FunctionEntity entity)
		{
			LIGHT_TYPE type = (LIGHT_TYPE)GetEnumIndex(entity, "type", (int)LIGHT_TYPE.OMNI);
			if (type == LIGHT_TYPE.UNKNOWN_LIGHT_TYPE)
				type = LIGHT_TYPE.OMNI;

			float endAttenuation = GetFloat(entity, "end_attenuation", 2f);
			float startAttenuation = GetFloat(entity, "start_attenuation", 0.1f);
			if (startAttenuation > endAttenuation - 0.05f)
				startAttenuation = Mathf.Max(0f, endAttenuation - 0.05f);

			return new LightVisualParams(
				type,
				startAttenuation,
				endAttenuation,
				GetFloat(entity, "near_dist", 0.1f),
				GetFloat(entity, "inner_cone_angle", 22.5f),
				GetFloat(entity, "outer_cone_angle", 45f),
				GetFloat(entity, "strip_length", 10f));
		}

		private LightVisualParams(
			LIGHT_TYPE type,
			float startAttenuation,
			float endAttenuation,
			float nearDist,
			float innerConeAngle,
			float outerConeAngle,
			float stripLength)
		{
			Type = type;
			StartAttenuation = startAttenuation;
			EndAttenuation = endAttenuation;
			NearDist = nearDist;
			InnerConeAngle = innerConeAngle;
			OuterConeAngle = outerConeAngle;
			StripLength = stripLength;
		}
	}

	private static float GetFloat(Entity entity, string name, float fallback)
	{
		Parameter parameter = entity?.GetParameter(name);
		if (parameter?.content is cFloat value)
			return value.value;

		return fallback;
	}

	private static int GetEnumIndex(Entity entity, string name, int fallback)
	{
		Parameter parameter = entity?.GetParameter(name);
		if (parameter?.content is cEnum value)
			return value.enumIndex;

		return fallback;
	}
}
