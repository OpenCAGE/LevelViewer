shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded, specular_disabled;

#include "res://shaders/model_reference_shading.gdshaderinc"

uniform vec4 base_grey : source_color = vec4(0.21, 0.21, 0.22, 1.0);
uniform float opacity : hint_range(0.0, 1.0) = 0.24;

void fragment() {
	model_reference_clip_to_reflection(VERTEX, INV_VIEW_MATRIX, CAMERA_VISIBLE_LAYERS);
	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	ALBEDO = model_reference_dimmed(base_grey.rgb, ndl);
	ALPHA = opacity;
}
