shader_type spatial;
render_mode wireframe, unshaded, cull_disabled, depth_draw_opaque, shadows_disabled, fog_disabled;

// Draws the selected entity as its own edges instead of a solid surface. Applied as a material
// OVERRIDE (not an overlay) so the geometry it replaces stops filling in - a wireframe drawn over
// the top of the shaded mesh would just be a mesh with lines on it.
uniform vec4 wireframe_color : source_color = vec4(0.35, 1.0, 0.45, 1.0);

void fragment() {
	ALBEDO = wireframe_color.rgb;
}
