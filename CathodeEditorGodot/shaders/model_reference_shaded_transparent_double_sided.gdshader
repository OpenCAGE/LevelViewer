shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded, specular_disabled;

#include "res://shaders/model_reference_alpha.gdshaderinc"
#include "res://shaders/model_reference_shading.gdshaderinc"

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
uniform vec4 diffuse_tint : source_color = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

uniform bool emission_enabled = false;
uniform vec3 emission : source_color = vec3(0.0);
uniform float emission_energy = 1.0;

void fragment() {
	vec2 uv = UV * diffuse_uv_mult;
	vec4 tex = vec4(1.0);
	if (use_diffuse_map) {
		tex = texture(diffuse_map, uv);
	}

	vec4 color = use_separate_alpha_map
		? model_reference_apply_diffuse_color(diffuse_tint, tex, use_diffuse_map)
		: diffuse_tint * tex;

	vec4 separate_sample = vec4(1.0);
	if (use_separate_alpha_map) {
		separate_sample = texture(separate_alpha_map, UV * separate_alpha_uv_mult);
	}

	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);

	float alpha = model_reference_blend_alpha(tex, separate_sample, diffuse_tint.a);
	model_reference_finish_alpha(alpha);

	ALBEDO = model_reference_apply_preview_shading(color.rgb, ndl);
	ALPHA = alpha;

	if (emission_enabled) {
		EMISSION = emission * emission_energy;
	}
}
