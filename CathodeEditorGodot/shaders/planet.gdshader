// A planet as the game draws it (CA_PLANET, decoded from its retail permutations; PlanetLook.cs feeds each instance): the
// model is a small sphere a few metres from a SpaceVista composite's Parallax_Position, scaled about its own centre by the
// Planet entity's parallax_scale, then pushed five million times further from that anchor and put on the far plane - so it
// is seen as the anchor sees it, the size of a gas giant, wherever the camera is, and only where nothing else was drawn.
// The colour is its atmosphere map times its detail map (to the fourth power, as the game decodes them), lit from the sun
// with a wrapped, penumbra-shaped falloff, plus a lit atmosphere rim boosted towards the horizon and an unlit one, its edge
// fading out by the alpha; premultiplied, as the game blends it. The atmosphere scrolls and flows as the game's does.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_back, shadows_disabled, fog_disabled, skip_vertex_transform;

global uniform float viewer_exposure;

// Sampled raw, as the game does before it takes them to the fourth power (as colour, the gas giant came out a stop dark)
uniform sampler2D atmosphere_map : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail_map : filter_linear_mipmap, repeat_enable;
uniform sampler2D flow_map : filter_linear_mipmap, repeat_enable;
uniform bool use_detail_map = false;
uniform bool use_flow = false;
uniform float detail_tex_scalar = 1.0;
// SCROLL_SPEED and DETAIL_SCROLL_SPEED (the vertex stage's), CYCLE_TIME, FLOW_SPEED, FLOW_TEX_SCALE, FLOW_WARP_STRENGTH
uniform vec2 scroll_speed = vec2(0.0);
uniform vec4 flow = vec4(1.0, 0.0, 1.0, 0.0);
// The material's constants
uniform float rim_transparency = 1.0;
uniform float overbright = 1.0;
uniform float rim_fresnel = 1.0;
uniform vec3 rim_colour = vec3(1.0);
uniform float rim_brightness = 0.0;
uniform float horizon_booster = 0.0;
uniform vec3 unlit_rim_colour = vec3(0.0);
uniform float unlit_rim_brightness = 0.0;
uniform float penumbra_falloff_power = 1.0;
uniform float light_wrap_angle = 0.0;

// The Planet entity's, per instance
instance uniform float parallax_scale = 1.0;
// The Parallax_Position it is seen from and the sun, from its centre
instance uniform vec3 anchor_offset = vec3(-10.0, 0.0, 0.0);
instance uniform vec3 sun_offset = vec3(0.0, 1000.0, 0.0);
instance uniform float instance_overbright = 1.0;
instance uniform float light_wrap_angle_scalar = 1.0;
instance uniform float penumbra_falloff_scalar = 1.0;
instance uniform float edge_transparency = 1.0;
instance uniform float edge_falloff_power = 1.0;
instance uniform float detail_uv_scale = 1.0;
// flow_cycle_time, flow_speed, flow_tex_scale, flow_warp_strength; atmosphere_scroll_speed, atmosphere_detail_scroll_speed
instance uniform vec4 instance_flow = vec4(1.0, 1.0, 1.0, 1.0);
instance uniform vec2 instance_scroll = vec2(0.0);
// 0 where the level starts it hidden (its mover not visible: a sun the script swaps in later)
instance uniform float planet_shown = 1.0;
// OpenCAGE's Render Planets: 0 draws none at all
global uniform float viewer_planets;

varying vec3 world_normal;
varying vec3 to_eye;
varying vec3 to_sun;

void vertex() {
	vec3 centre = MODEL_MATRIX[3].xyz;
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world = (world - centre) * parallax_scale + centre;
	vec3 dir = world - (centre + anchor_offset);
	world_normal = normalize(mat3(MODEL_MATRIX) * NORMAL);
	to_eye = -normalize(dir);
	to_sun = normalize(sun_offset);

	vec4 clip = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(dir, 0.0));
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	POSITION = vec4(clip.xy, clip.w, clip.w);
#else
	POSITION = vec4(clip.xy, 0.0, clip.w);
#endif
	// Hidden: every vertex behind the camera, so nothing is drawn
	if (planet_shown < 0.5 || viewer_planets < 0.5) {
		POSITION = vec4(0.0, 0.0, 0.0, -1.0);
	}
}

void fragment() {
	vec3 n = normalize(world_normal);
	vec3 v = normalize(to_eye);
	vec3 l = normalize(to_sun);

	// Scrolling round the planet (the pairing of speeds is the game's), then two flow-warped samples of each map half a
	// cycle apart, crossfaded so neither phase's reset shows
	vec2 atmosphere_uv = UV + vec2(TIME * scroll_speed.x * instance_scroll.y, 0.0);
	vec2 detail_base = UV + vec2(TIME * scroll_speed.y * instance_scroll.x, 0.0);
	vec2 detail_uv = detail_base * detail_tex_scalar * detail_uv_scale;
	vec4 c;
	if (use_flow) {
		float cycle = max(instance_flow.x * flow.x, 0.0001);
		float t = TIME * instance_flow.y * flow.y;
		float phase0 = fract(t / cycle);
		float phase1 = fract((t + cycle * 0.5) / cycle);
		float blend = 2.0 * abs(0.5 - phase0);
		float tex_scale = instance_flow.z * flow.z;
		float warp = instance_flow.w * flow.w;
		vec2 flow_a = (texture(flow_map, atmosphere_uv * tex_scale).zw * 2.0 - 1.0) * warp;
		vec2 flow_b = (texture(flow_map, detail_base * tex_scale).xy * 2.0 - 1.0) * warp;
		c = mix(texture(atmosphere_map, atmosphere_uv - flow_a * phase0), texture(atmosphere_map, atmosphere_uv - flow_a * phase1), blend);
		if (use_detail_map) {
			c *= mix(texture(detail_map, detail_uv - flow_b * phase0), texture(detail_map, detail_uv - flow_b * phase1), blend);
		}
	} else {
		c = texture(atmosphere_map, atmosphere_uv);
		if (use_detail_map) {
			c *= texture(detail_map, detail_uv);
		}
	}
	vec3 col = c.rgb * c.rgb;
	col *= col;

	// Lit side: the facing, wrapped past the terminator, shaped by the penumbra power
	float wrap = -sin(light_wrap_angle * light_wrap_angle_scalar);
	float ndl = min(max(dot(n, l), wrap), 1.0);
	float lit = clamp((ndl - wrap) / max(1.0 - wrap, 0.0001), 0.0, 1.0);
	lit = pow(lit, max(penumbra_falloff_scalar * penumbra_falloff_power, 0.0001));

	float ndv = max(abs(dot(v, n)), 0.075);
	float rim = min(ndv * pow(1.0 - min(ndv, 1.0), rim_fresnel), 1.0);
	float edge = c.a * pow(ndv * abs(edge_falloff_power), abs(edge_falloff_power));

	vec3 lit_rim = rim_colour * rim_brightness * min(lit * rim, 1.0);
	lit_rim += lit_rim * clamp(dot(v, -l), 0.0, 1.0) * horizon_booster;
	vec3 rgb = col * lit + lit_rim + unlit_rim_colour * unlit_rim_brightness * rim;

	float alpha = clamp(2.0 * edge * edge * edge_transparency * rim_transparency, 0.0, 1.0);
	ALBEDO = rgb * alpha * instance_overbright * overbright * viewer_exposure;
	ALPHA = alpha;
}
