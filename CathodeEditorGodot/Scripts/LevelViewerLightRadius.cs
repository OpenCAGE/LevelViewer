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
	// Strip lights extend sideways (perpendicular to the light's forward direction).
	private static readonly Vector3 StripAxis = Vector3.Right;
	private static readonly Color WhiteColor = new Color(1f, 1f, 1f, 1f);
	private static readonly Color GreyColor = new Color(0.5f, 0.5f, 0.5f, 1f);

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

		switch (parameters.Type)
		{
			case LIGHT_TYPE.OMNI:
				BuildOmni(_visualRoot, parameters);
				break;
			case LIGHT_TYPE.SPOT:
				BuildSpot(_visualRoot, parameters);
				break;
			case LIGHT_TYPE.STRIP:
				BuildStrip(_visualRoot, parameters);
				break;
		}
	}

	// OMNI: end_attenuation draws a white wireframe sphere; start_attenuation draws a grey one.
	private static void BuildOmni(Node3D parent, LightVisualParams parameters)
	{
		float endRadius = Mathf.Max(parameters.EndAttenuation, MinDistance);
		BuildWireSphere(parent, Vector3.Zero, endRadius, WhiteColor, "OmniEnd");

		float startRadius = Mathf.Max(parameters.StartAttenuation, 0f);
		if (startRadius > MinDistance)
			BuildWireSphere(parent, Vector3.Zero, startRadius, GreyColor, "OmniStart");
	}

	private static void BuildSpot(Node3D parent, LightVisualParams parameters)
	{
		float endDistance = Mathf.Max(parameters.EndAttenuation, MinDistance);
		float startDistance = Mathf.Clamp(parameters.StartAttenuation, 0f, endDistance);
		float outerHalfAngleRad = Mathf.DegToRad(parameters.OuterConeAngle * 0.5f);
		float innerHalfAngleRad = Mathf.DegToRad(parameters.InnerConeAngle * 0.5f);

		// outer_cone_angle: white frustum from the light origin out to end_attenuation.
		BuildSpotFrustum(
			parent,
			endDistance,
			outerHalfAngleRad,
			WhiteColor,
			"SpotOuter",
			parameters.IsSquareLight,
			parameters.AspectRatio);

		// start_attenuation: grey cross-section at the start distance, sized by the outer cone.
		if (startDistance > MinDistance)
		{
			BuildSpotCrossSection(
				parent,
				startDistance,
				outerHalfAngleRad,
				GreyColor,
				"SpotStart",
				parameters.IsSquareLight,
				parameters.AspectRatio);
		}

		// inner_cone_angle: grey frustum from the light origin out to end_attenuation.
		if (innerHalfAngleRad > 0.001f && innerHalfAngleRad < outerHalfAngleRad - 0.001f)
		{
			BuildSpotFrustum(
				parent,
				endDistance,
				innerHalfAngleRad,
				GreyColor,
				"SpotInner",
				parameters.IsSquareLight,
				parameters.AspectRatio);
		}
	}

	private static void BuildSpotFrustum(
		Node3D parent,
		float endDistance,
		float halfAngleRad,
		Color color,
		string prefix,
		bool isSquare,
		float aspectRatio)
	{
		Vector3 apex = Vector3.Zero;
		Vector3 endCenter = LightForward * endDistance;
		GetSpotCrossSectionExtents(halfAngleRad, endDistance, isSquare, aspectRatio, out float halfHeight, out float halfWidth);

		if (isSquare)
			BuildRectangle(parent, LightForward, halfHeight, halfWidth, color, prefix + "End", endCenter);
		else
			BuildCircle(parent, LightForward, halfHeight, color, prefix + "End", endCenter);

		GetSpotPlaneBasis(LightForward, out Vector3 tangent, out Vector3 bitangent);
		if (isSquare)
		{
			Vector3[] corners =
			{
				endCenter + tangent * halfHeight + bitangent * halfWidth,
				endCenter + tangent * halfHeight - bitangent * halfWidth,
				endCenter - tangent * halfHeight - bitangent * halfWidth,
				endCenter - tangent * halfHeight + bitangent * halfWidth,
			};

			for (int i = 0; i < corners.Length; i++)
			{
				PreviewVisualUtility.CreateLineSegment(
					$"{prefix}Spoke{i}",
					parent,
					apex,
					corners[i],
					LineWidth,
					color);
			}
		}
		else
		{
			const int spokeCount = 8;
			for (int i = 0; i < spokeCount; i++)
			{
				float angle = i / (float)spokeCount * Mathf.Tau;
				Vector3 offset = ConeBasisOffset(LightForward, angle, halfHeight);
				PreviewVisualUtility.CreateLineSegment(
					$"{prefix}Spoke{i}",
					parent,
					apex,
					endCenter + offset,
					LineWidth,
					color);
			}
		}
	}

	private static void BuildSpotCrossSection(
		Node3D parent,
		float distance,
		float halfAngleRad,
		Color color,
		string prefix,
		bool isSquare,
		float aspectRatio)
	{
		GetSpotCrossSectionExtents(halfAngleRad, distance, isSquare, aspectRatio, out float halfHeight, out float halfWidth);
		Vector3 center = LightForward * distance;

		if (isSquare)
			BuildRectangle(parent, LightForward, halfHeight, halfWidth, color, prefix, center);
		else
			BuildCircle(parent, LightForward, halfHeight, color, prefix, center);
	}

	private static void GetSpotCrossSectionExtents(
		float halfAngleRad,
		float distance,
		bool isSquare,
		float aspectRatio,
		out float halfHeight,
		out float halfWidth)
	{
		halfHeight = Mathf.Max(Mathf.Tan(halfAngleRad) * distance, MinDistance);
		if (!isSquare)
		{
			halfWidth = halfHeight;
			return;
		}

		halfWidth = Mathf.Max(halfHeight * Mathf.Max(aspectRatio, 0.001f), MinDistance);
	}

	private static void GetSpotPlaneBasis(Vector3 planeNormal, out Vector3 tangent, out Vector3 bitangent)
	{
		planeNormal = planeNormal.Normalized();
		if (planeNormal.LengthSquared() < 0.0001f)
			planeNormal = Vector3.Up;

		tangent = PreviewVisualUtility.GetSafeLookUpVector(planeNormal);
		bitangent = planeNormal.Cross(tangent).Normalized();
		tangent = bitangent.Cross(planeNormal).Normalized();
	}

	private static void BuildRectangle(
		Node3D parent,
		Vector3 planeNormal,
		float halfHeight,
		float halfWidth,
		Color color,
		string name,
		Vector3 center = default)
	{
		GetSpotPlaneBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);

		Vector3 topLeft = center + tangent * halfHeight + bitangent * halfWidth;
		Vector3 topRight = center + tangent * halfHeight - bitangent * halfWidth;
		Vector3 bottomRight = center - tangent * halfHeight - bitangent * halfWidth;
		Vector3 bottomLeft = center - tangent * halfHeight + bitangent * halfWidth;

		PreviewVisualUtility.CreateLineSegment($"{name}_Top", parent, topLeft, topRight, LineWidth, color);
		PreviewVisualUtility.CreateLineSegment($"{name}_Right", parent, topRight, bottomRight, LineWidth, color);
		PreviewVisualUtility.CreateLineSegment($"{name}_Bottom", parent, bottomRight, bottomLeft, LineWidth, color);
		PreviewVisualUtility.CreateLineSegment($"{name}_Left", parent, bottomLeft, topLeft, LineWidth, color);
	}

	// STRIP: end_attenuation is the capsule diameter; strip_length is the length to the sides
	// (0 = sphere). Drawn as a white wireframe capsule along the strip axis.
	private static void BuildStrip(Node3D parent, LightVisualParams parameters)
	{
		float radius = Mathf.Max(parameters.EndAttenuation * 0.5f, MinDistance);
		float halfLength = Mathf.Max(parameters.StripLength * 0.5f, 0f);

		if (halfLength < MinDistance)
		{
			BuildWireSphere(parent, Vector3.Zero, radius, WhiteColor, "StripSphere");
			return;
		}

		BuildWireCapsule(parent, StripAxis, halfLength, radius, WhiteColor, "Strip");
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

	private static void BuildWireSphere(Node3D parent, Vector3 center, float radius, Color color, string prefix)
	{
		const int longitudeCount = 4;
		for (int i = 0; i < longitudeCount; i++)
		{
			float a = i / (float)longitudeCount * Mathf.Pi;
			Vector3 normal = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
			BuildCircle(parent, normal, radius, color, $"{prefix}_Lon{i}", center);
		}

		const int latitudeCount = 3;
		for (int i = 1; i <= latitudeCount; i++)
		{
			float t = i / (float)(latitudeCount + 1);
			float y = Mathf.Cos(t * Mathf.Pi);
			float r = radius * Mathf.Sin(t * Mathf.Pi);
			if (r < MinDistance)
				continue;
			Vector3 ringCenter = center + Vector3.Up * (radius * y);
			BuildCircle(parent, Vector3.Up, r, color, $"{prefix}_Lat{i}", ringCenter);
		}
	}

	private static void BuildWireCapsule(Node3D parent, Vector3 axis, float halfLength, float radius, Color color, string prefix)
	{
		axis = axis.Normalized();
		if (axis.LengthSquared() < 0.0001f)
			axis = Vector3.Right;

		Vector3 centerA = axis * halfLength;
		Vector3 centerB = -axis * halfLength;

		BuildCircle(parent, axis, radius, color, prefix + "RingA", centerA);
		BuildCircle(parent, axis, radius, color, prefix + "RingB", centerB);

		Vector3 tangent = PreviewVisualUtility.GetSafeLookUpVector(axis);
		Vector3 bitangent = axis.Cross(tangent).Normalized();
		tangent = bitangent.Cross(axis).Normalized();

		Vector3[] dirs = { tangent, -tangent, bitangent, -bitangent };
		for (int i = 0; i < dirs.Length; i++)
		{
			Vector3 dir = dirs[i];
			PreviewVisualUtility.CreateLineSegment(
				$"{prefix}Side{i}",
				parent,
				centerB + dir * radius,
				centerA + dir * radius,
				LineWidth,
				color);
			BuildArcRib(parent, centerA, dir, axis, radius, color, $"{prefix}CapA{i}");
			BuildArcRib(parent, centerB, dir, -axis, radius, color, $"{prefix}CapB{i}");
		}
	}

	// Quarter-circle rib from a ring direction up to the pole direction (both unit and perpendicular).
	private static void BuildArcRib(Node3D parent, Vector3 center, Vector3 fromDir, Vector3 poleDir, float radius, Color color, string name)
	{
		fromDir = fromDir.Normalized();
		poleDir = poleDir.Normalized();

		const int segments = 8;
		Vector3 previous = center + fromDir * radius;
		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * (Mathf.Pi * 0.5f);
			Vector3 point = center + (Mathf.Cos(angle) * fromDir + Mathf.Sin(angle) * poleDir) * radius;
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
		public readonly float InnerConeAngle;
		public readonly float OuterConeAngle;
		public readonly float StripLength;
		public readonly bool IsSquareLight;
		public readonly float AspectRatio;

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
				GetFloat(entity, "inner_cone_angle", 22.5f),
				GetFloat(entity, "outer_cone_angle", 45f),
				GetFloat(entity, "strip_length", 10f),
				GetBool(entity, "is_square_light", false),
				GetFloat(entity, "aspect_ratio", 1f));
		}

		private LightVisualParams(
			LIGHT_TYPE type,
			float startAttenuation,
			float endAttenuation,
			float innerConeAngle,
			float outerConeAngle,
			float stripLength,
			bool isSquareLight,
			float aspectRatio)
		{
			Type = type;
			StartAttenuation = startAttenuation;
			EndAttenuation = endAttenuation;
			InnerConeAngle = innerConeAngle;
			OuterConeAngle = outerConeAngle;
			StripLength = stripLength;
			IsSquareLight = isSquareLight;
			AspectRatio = aspectRatio;
		}
	}

	private static float GetFloat(Entity entity, string name, float fallback)
	{
		Parameter parameter = entity?.GetParameter(name);
		if (parameter?.content is cFloat value)
			return value.value;

		return fallback;
	}

	private static bool GetBool(Entity entity, string name, bool fallback)
	{
		Parameter parameter = entity?.GetParameter(name);
		if (parameter?.content is cBool value)
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
