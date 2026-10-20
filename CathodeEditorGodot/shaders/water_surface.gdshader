// Water as the game draws it (CA_SIMPLEWATER / CA_NONINTERACTIVE_WATER, decoded master simplewater_master_ps.hlsl): a
// SimpleWater sheet (WaterSurface.cs) - the alien nest's floor is one, 140 m across, a finger's depth of black water over
// the resin with the room mirrored in it - or a water material on a mesh of its own (a puddle). Unlit: what is under it
// is fogged through three colour stops by how much deeper the level lies behind it, faded in over its soft edge, and a
// reflection is added on top at a clamped fresnel. The game reflects a planar reflection it renders, so it mirrors what
// is off screen too; here the reflected ray is marched through the depth the opaque level left, so only what is on screen
// is mirrored, and elsewhere a soft blur of where the ray left the screen stands in for it.
//
// Ripples: one normal map read twice, each layer scrolling along u at its own speed and scale, optionally warped by a flow
// map in two crossfaded phases (the gas giant's idiom). Their normal tilts the fresnel, and moves where the reflection is
// read by REFLECTION_PERTURBATION_STRENGTH pixels - which is what breaks the game's mirror up, and, read from the mips the
// moving lookup asks for, softens it where the ripples are small on screen.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

global uniform float viewer_exposure;
// OpenCAGE's Render Water: 0 draws none at all
global uniform float viewer_water;
// The water's mirror (LevelViewerPlanarReflection.cs): its texture, the plane it's of (normal xyz, distance w), and whether
// there is one this frame
global uniform sampler2D viewer_reflection : filter_linear, repeat_disable;
global uniform vec4 viewer_reflection_plane;
global uniform float viewer_reflection_active;
// The level's fog (atmosphere_fog.gdshader; LevelViewerFog.cs sets them)
global uniform float viewer_fog;
global uniform vec4 viewer_fog_setting;
global uniform vec3 viewer_fog_near;
global uniform vec3 viewer_fog_far;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
// DEPTH_FOG_INITIAL / MIDPOINT / END: colour (0-1, squared as the game decodes it) and alpha, at no depth, the midpoint's
// depth and the end's
uniform vec4 fog_initial = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 fog_mid = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 fog_end = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec2 fog_depths = vec2(0.5, 1.0);
// MIN_FRESNEL, MAX_FRESNEL, FRESNEL_POWER
uniform vec3 fresnel = vec3(0.0, 5.0, 0.8);
// REFLECTIVE_MAPPING
uniform bool reflective = true;
// softness_edge: how deep it fades in over where it meets the level (0: at once)
uniform float softness = 0.0;
// A water material on a mesh of its own (a puddle) faces as its mesh does; a SimpleWater sheet faces up its transform
uniform bool use_mesh_normal = false;
// ALPHA_MASKING: the outline (its alpha) on the mesh's uvs - a puddle's mesh is a square
uniform sampler2D alpha_mask : filter_linear_mipmap, repeat_enable;
uniform bool use_alpha_mask = false;

// NORMAL_MAP (two channels, as stored: not a colour, not Godot's normal decode), and SECONDARY_NORMAL_MAPPING's second read
uniform sampler2D normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform bool secondary_normal = false;
// CA_NONINTERACTIVE_WATER's second read is of a map of its own (SECONDARY_NORMAL_MAP)
uniform sampler2D secondary_normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_secondary_map = false;
// SPEED, SCALE, SECONDARY_SPEED, SECONDARY_SCALE
uniform vec4 ripple = vec4(0.01, 1.0, -0.01, 1.0);
// NORMAL_MAP_STRENGTH, SECONDARY_NORMAL_MAP_STRENGTH
uniform vec2 ripple_strength = vec2(1.0, 1.0);
// FLOW_UV_ANIMATION: FLOW_MAP, and CYCLE_TIME, FLOW_SPEED, FLOW_TEX_SCALE, FLOW_WARP_STRENGTH
uniform sampler2D flow_map : filter_linear_mipmap, repeat_enable;
uniform bool use_flow = false;
uniform vec4 flow = vec4(10.0, 1.0, 4.0, 0.5);
// REFLECTION_PERTURBATION_STRENGTH: how many pixels a ripple moves the reflection by (the game's, at 1080 lines)
uniform float perturbation = 1.0;

vec3 view_at(vec2 uv, mat4 inv_projection) {
	float depth = textureLod(depth_texture, uv, 0.0).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	// OpenGL (a machine without Vulkan or D3D12 falls back to it) keeps depth -1..1, the others 0..1
	vec4 v = inv_projection * vec4(vec3(uv, depth) * 2.0 - 1.0, 1.0);
#else
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, depth, 1.0);
#endif
	return v.xyz / v.w;
}

