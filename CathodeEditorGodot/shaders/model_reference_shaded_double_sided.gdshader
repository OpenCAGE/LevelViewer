shader_type spatial;
render_mode cull_disabled, unshaded, specular_disabled;

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
	vec3 light_dir = normalize(vec3(0.45, 0.82, 0.35));
	float ndl = max(dot(normal, light_dir), 0.0);
	float shade = 0.48 + 0.52 * ndl;

	ALBEDO = color.rgb * shade;

	if (emission_enabled) {
		EMISSION = emission * emission_energy;
	}
}
