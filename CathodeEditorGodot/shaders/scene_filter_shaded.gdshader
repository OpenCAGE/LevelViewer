// Flat-coloured geometry the engine never draws (collision hulls), shaded with the same fake
// directional cue as the model preview so surfaces read as surfaces instead of one solid silhouette.
shader_type spatial;
render_mode cull_disabled, unshaded, specular_disabled;

#include "res://shaders/scene_filter_shading.gdshaderinc"

uniform vec4 filter_colour : source_color = vec4(1.0);

void fragment() {
	ALBEDO = scene_filter_shade(filter_colour.rgb, VERTEX);
}
