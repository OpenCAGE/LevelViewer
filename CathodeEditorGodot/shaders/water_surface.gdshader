// Water as the game draws it (CA_SIMPLEWATER / CA_NONINTERACTIVE_WATER, decoded master simplewater_master_ps.hlsl): a
// SimpleWater sheet (WaterSurface.cs) - the alien nest's floor is one, 140 m across, a finger's depth of black water over
// the resin with the room mirrored in it - or a water material on a mesh of its own (a puddle). Unlit: what is under it
// is fogged through three colour stops by how much deeper the level lies behind it, faded in over its soft edge, and a
// reflection is added on top at a clamped fresnel. The game reflects a planar reflection it renders, so it mirrors what
// is off screen too; here the reflected ray is marched through the depth the opaque level left, so only what is on screen
// is mirrored, and elsewhere there is no reflection.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

global uniform float viewer_exposure;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
// DEPTH_FOG_INITIAL / MIDPOINT / END: colour (0-1, squared as the game decodes it) and alpha, at no depth, the midpoint's
// depth and the end's
uniform vec4 fog_initial = vec4(0.0, 0.0, 0.0, 0.0);
uniform vec4 fog_mid = vec4(0.0, 0.0, 0.0, 0.75);
uniform vec4 fog_end = vec4(0.0, 0.0, 0.0, 0.99);
uniform vec2 fog_depths = vec2(0.05, 0.075);
// MIN_FRESNEL, MAX_FRESNEL, FRESNEL_POWER
uniform vec3 fresnel = vec3(0.0, 1.0, 1.0);
// REFLECTIVE_MAPPING
uniform bool reflective = true;
// softness_edge: how deep it fades in over where it meets the level (0: at once)
uniform float softness = 0.0;
// A water material on a mesh of its own (a puddle) faces as its mesh does; a SimpleWater sheet faces up its transform
uniform bool use_mesh_normal = false;
// ALPHA_MASKING: the outline (its alpha) on the mesh's uvs - a puddle's mesh is a square
uniform sampler2D alpha_mask : filter_linear_mipmap, repeat_enable;
uniform bool use_alpha_mask = false;

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

void fragment() {
	float mask = 1.0;
	if (use_alpha_mask) {
		mask = texture(alpha_mask, UV).a;
		if (mask < 0.00001) {
			discard;
		}
	}

	// How much deeper the level lies behind this pixel, along the view (the game's linear depths)
	float depth = max(view_at(SCREEN_UV, INV_PROJECTION_MATRIX).z * -1.0 - VERTEX.z * -1.0, 0.0);
	float t_mid = clamp(depth / max(fog_depths.x, 0.0001), 0.0, 1.0);
	float t_end = clamp((depth - fog_depths.x) / max(fog_depths.y - fog_depths.x, 0.0001), 0.0, 1.0);
	vec4 fog = mix(mix(fog_initial, fog_mid, t_mid), fog_end, t_end);
	float soft = softness > 0.0 ? (cos((1.0 - clamp(depth / softness, 0.0, 1.0)) * PI) + 1.0) * 0.5 : 1.0;
	float alpha = clamp(fog.a, 0.0, 1.0) * soft;
	// premultiplied as the game blends it: the fog's colour squared, by its alpha twice (the mask's once)
	vec3 colour = textureLod(screen_texture, SCREEN_UV, 0.0).rgb * (1.0 - alpha * mask) + fog.rgb * fog.rgb * alpha * alpha * mask * viewer_exposure;

	if (reflective) {
		vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 n = use_mesh_normal ? normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz) : normalize(MODEL_MATRIX[1].xyz);
		vec3 to_eye = normalize(CAMERA_POSITION_WORLD - world);
		if (dot(n, to_eye) < 0.0) {
			n = -n;
		}
		// The game's fresnel: one less the facing, held between MIN and MAX (as HLSL's clamp, MAX winning where they cross),
		// then to its power
		float f = clamp(1.0 - dot(to_eye, n), 0.0, 1.0);
		f = min(pow(clamp(min(max(f, fresnel.x), fresnel.y), 0.0, 1.0), max(fresnel.z, 0.01)), 1.0);

		// The reflected ray, marched until it passes behind the level, then narrowed down to where it crossed. The first
		// step behind the level is taken as the hit whatever its depth there: what the mirror would show behind something
		// is off the screen, and the nearest thing in front of it stands in for it (a gap went black). Not marched where
		// the reflection would add next to nothing - the march is the cost of the water
		vec3 r = reflect(normalize(VERTEX), normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz));
		vec3 p = VERTEX;
		float step_length = max(0.04, -VERTEX.z * 0.01);
		vec2 hit = vec2(-1.0);
		bool march = f * mask > 0.002;
		for (int i = 0; march && i < 48; i++) {
			vec3 q = p + r * step_length;
			vec2 uv = screen_of(q, PROJECTION_MATRIX);
			if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
				break;
			}
			if (view_at(uv, INV_PROJECTION_MATRIX).z - q.z > 0.0) {
				vec3 lo = p;
				vec3 hi = q;
				hit = uv;
				for (int j = 0; j < 5; j++) {
					vec3 mid = (lo + hi) * 0.5;
					vec2 um = screen_of(mid, PROJECTION_MATRIX);
					if (view_at(um, INV_PROJECTION_MATRIX).z - mid.z > 0.0) {
						hi = mid;
						hit = um;
					} else {
						lo = mid;
					}
				}
				break;
			}
			p = q;
			step_length *= 1.08;
		}
		if (hit.x >= 0.0) {
			// Added on top, as the game's is; faded towards the screen's edges, where what it mirrors runs off
			vec2 edge = abs(hit * 2.0 - 1.0);
			float keep = 1.0 - smoothstep(0.8, 1.0, max(edge.x, edge.y));
			colour += textureLod(screen_texture, hit, 0.0).rgb * f * keep * mask;
		}
	}

	ALBEDO = colour;
	ALPHA = 1.0;
}
