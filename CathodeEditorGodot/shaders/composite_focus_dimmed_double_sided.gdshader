shader_type spatial;
render_mode cull_disabled, unshaded, specular_disabled;

#include "res://shaders/model_reference_shading.gdshaderinc"

uniform vec4 base_grey : source_color = vec4(0.09, 0.09, 0.10, 1.0);

void fragment() {
	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	ALBEDO = model_reference_apply_preview_shading(base_grey.rgb, ndl);
}
