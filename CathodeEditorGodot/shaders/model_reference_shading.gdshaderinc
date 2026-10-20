// Shared preview "fullbright" shading: directional cue with selective toe/shoulder.

const vec3 MODEL_REFERENCE_PREVIEW_LIGHT_DIR = normalize(vec3(0.45, 0.82, 0.35));

// The shading's knobs (project settings, shader globals): the max display brightness for lit surfaces (lower = less
// blown-out whites), how hard bright texels are pulled down, a scale on every surface before it's levelled, and how
// dark a surface facing away from the preview light goes.
global uniform float viewer_peak_cap;
global uniform float viewer_highlight_compress;
global uniform float viewer_exposure;
global uniform float viewer_shade_floor;
// How the material's diffuse tint is read: 0 as an sRGB colour (decoded to linear), 1 as the linear multiplier the game
// applies it as - and how much colour is left in the levelled albedo (1 = all of it)
global uniform float viewer_tint_linear;
global uniform float viewer_saturation;
// How far the darkest surfaces are lifted towards readable (0 = drawn as they are)
global uniform float viewer_toe_lift;

vec4 model_reference_tint(vec4 tint) {
	vec3 c = clamp(tint.rgb, vec3(0.0), vec3(1.0));
	vec3 decoded = mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
	return vec4(mix(decoded, tint.rgb, viewer_tint_linear), tint.a);
}

vec3 model_reference_cap_peak(vec3 rgb) {
	float peak = max(max(rgb.r, rgb.g), rgb.b);
	if (peak > viewer_peak_cap) {
		rgb *= viewer_peak_cap / peak;
	}
	return rgb;
}

// Luma-preserving highlight compression; keeps hue, pulls bright texels down.
vec3 model_reference_compress_highlights(vec3 albedo) {
	float luma = dot(albedo, vec3(0.299, 0.587, 0.114));
	if (luma < 0.0001) {
		return albedo;
	}

	float blend = smoothstep(0.38, 0.82, luma) * viewer_highlight_compress;
	float compressed = luma / (luma + 0.55);
	float new_luma = mix(luma, compressed, blend);
	return albedo * (new_luma / luma);
}

vec3 model_reference_level_albedo(vec3 albedo) {
	albedo = min(max(albedo * viewer_exposure, vec3(0.0)), vec3(4.0));

	float luma = dot(albedo, vec3(0.299, 0.587, 0.114));
	albedo = max(mix(vec3(luma), albedo, viewer_saturation), vec3(0.0));
	float toe = 1.0 - smoothstep(0.06, 0.26, luma);
	vec3 lifted = pow(albedo, vec3(0.82));
	albedo = mix(albedo, lifted, toe * viewer_toe_lift);

	albedo = model_reference_compress_highlights(albedo);
	return model_reference_cap_peak(albedo);
}

vec3 model_reference_apply_preview_shading(vec3 albedo, float ndl) {
	albedo = model_reference_level_albedo(albedo);
	float shade = viewer_shade_floor + (1.0 - viewer_shade_floor) * ndl;
	vec3 lit = albedo * shade;
	lit = model_reference_compress_highlights(lit);
	return model_reference_cap_peak(lit);
}

// Whether the mesh's own vertex colours tint the diffuse: set per material, where the game's VERTEX_COLOUR does
// (ModelReferenceMaterialOverrides.TintsWithVertexColour). A mesh without vertex colours reads as white.
uniform bool use_vertex_colour = false;

// The vertex colour tint as the game builds it: the instance's vertex_colour_scale, times the mesh's vertex colour where
// the material tints with it
vec4 model_reference_vertex_tint(vec4 vertex_colour_tint, vec4 vertex_colour) {
	return use_vertex_colour ? vertex_colour_tint * vertex_colour : vertex_colour_tint;
}

vec4 model_reference_apply_vertex_colour_tint(vec4 color, vec4 vertex_colour_tint) {
	return vec4(color.rgb * vertex_colour_tint.rgb, color.a * vertex_colour_tint.a);
}
