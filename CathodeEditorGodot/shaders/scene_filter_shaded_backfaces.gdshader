// As scene_filter_shaded, but only the back faces are drawn. Occlusion volumes enclose the geometry
// they hide, so culling the near half lets you see into them rather than at a solid blob.
shader_type spatial;
render_mode cull_front, unshaded, specular_disabled;

#include "res://shaders/scene_filter_shading.gdshaderinc"

uniform vec4 filter_colour : source_color = vec4(1.0);

void fragment() {
	ALBEDO = scene_filter_shade(filter_colour.rgb, VERTEX);
}
