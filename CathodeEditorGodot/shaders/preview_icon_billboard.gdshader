shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded;

uniform vec4 albedo_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;

void vertex() {
	// Camera-facing billboard while preserving node scale (MODEL_MATRIX column lengths).
	vec3 scale = vec3(
		length(MODEL_MATRIX[0].xyz),
		length(MODEL_MATRIX[1].xyz),
		length(MODEL_MATRIX[2].xyz));
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		INV_VIEW_MATRIX[0] * scale.x,
		INV_VIEW_MATRIX[1] * scale.y,
		INV_VIEW_MATRIX[2] * scale.z,
		MODEL_MATRIX[3]);
}

void fragment() {
	vec4 tex = texture(albedo_texture, vec2(UV.x, 1.0 - UV.y));
	ALBEDO = tex.rgb * albedo_color.rgb;
	ALPHA = albedo_color.a * tex.a;
}
