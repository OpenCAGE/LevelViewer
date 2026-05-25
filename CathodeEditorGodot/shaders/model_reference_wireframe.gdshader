shader_type spatial;
render_mode wireframe, unshaded, cull_back, depth_draw_opaque;

uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_diffuse_map = false;
uniform vec4 diffuse_tint : source_color = vec4(1.0);
uniform vec2 diffuse_uv_mult = vec2(1.0);

void fragment() {
	vec4 color = diffuse_tint;
	if (use_diffuse_map) {
		color *= texture(diffuse_map, UV * diffuse_uv_mult);
	}
	ALBEDO = vec3(1.0) - color.rgb;
}
