shader_type spatial;
render_mode wireframe, blend_mix, depth_draw_opaque, unshaded, cull_disabled;

#include "res://shaders/model_reference_alpha.gdshaderinc"
#include "res://shaders/model_reference_shading.gdshaderinc"

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
uniform vec4 diffuse_tint : source_color = vec4(1.0);
uniform vec4 vertex_colour_tint : source_color = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

void fragment() {
	vec2 uv = UV * diffuse_uv_mult;
	vec4 tex = vec4(1.0);
	if (use_diffuse_map) {
		tex = texture(diffuse_map, uv);
	}

	vec4 color = use_separate_alpha_map
		? model_reference_apply_diffuse_color(diffuse_tint, tex, use_diffuse_map)
		: diffuse_tint * tex;
	color = model_reference_apply_vertex_colour_tint(color, vertex_colour_tint);

	vec4 separate_sample = vec4(1.0);
	if (use_separate_alpha_map) {
		separate_sample = texture(separate_alpha_map, UV * separate_alpha_uv_mult);
	}

	float alpha = model_reference_blend_alpha(tex, separate_sample, diffuse_tint.a);
	alpha *= vertex_colour_tint.a;
	model_reference_finish_alpha(alpha);

	ALBEDO = vec3(1.0) - color.rgb;
	ALPHA = alpha;
}
