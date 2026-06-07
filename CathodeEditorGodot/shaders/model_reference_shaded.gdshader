shader_type spatial;
render_mode cull_back, unshaded, specular_disabled;

#include "res://shaders/model_reference_shading.gdshaderinc"

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
uniform vec4 diffuse_tint : source_color = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

uniform bool emission_enabled = false;
uniform vec3 emission : source_color = vec3(0.0);
uniform float emission_energy = 1.0;

void fragment() {
	vec4 color = diffuse_tint;
	if (use_diffuse_map) {
		color *= texture(diffuse_map, UV * diffuse_uv_mult);
	}

	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	ALBEDO = model_reference_apply_preview_shading(color.rgb, ndl);

	if (emission_enabled) {
		EMISSION = emission * emission_energy;
	}
}