vec2 screen_of(vec3 view, mat4 projection) {
	vec4 c = projection * vec4(view, 1.0);
	return c.w <= 0.0 ? vec2(-1.0) : c.xy / c.w * 0.5 + 0.5;
}

// The level's fog over something of the screen's seen from distance d: the copy of the screen the water reads was taken before
// the fog was laid over it, where the game's water is drawn over the fogged scene (and mirrors it fogged). Only where the fog
// is drawn - a camera that sees layer 1, as the main view's does and a composite preview's doesn't
vec3 fogged(vec3 c, float d, uint camera_layers) {
	if ((camera_layers & 1u) == 0u) {
		return c;
	}
	float x = max(d - viewer_fog_setting.x, 0.0);
	float lin = clamp(x / max(viewer_fog_setting.x, 0.0001), 0.0, 1.0);
	float k = viewer_fog_setting.z * viewer_fog_setting.z;
	float w = clamp(lin * viewer_fog_setting.y + 1.0 - exp2(-k * x * x), 0.0, 1.0) * clamp(viewer_fog, 0.0, 1.0);
	return mix(c, mix(viewer_fog_near, viewer_fog_far, w) / 255.0 * viewer_exposure, w);
}

// The ripples' tilt across the surface (u, v), as the game's master builds it from its two reads of the normal map
vec2 ripple_tilt(vec2 uv) {
	vec2 uv1 = (uv + vec2(TIME * ripple.x, 0.0)) * ripple.y;
	vec2 uv2 = (uv + vec2(TIME * ripple.z, 0.0)) * ripple.w;
	vec2 n1;
	vec2 n2;
	if (use_flow) {
		// Two phases a half cycle apart, each pushing the uvs along the flow, crossfaded so neither's jump shows
		vec2 f1 = (texture(flow_map, (uv + vec2(TIME * ripple.x, 0.0)) * flow.z).xy * 2.0 - 1.0) * flow.w;
		vec2 f2 = (texture(flow_map, (uv + vec2(TIME * ripple.z, 0.0)) * flow.z).xy - 0.5) * flow.w * 2.0;
		float cycle = max(flow.x, 0.0001);
		float phase0 = fract(TIME * flow.y / cycle);
		float phase1 = fract((cycle * 0.5 + TIME * flow.y) / cycle);
		float blend = 2.0 * abs(0.5 - phase0);
		n1 = mix(texture(normal_map, uv1 - f1 * phase0).xy, texture(normal_map, uv1 - f1 * phase1).xy, blend);
		n2 = use_secondary_map
			? mix(texture(secondary_normal_map, uv2 - f2 * phase0).xy, texture(secondary_normal_map, uv2 - f2 * phase1).xy, blend)
			: mix(texture(normal_map, uv2 - f2 * phase0).xy, texture(normal_map, uv2 - f2 * phase1).xy, blend);
	} else {
		n1 = texture(normal_map, uv1).xy;
		n2 = use_secondary_map ? texture(secondary_normal_map, uv2).xy : texture(normal_map, uv2).xy;
	}
	vec2 n = (n1 - 0.5) * ripple_strength.x * 2.0;
	if (secondary_normal) {
		n += (n2 - 0.5) * ripple_strength.y * 2.0;
	}
	return n;
}

