shader_type spatial;
render_mode blend_add, unshaded, depth_draw_opaque, cull_disabled, shadows_disabled, fog_disabled;

uniform vec4 highlight_color : source_color = vec4(0.35, 1.0, 0.45, 1.0);
uniform float highlight_strength = 0.55;

void vertex() {
	// Match preview_icon_billboard.gdshader so the overlay faces the camera.
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
	ALBEDO = highlight_color.rgb * highlight_strength;
	ALPHA = 1.0;
}
