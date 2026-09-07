// The level's own geometry recoloured by the zone it belongs to. Same fake directional shading as the
// scene geometry filters, so surfaces still read as surfaces rather than one flat silhouette - but this
// REPLACES the material rather than being laid over it, because it covers a whole level's worth of
// meshes at once and an overlay pass would draw every one of them a second time.
//
// Back-face culled, unlike the filters: they cull_disabled because a Havok hull has no dependable
// winding, and doing the same here would rasterise the entire level twice over.
shader_type spatial;
render_mode unshaded, specular_disabled;

#include "res://shaders/scene_filter_shading.gdshaderinc"

uniform vec4 zone_colour : source_color = vec4(1.0);

void fragment() {
	ALBEDO = scene_filter_shade(zone_colour.rgb, VERTEX);
}
