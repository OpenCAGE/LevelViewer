shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded, specular_disabled;

#include "res://shaders/model_reference_alpha.gdshaderinc"
#include "res://shaders/model_reference_shading.gdshaderinc"
#include "res://shaders/model_reference_surface.gdshaderinc"

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
// Not source_color: the game multiplies its DIFFUSE_TINT in as it is, and model_reference_tint says how it's read
uniform vec4 diffuse_tint = vec4(1.0);
// The instance's vertex colour scale, a multiplier the game uses as it is (not a colour to decode)
uniform vec4 vertex_colour_tint = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

uniform bool emission_enabled = false;
uniform vec3 emission : source_color = vec3(0.0);
uniform float emission_energy = 1.0;

void fragment() {
	model_reference_clip_to_reflection(VERTEX, INV_VIEW_MATRIX, CAMERA_VISIBLE_LAYERS);
	vec4 tint = model_reference_tint(diffuse_tint);
	vec2 uv = UV * diffuse_uv_mult;
	vec4 tex = vec4(1.0);
	if (use_diffuse_map) {
		tex = texture(diffuse_map, uv);
	}

	vec4 color = use_separate_alpha_map
		? model_reference_apply_diffuse_color(tint, tex, use_diffuse_map)
		: tint * tex;
	vec4 vertex_tint = model_reference_vertex_tint(vertex_colour_tint, COLOR);
	color.rgb = model_reference_emissive_base(color.rgb, tex.rgb);
	color = model_reference_apply_vertex_colour_tint(color, vertex_tint);
	color.rgb = model_reference_surface_layers(color.rgb, UV, COLOR, NORMAL, VIEW, mat3(INV_VIEW_MATRIX));

	vec4 separate_sample = vec4(1.0);
	if (use_separate_alpha_map) {
		separate_sample = texture(separate_alpha_map, UV * separate_alpha_uv_mult);
	}

	vec3 normal = normalize(NORMAL);
	float ndl = max(dot(normal, MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);

	vec2 separate_uv = UV * separate_alpha_uv_mult;
	float alpha = model_reference_blend_alpha_uv(tex, separate_sample, tint.a, abs(dFdx(separate_uv.x)) + abs(dFdy(separate_uv.y)));
	alpha *= vertex_tint.a;
	alpha *= model_reference_alpha_fade(UV, COLOR);
	model_reference_finish_alpha(alpha);

	ALBEDO = model_reference_apply_preview_shading(color.rgb, ndl) + model_reference_emission(color.rgb);
	ALPHA = alpha;

	if (emission_enabled) {
		EMISSION = emission * emission_energy;
	}
}
