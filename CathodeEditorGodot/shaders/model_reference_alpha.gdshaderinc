uniform sampler2D separate_alpha_map : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_separate_alpha_map = false;
uniform bool separate_alpha_from_green = false;
uniform vec2 separate_alpha_uv_mult = vec2(1.0);
uniform bool alpha_from_luminance = false;
uniform bool alpha_cutout = false;
uniform float alpha_cutout_threshold = 0.5;

float model_reference_sample_mask(vec4 sample) {
	if (alpha_from_luminance) {
		return separate_alpha_from_green
			? sample.g
			: max(max(sample.r, sample.g), sample.b);
	}
	if (separate_alpha_from_green) {
		return sample.g;
	}
	return sample.a;
}

float model_reference_blend_alpha(vec4 diffuse_sample, vec4 separate_sample, float tint_alpha) {
	if (use_separate_alpha_map) {
		return model_reference_sample_mask(separate_sample) * tint_alpha;
	}
	return model_reference_sample_mask(diffuse_sample) * tint_alpha;
}

// Separate-alpha decals: diffuse RGB may be a white mask; colour comes from tint.
vec4 model_reference_apply_diffuse_color(vec4 tint, vec4 tex, bool use_map) {
	if (!use_map) {
		return tint;
	}
	float max_rgb = max(max(tex.r, tex.g), tex.b);
	if (max_rgb > 0.99 && tex.a < 0.999) {
		return vec4(tint.rgb, tint.a);
	}
	return tint * tex;
}

void model_reference_finish_alpha(float alpha) {
	if (alpha_cutout && alpha < alpha_cutout_threshold) {
		discard;
	}
}
