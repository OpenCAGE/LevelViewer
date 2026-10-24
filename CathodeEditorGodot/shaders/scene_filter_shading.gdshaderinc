// Shading for the flat-colour scene geometry filters (occlusion volumes, collision hulls).
//
// Occlusion geometry is culling data, so a lot of it carries no normals at all, and a hull sliced out
// of Havok has none either. The face normal is taken from the screen-space derivatives of the view
// position instead, which works for any mesh and gives crisp per-facet shading - exactly what makes a
// hull's shape readable. Without it every one of these renders as a single flat silhouette.

#include "res://shaders/model_reference_shading.gdshaderinc"

// Ambient floor, so faces angled away from the light stay legible rather than going black.
const float SCENE_FILTER_AMBIENT = 0.35;

vec3 scene_filter_face_normal(vec3 view_position) {
	vec3 normal = normalize(cross(dFdx(view_position), dFdy(view_position)));

	// Point it back at the viewer: which way the cross product falls depends on winding, and a
	// consistently-facing normal keeps neighbouring facets from flipping between light and dark.
	if (normal.z < 0.0) {
		normal = -normal;
	}

	return normal;
}

vec3 scene_filter_shade(vec3 colour, vec3 view_position) {
	vec3 normal = scene_filter_face_normal(view_position);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	float shade = SCENE_FILTER_AMBIENT + (1.0 - SCENE_FILTER_AMBIENT) * ndl;
	return model_reference_cap_peak(colour * shade);
}
