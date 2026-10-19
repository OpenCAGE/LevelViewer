shader_type spatial;
render_mode cull_disabled, unshaded, specular_disabled;

#include "res://shaders/model_reference_alpha.gdshaderinc"
#include "res://shaders/model_reference_shading.gdshaderinc"
#include "res://shaders/model_reference_surface.gdshaderinc"

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
// Not source_color: the game multiplies its DIFFUSE_TINT in as it is, and model_reference_tint says how it's read
uniform vec4 diffuse_tint = vec4(1.0);
uniform vec4 vertex_colour_tint : source_color = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

uniform bool emission_enabled = false;
uniform vec3 emission : source_color = vec3(0.0);
uniform float emission_energy = 1.0;

void fragment() {
	vec4 tint = model_reference_tint(diffuse_tint);
	vec2 uv = UV * diffuse_uv_mult;
	vec4 tex = vec4(1.0);
	if (use_diffuse_map) {
		tex = texture(diffuse_map, uv);
	}

	vec4 color = tint * tex;
	vec4 vertex_tint = model_reference_vertex_tint(vertex_colour_tint, COLOR);
	color = model_reference_apply_vertex_colour_tint(color, vertex_tint);
	color.rgb = model_reference_surface_layers(color.rgb, UV, COLOR, NORMAL, VIEW, mat3(INV_VIEW_MATRIX));

	if (alpha_cutout) {
		vec4 separate_sample = vec4(1.0);
		if (use_separate_alpha_map) {
			separate_sample = texture(separate_alpha_map, UV * separate_alpha_uv_mult);
		}

		float alpha = model_reference_blend_alpha(tex, separate_sample, tint.a);
		alpha *= vertex_tint.a;
		alpha *= model_reference_alpha_fade(UV, COLOR);
		model_reference_finish_alpha(alpha);
	}

	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	ALBEDO = model_reference_apply_preview_shading(color.rgb, ndl);

	if (emission_enabled) {
		EMISSION = emission * emission_energy;
	}
}
