// As zone_tint, for the see-through volumes the viewport draws trigger boxes and spheres as: drawn the way
// preview_transparent draws them, in the zone's colour, so a volume around a room shows its zone without hiding the room.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, unshaded;

uniform vec4 zone_colour : source_color = vec4(1.0, 1.0, 1.0, 0.24);

void fragment() {
	ALBEDO = zone_colour.rgb;
	ALPHA = zone_colour.a;
}
