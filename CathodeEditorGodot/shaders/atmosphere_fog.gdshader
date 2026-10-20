// The level's fog as the game's atmosphere filter draws it (filter_Atmosphere, BespokeShaders_DX11 entry 27; LevelViewerFog.cs
// picks the FogSetting and feeds it): a full-screen pass over everything opaque, by each pixel's view depth. Past the start,
// a linear ramp over as far again plus a squared exponential, together at most 1, is how much fog there is; the colour runs
// from near to far by that amount (not by distance), and is laid over the scene by it. The sky - nothing drawn there - is left
// alone, so the stars through a window stay.
//
// The filter's constants are worked out on the CPU from the FogSetting, which isn't decoded: scored against the game
// (HAB_CORPORATEPENT, the alien nest), the exponential density squared and the colours as given (0-255) matched; the density
// as given fogged HAB_CORPORATEPENT a stop too dark, and starting at max_distance left it unfogged.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled, skip_vertex_transform;

global uniform float viewer_exposure;
// The fog's strength, 0-1 (1: as the game; 0 turns it off)
global uniform float viewer_fog;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
// FogSetting: linear_distance, linear_density, exponential_density, max_distance (unused)
uniform vec4 fog_setting = vec4(0.0);
// near_colour, far_colour (0-255)
uniform vec3 fog_near = vec3(0.0);
uniform vec3 fog_far = vec3(0.0);

void vertex() {
	// A 2 x 2 quad over the whole screen, wherever the camera is
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	float depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	// OpenGL (a machine without Vulkan or D3D12 falls back to it) keeps depth -1..1 and clears to the far end
	bool sky = depth >= 1.0;
	vec4 view = INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV, depth) * 2.0 - 1.0, 1.0);
#else
	bool sky = depth <= 0.0;
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
#endif
	if (sky) {
		discard;
	}
	float d = -view.z / view.w;

	float x = max(d - fog_setting.x, 0.0);
	float lin = clamp(x / max(fog_setting.x, 0.0001), 0.0, 1.0);
	float k = fog_setting.z * fog_setting.z;
	float w = clamp(lin * fog_setting.y + 1.0 - exp2(-k * x * x), 0.0, 1.0) * clamp(viewer_fog, 0.0, 1.0);

	ALBEDO = mix(fog_near, fog_far, w) / 255.0 * viewer_exposure;
	ALPHA = w;
}
