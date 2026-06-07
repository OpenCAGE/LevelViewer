// Shared preview "fullbright" shading: directional cue with selective toe/shoulder.

const vec3 MODEL_REFERENCE_PREVIEW_LIGHT_DIR = normalize(vec3(0.45, 0.82, 0.35));

// Max display brightness for lit surfaces (lower = less blown-out whites).
const float PREVIEW_PEAK_CAP = 0.68;

vec3 model_reference_cap_peak(vec3 rgb) {
	float peak = max(max(rgb.r, rgb.g), rgb.b);
	if (peak > PREVIEW_PEAK_CAP) {
		rgb *= PREVIEW_PEAK_CAP / peak;
	}
	return rgb;
}

// Luma-preserving highlight compression; keeps hue, pulls bright texels down.
vec3 model_reference_compress_highlights(vec3 albedo) {
	float luma = dot(albedo, vec3(0.299, 0.587, 0.114));
	if (luma < 0.0001) {
		return albedo;
	}

	float blend = smoothstep(0.38, 0.82, luma);
	float compressed = luma / (luma + 0.55);
	float new_luma = mix(luma, compressed, blend);
	return albedo * (new_luma / luma);
}

vec3 model_reference_level_albedo(vec3 albedo) {
	albedo = min(max(albedo, vec3(0.0)), vec3(4.0));

	float luma = dot(albedo, vec3(0.299, 0.587, 0.114));
	float toe = 1.0 - smoothstep(0.06, 0.26, luma);
	vec3 lifted = pow(albedo, vec3(0.82));
	albedo = mix(albedo, lifted, toe * 0.45);

	albedo = model_reference_compress_highlights(albedo);
	return model_reference_cap_peak(albedo);
}

vec3 model_reference_apply_preview_shading(vec3 albedo, float ndl) {
	albedo = model_reference_level_albedo(albedo);
	float shade = 0.50 + 0.50 * ndl;
	vec3 lit = albedo * shade;
	lit = model_reference_compress_highlights(lit);
	return model_reference_cap_peak(lit);
}
