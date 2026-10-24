// A SimpleWater's caustics (WaterCaustics.cs): the game's CA_WATER_CAUSTICS_OVERLAY, a deferred pass over the box above or
// below the water that finds the surface behind each pixel, reads the caustics volume texture three ways across it - the
// third coordinate running with time, so the 16 slices play as a loop - and adds where the texture's coverage clears a cut
// the box's falloff and the surface's facing lower. Added on top, unlit, as the game adds it to its lit buffer. Drawn from
// the depth the opaque level left, with the box's back faces so it still covers the screen from inside.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;

// OpenCAGE's Render Water: 0 draws none at all
global uniform float viewer_water;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
// Not source_color: the game squares what it samples
uniform sampler3D caustics_volume : filter_linear, repeat_enable;
// CAUSTIC_TEXTURE_SCALE, CAUSTIC_INTENSITY, CAUSTIC_SURFACE_WRAP, and the speed (the water's SPEED x CAUSTIC_SPEED_SCALAR)
uniform float texture_scale = 1.0;
uniform float intensity = 1.0;
uniform float surface_wrap = 1.0;
uniform float speed = 0.2;
// Back towards the water, in the box's own up: +1 from under it, -1 from over it
uniform float wrap_direction = 1.0;

void fragment() {
	if (viewer_water < 0.5) {
		discard;
	}
	// The surface behind this pixel, back in the world (derivatives before anything is discarded)
	float depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	vec4 view = INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV, depth) * 2.0 - 1.0, 1.0);
#else
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
#endif
	view.xyz /= view.w;
	vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
	vec3 n = normalize(cross(dFdx(world), dFdy(world)));
	if (dot(n, CAMERA_POSITION_WORLD - world) < 0.0) {
		n = -n;
	}

	// Where in the box it is: -1..1 along each of its axes, out to its faces
	vec3 rel = world - MODEL_MATRIX[3].xyz;
	vec3 ax = MODEL_MATRIX[0].xyz;
	vec3 ay = MODEL_MATRIX[1].xyz;
	vec3 az = MODEL_MATRIX[2].xyz;
	vec3 l = vec3(dot(rel, ax) / dot(ax, ax), dot(rel, ay) / dot(ay, ay), dot(rel, az) / dot(az, az)) * 2.0;
	if (max(max(abs(l.x), abs(l.y)), abs(l.z)) > 1.0) {
		discard;
	}
	vec3 fall = vec3(1.0) - min(abs(l), vec3(1.0));
	float fade = fall.x * fall.y * fall.z * intensity;
	// How much it faces back towards the water, round to SURFACE_WRAP
	float facing = clamp((dot(n, normalize(ay) * wrap_direction) + surface_wrap) / (surface_wrap + 1.0), 0.0, 1.0);

	// The volume three ways, weighted by the cubed facing past a dead zone (as the surface effects' overlay), squared
	vec3 tw = max((abs(n) - 0.2) * 7.0, vec3(0.0));
	tw = tw * tw * tw;
	tw /= max(tw.x + tw.y + tw.z, 0.0001);
	float t = TIME * speed;
	vec3 p = world * texture_scale;
	vec4 c_x = texture(caustics_volume, vec3(p.z, p.y, t));
	vec4 c_y = texture(caustics_volume, vec3(p.z, p.x, t));
	vec4 c_z = texture(caustics_volume, vec3(p.x, p.y, t));
	c_x.rgb *= c_x.rgb;
	c_y.rgb *= c_y.rgb;
	c_z.rgb *= c_z.rgb;
	vec4 c = c_x * tw.x + c_y * tw.y + c_z * tw.z;

	float x = clamp(c.a - (1.0 - fade * facing), 0.0, 1.0);
	ALBEDO = min(x * x * (3.0 - 2.0 * x), 1.0) * c.rgb;
	ALPHA = 1.0;
}