void fragment() {
	if (viewer_water < 0.5) {
		discard;
	}
	// The ripples, and how far they move the reflection's read across the screen - with how fast that read moves from pixel
	// to pixel, worked out first, in uniform flow and before anything is discarded (the mip the game's sample takes from its derivatives)
	vec2 tilt = use_normal_map ? ripple_tilt(UV) : vec2(0.0);
	vec2 pert = tilt * perturbation / 1080.0 * vec2(VIEWPORT_SIZE.y / VIEWPORT_SIZE.x, 1.0);
	vec2 pert_px_dx = dFdx(pert) * VIEWPORT_SIZE;
	vec2 pert_px_dy = dFdy(pert) * VIEWPORT_SIZE;
	float footprint = max(length(vec2(1.0, 0.0) + pert_px_dx), length(vec2(0.0, 1.0) + pert_px_dy));
	float ripple_lod = log2(max(footprint, 1.0));

	float mask = 1.0;
	if (use_alpha_mask) {
		mask = texture(alpha_mask, UV).a;
		if (mask < 0.00001) {
			discard;
		}
	}

	// How much deeper the level lies behind this pixel, along the view (the game's linear depths)
	float behind = -view_at(SCREEN_UV, INV_PROJECTION_MATRIX).z;
	float depth = max(behind - VERTEX.z * -1.0, 0.0);
	float t_mid = clamp(depth / max(fog_depths.x, 0.0001), 0.0, 1.0);
	float t_end = clamp((depth - fog_depths.x) / max(fog_depths.y - fog_depths.x, 0.0001), 0.0, 1.0);
	vec4 fog = mix(mix(fog_initial, fog_mid, t_mid), fog_end, t_end);
	float soft = softness > 0.0 ? (cos((1.0 - clamp(depth / softness, 0.0, 1.0)) * PI) + 1.0) * 0.5 : 1.0;
	float alpha = clamp(fog.a, 0.0, 1.0) * soft;
	// premultiplied as the game blends it: the fog's colour squared, by its alpha twice (the mask's once)
	vec3 colour = fogged(textureLod(screen_texture, SCREEN_UV, 0.0).rgb, behind, CAMERA_VISIBLE_LAYERS) * (1.0 - alpha * mask) + fog.rgb * fog.rgb * alpha * alpha * mask * viewer_exposure;

	if (reflective) {
		vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 n = use_mesh_normal ? normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz) : normalize(MODEL_MATRIX[1].xyz);
		// Across it along u and v: the sheet's x and z, or the mesh's tangents (made up, should a mesh have none)
		vec3 t = use_mesh_normal ? (INV_VIEW_MATRIX * vec4(TANGENT, 0.0)).xyz : MODEL_MATRIX[0].xyz;
		t = dot(t, t) > 1e-8 ? normalize(t) : normalize(cross(abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), n));
		vec3 b = use_mesh_normal ? (INV_VIEW_MATRIX * vec4(BINORMAL, 0.0)).xyz : MODEL_MATRIX[2].xyz;
		b = dot(b, b) > 1e-8 ? normalize(b) : normalize(cross(n, t));
		vec3 to_eye = normalize(CAMERA_POSITION_WORLD - world);
		if (dot(n, to_eye) < 0.0) {
			n = -n;
		}
		// The ripples' normal, half applied as the game's is (normalised back towards the flat one)
		float nz = sqrt(max(1.0 - dot(tilt, tilt), 0.0));
		vec3 bumped = normalize(normalize(nz * n + tilt.x * t + tilt.y * b) + n);
		// The game's fresnel: one less the facing, held between MIN and MAX (as HLSL's clamp, MAX winning where they cross),
		// then to its power
		float f = clamp(1.0 - dot(to_eye, bumped), 0.0, 1.0);
		f = min(pow(abs(min(max(f, fresnel.x), fresnel.y)), max(fresnel.z, 0.01)), 1.0);

		/* On the plane the water's mirror is of this frame (LevelViewerPlanarReflection): the level drawn again from a camera
		   mirrored through it, what's marked to be in it alone - what the game reads. Its texture is that mirror turned about
		   its vertical, so it's read across from this pixel (the game's 1 - u), moved by the ripples, and softened over their
		   footprint (five taps: it has no mips). Anywhere else, the screen-space march below */
		bool planar = viewer_reflection_active > 0.5
			&& abs(dot(world, viewer_reflection_plane.xyz) - viewer_reflection_plane.w) < 0.15
			&& dot(n, viewer_reflection_plane.xyz) > 0.98;
		if (planar && f * mask > 0.002) {
			vec2 ruv = clamp(vec2(1.0 - SCREEN_UV.x, SCREEN_UV.y) + vec2(-pert.x, pert.y), vec2(0.0), vec2(1.0));
			vec2 texel = 1.0 / vec2(textureSize(viewer_reflection, 0));
			float radius = 0.75 + max(exp2(ripple_lod) - 1.0, 0.0) * 0.5;
			vec3 mirrored = textureLod(viewer_reflection, ruv, 0.0).rgb * 0.4;
			mirrored += textureLod(viewer_reflection, ruv + vec2(radius, 0.5 * radius) * texel, 0.0).rgb * 0.15;
			mirrored += textureLod(viewer_reflection, ruv + vec2(-0.5 * radius, radius) * texel, 0.0).rgb * 0.15;
			mirrored += textureLod(viewer_reflection, ruv + vec2(-radius, -0.5 * radius) * texel, 0.0).rgb * 0.15;
			mirrored += textureLod(viewer_reflection, ruv + vec2(0.5 * radius, -radius) * texel, 0.0).rgb * 0.15;
			// fogged as far as the eye to the water and back again, near enough
			colour += fogged(mirrored, length(VERTEX) * 2.0, CAMERA_VISIBLE_LAYERS) * f * mask;
		}

		/* The reflected ray off the flat surface (the game's mirror is of the flat plane), marched until it passes behind the
		   level, then narrowed down to where it crossed. A ray that meets the level crosses its surface and ends on it; one
		   that ends well behind it slipped behind something nearer - a crate, a strut - and what it found there is that thing,
		   which drawn would stretch it down the water in streaks. That ray goes on, behind the thing, and takes what it comes
		   out onto beyond it; one that never comes out mirrors what the screen can't show, and adds nothing. Not marched where
		   the reflection would add next to nothing - the march is the cost of the water */
		vec3 r = reflect(normalize(VERTEX), normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz));
		vec3 p = VERTEX;
		float step_length = max(0.04, -VERTEX.z * 0.01);
		vec2 hit = vec2(-1.0);
		vec3 hit_point = VERTEX;
		float occluded = 1.0;
		bool behind_level = false;
		// Where the ray was last on the screen, for what it can't show
		vec2 last_uv = SCREEN_UV;
		vec3 last_point = VERTEX;
		bool march = !planar && f * mask > 0.002;
		for (int i = 0; march && i < 64; i++) {
			vec3 q = p + r * step_length;
			vec2 uv = screen_of(q, PROJECTION_MATRIX);
			if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
				break;
			}
			last_uv = uv;
			last_point = q;
			bool now_behind = view_at(uv, INV_PROJECTION_MATRIX).z - q.z > 0.0;
			if (now_behind && !behind_level) {
				vec3 lo = p;
				vec3 hi = q;
				vec2 h = uv;
				for (int j = 0; j < 5; j++) {
					vec3 mid = (lo + hi) * 0.5;
					vec2 um = screen_of(mid, PROJECTION_MATRIX);
					if (view_at(um, INV_PROJECTION_MATRIX).z - mid.z > 0.0) {
						hi = mid;
						h = um;
					} else {
						lo = mid;
					}
				}
				float gap = view_at(h, INV_PROJECTION_MATRIX).z - hi.z;
				float thickness = max(0.05, -hi.z * 0.015);
				float o = smoothstep(thickness, thickness * 3.0, gap);
				// Kept as the hit if it's the nearest yet to a surface: a last one through a gap stands if none is met
				if (o < occluded) {
					occluded = o;
					hit = h;
					hit_point = hi;
				}
				if (o < 0.5) {
					break;
				}
			}
			behind_level = now_behind;
			p = q;
			step_length *= 1.08;
		}
		/* What the screen can't show - where the ray left it, or got no further than behind something - the game's mirror has
		   the room for: here the blurred colour of where it was last on the screen, at half strength. A soft fill rather than
		   a black hole with stepped edges where the mirrored thing is off the screen. Fogged, as everything here is, by how
		   far it is seen, down to the water and back up */
		vec3 reflection = fogged(textureLod(screen_texture, last_uv, 6.0).rgb, length(VERTEX) + length(last_point - VERTEX), CAMERA_VISIBLE_LAYERS) * 0.5;
		if (hit.x >= 0.0) {
			/* The read moved by the ripples - unless that lands on something nearer than what was hit (the game's mirror has
			   nothing in front of the water in it). Faded into the fill towards the screen's edges, where what it mirrors runs
			   off, and where all it met was behind something. Soft as the moving read's mips have it, a little more the further
			   the ray went, and more again where it only got through a gap */
			float thickness = max(0.05, -hit_point.z * 0.015);
			vec2 read = clamp(hit + pert, vec2(0.0), vec2(1.0));
			if (view_at(read, INV_PROJECTION_MATRIX).z - hit_point.z > thickness) {
				read = hit;
			}
			vec2 edge = abs(read * 2.0 - 1.0);
			float keep = 1.0 - smoothstep(0.8, 1.0, max(edge.x, edge.y));
			float travel = length(hit_point - VERTEX);
			float blur = clamp(max(ripple_lod, 1.5 + travel * 0.12) + occluded * 4.0, 0.0, 7.0);
			keep *= 1.0 - occluded * 0.9;
			vec3 mirrored = fogged(textureLod(screen_texture, read, blur).rgb, length(VERTEX) + travel, CAMERA_VISIBLE_LAYERS);
			reflection = mix(reflection, mirrored, keep);
		}
		// Added on top, as the game's is
		if (march) {
			colour += reflection * f * mask;
		}
	}

	ALBEDO = colour;
	ALPHA = 1.0;
}
