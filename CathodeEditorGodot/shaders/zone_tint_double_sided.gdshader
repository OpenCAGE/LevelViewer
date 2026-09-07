// As zone_tint, for geometry whose own material was double sided. Culling those would put holes in the
// zone where the level draws a single-sided sheet from both sides - railings, sheeting, foliage cards.
shader_type spatial;
render_mode cull_disabled, unshaded, specular_disabled;

#include "res://shaders/scene_filter_shading.gdshaderinc"

uniform vec4 zone_colour : source_color = vec4(1.0);

void fragment() {
	ALBEDO = scene_filter_shade(zone_colour.rgb, VERTEX);
}
