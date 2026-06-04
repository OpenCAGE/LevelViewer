shader_type spatial;
render_mode cull_back, unshaded;

// Do not read or write the ALPHA built-in: in Godot 4.x that always routes the
// material through the transparent render pipeline, even at ALPHA = 1.0.

uniform vec4 albedo_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);

void fragment() {
	ALBEDO = albedo_color.rgb;
}
