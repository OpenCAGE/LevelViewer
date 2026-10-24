// As selection_highlight_overlay, but only the back faces are painted. Applied over geometry that is
// itself drawn back-face-only (occlusion hulls) - without matching the cull mode the highlight fills
// in the near half of the hull that was deliberately left out, hiding everything behind it.
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_opaque, cull_front, shadows_disabled, fog_disabled;

uniform vec4 highlight_color : source_color = vec4(0.35, 1.0, 0.45, 1.0);
uniform float highlight_strength = 0.55;

void fragment() {
	ALBEDO = highlight_color.rgb * highlight_strength;
	ALPHA = 1.0;
}
