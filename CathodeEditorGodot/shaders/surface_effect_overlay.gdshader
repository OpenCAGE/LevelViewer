// What a SurfaceEffectBox / SurfaceEffectSphere paints onto the level (SurfaceEffectOverlay.cs): the game's CA_EFFECT_OVERLAY,
// a deferred pass that finds the surface behind each pixel of its volume and lays a texture over it, projected three ways
// and weighted by the surface's facing - the reactor's and the airport's frost, scorch marks. Drawn after the opaque
// level from the depth it left, with the volume's back faces so it still covers the screen from inside.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;

#include "res://shaders/model_reference_shading.gdshaderinc"

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
// Not source_color: the game squares what it samples (its gamma 2 decode)
uniform sampler2D texture_map : filter_linear_mipmap, repeat_enable;
uniform bool use_texture_map = false;
uniform bool is_sphere = false;
// FALLOFF, INTENSITY, OPACITY, SURFACE_WRAP, TILING_ZY / ZX / XY, COLOUR_TINT and COLOUR_TINT_OUTER (0-1)
uniform vec3 falloff = vec3(1.0);
uniform float intensity = 1.0;
uniform float opacity = 1.0;
uniform float surface_wrap = 0.0;
uniform vec3 tiling = vec3(1.0);
uniform vec3 colour_tint = vec3(1.0);
uniform vec3 colour_tint_outer = vec3(1.0);

void fragment() {
	// The surface behind this pixel, back in the world (derivatives before anything is discarded)
	float depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	// OpenGL (a machine without Vulkan or D3D12 falls back to it) keeps depth -1..1, the others 0..1
	vec4 view = INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV, depth) * 2.0 - 1.0, 1.0);
#else
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
#endif
	view.xyz /= view.w;
	vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
	vec3 world_dx = dFdx(world);
	vec3 world_dy = dFdy(world);
	vec3 n = normalize(cross(world_dx, world_dy));
	if (dot(n, CAMERA_POSITION_WORLD - world) < 0.0) {
		n = -n;
	}

	// Where in the volume it is: the mesh is a unit box or sphere scaled out to it, so -0.5..0.5 along each axis inside
	vec3 rel = world - MODEL_MATRIX[3].xyz;
	vec3 ax = MODEL_MATRIX[0].xyz;
	vec3 ay = MODEL_MATRIX[1].xyz;
	vec3 az = MODEL_MATRIX[2].xyz;
	vec3 local = vec3(dot(rel, ax) / dot(ax, ax), dot(rel, ay) / dot(ay, ay), dot(rel, az) / dot(az, az));
	float fall;
	if (is_sphere) {
		float d = length(local) * 2.0;
		if (d > 1.0) {
			discard;
		}
		fall = 1.0 - d;
	} else {
		vec3 p = abs(local) * 2.0;
		if (max(max(p.x, p.y), p.z) > 1.0) {
			discard;
		}
		vec3 f = clamp((1.0 - p) / max(falloff, vec3(0.0001)), 0.0, 1.0);
		fall = f.x * f.y * f.z;
	}
	fall *= intensity;

	// The texture three ways, weighted by the cubed facing past a dead zone, as the game's
	vec4 tex = vec4(1.0);
	if (use_texture_map) {
		vec3 tw = max((abs(n) - 0.2) * 7.0, vec3(0.0));
		tw = tw * tw * tw;
		tw /= max(tw.x + tw.y + tw.z, 0.0001);
		vec4 s_zy = textureGrad(texture_map, world.zy * tiling.x, world_dx.zy * tiling.x, world_dy.zy * tiling.x);
		vec4 s_zx = textureGrad(texture_map, world.zx * tiling.y, world_dx.zx * tiling.y, world_dy.zx * tiling.y);
		vec4 s_xy = textureGrad(texture_map, world.xy * tiling.z, world_dx.xy * tiling.z, world_dy.xy * tiling.z);
		s_zy.rgb *= s_zy.rgb;
		s_zx.rgb *= s_zx.rgb;
		s_xy.rgb *= s_xy.rgb;
		tex = s_zy * tw.x + s_zx * tw.y + s_xy * tw.z;
	}

	// SURFACE_WRAP: how far round from facing up it reaches (0 = upward faces only); the tint goes from the outer colour to
	// the inner one with the falloff. The coverage is the texture's alpha past a cut the falloff and the facing lower,
	// smoothed - so frost gathers on what faces up and breaks into clumps towards the volume's edges. (Matched to the game
	// by eye, HAB_AIRPORT's frosted pipes and ENG_REACTORCORE's walls: the reconstruction reads its alpha as the texture's
	// alone, which covered the whole volume in a flat coat, where the game's frost is patchy and stops short of it.)
	float wrap = clamp((n.y + surface_wrap) / max(surface_wrap + 1.0, 0.0001), 0.0, 1.0);
	float shade = 1.0 - (1.0 - wrap) * (1.0 - wrap);
	float inner = clamp(wrap * fall, 0.0, 1.0);
	vec4 col = shade * tex;
	vec3 albedo = mix(colour_tint_outer, colour_tint, inner) * col.rgb;
	float edge = clamp(tex.a - (1.0 - fall * wrap), 0.0, 1.0);
	edge = edge * edge * (3.0 - 2.0 * edge);
	float ndl = max(dot(normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz), MODEL_REFERENCE_PREVIEW_LIGHT_DIR), 0.0);
	ALBEDO = model_reference_apply_preview_shading(albedo, ndl);
	ALPHA = clamp(edge * shade * opacity, 0.0, 1.0);
}
