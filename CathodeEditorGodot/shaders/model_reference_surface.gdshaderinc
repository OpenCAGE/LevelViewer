// What CA_ENVIRONMENT draws over its tinted diffuse that a preview with no lights can still show, as the game builds it
// (decoded from the retail environment shaders): the dirt map - lerped to, or multiplied over - faded by the mesh's
// vertex colours and the alpha-blend noise; the vertex AO; and the environment cube the specular map reflects, which is
// most of what a metal looks like (TECH_HUB's grey ceilings have a 0.016 diffuse tint, its brass a dark brown one).
// Each is off unless AlienSceneMaterials sets it up for the material. The viewer_* globals scale them as a whole
// (project settings, shader globals).

global uniform float viewer_dirt;
global uniform float viewer_vertex_ao;
global uniform float viewer_sheen;
global uniform float viewer_env_reflection;
// 1 to reflect the room's cube where an instance has one, 0 the material's own
global uniform float viewer_env_room;
// The game's fresnel comes from a LUT (view angle, gloss, specular) the viewer doesn't have: how much a plastic reflects head
// on against its specular (1 = all of it), and how far towards the gloss the weight rises at grazing angles (1 = Schlick's)
global uniform float viewer_plastic_reflect;
global uniform float viewer_fresnel_rise;
// A scale on what EMISSIVE surfaces add (1 = as the material asks)
global uniform float viewer_emissive;

// 0 none, 1 lerp towards the dirt colour (DIRT_MAPPING), 2 multiply it over (with DIRT_BLEND_MULTIPLY)
uniform int dirt_mode = 0;
uniform sampler2D dirt_map : source_color, filter_linear_mipmap_anisotropic;
uniform float dirt_uv_mult = 1.0;
// Lerp: saturate(DIRT_BLEND_MULT_SPEC_POWER), how far the dirt goes
uniform float dirt_amount = 1.0;
// Multiply: the specular is scaled by the overlay's green to this power (DIRT_BLEND_MULT_SPEC_POWER as it is) - where it's
// dirty, a floor's vinyl stops reflecting
uniform float dirt_spec_power = 1.0;
uniform float dirt_ao_amount = 0.0;

uniform bool use_alphablend_noise = false;
uniform sampler2D alphablend_noise_map : filter_linear_mipmap_anisotropic;
uniform float alphablend_noise_uv_mult = 1.0;
uniform float alphablend_noise_power = 1.0;

// EMISSIVE: the glow's colour and strength (AlienSceneMaterials - an instance's own, from its mover, on the copy AlienScene
// makes for it), and the negative-tint form that draws the surface as its diffuse's brightness in a colour of its own
uniform bool emissive = false;
uniform vec3 emissive_tint = vec3(1.0);
uniform float emissive_strength = 0.0;
uniform bool emissive_from_luma = false;
uniform vec3 emissive_luma_tint = vec3(1.0);

// What the game fades a cut-out or blended surface's alpha by, from its vertex colours: 0 nothing, 1 the vertex alpha, 2 the
// alpha-blend noise blend (ALPHABLEND_NOISE without dirt) - scrape and grime overlays show only where the mesh paints them
uniform int alpha_fade_mode = 0;

// The material has VERTEX_COLOUR and puts the colour to these uses: x (and y, with the noise) fade the dirt, z is the AO.
// vertex_colour_scale is the instance's vertex_colour_scale / vertex_opacity_scale, which the game multiplies them by.
uniform bool use_vertex_lanes = false;
uniform vec4 vertex_colour_scale = vec4(1.0);
uniform bool use_vertex_ao = false;
uniform vec3 vertex_ao_tint = vec3(0.0);

uniform bool use_specular = false;
uniform sampler2D specular_map : filter_linear_mipmap_anisotropic;
uniform float specular_uv_mult = 1.0;
uniform float specular_tint = 0.0;
uniform float specular_power = 0.0;
uniform float fresnel_intensity = 1.0;
uniform bool use_environment = false;
uniform float environment_map_mult = 1.0;
// REFLECTIVE_PLASTIC: the reflection isn't tinted by the diffuse, and takes its share of it away
uniform bool reflective_plastic = false;
// The cube the game reflects (its ENVIRONMENT_MAP), when the viewer could make one of it. Read as stored: the game squares it.
uniform bool use_environment_cube = false;
uniform samplerCube environment_cube : filter_linear_mipmap;
// The cube of the room the instance is in (the level's EnvironmentMap that claims it), which the game reflects instead
uniform bool use_environment_room_cube = false;
uniform samplerCube environment_room_cube : filter_linear_mipmap;

// The surface's colour as an EMISSIVE material with a negative tint lane draws it: the diffuse texture's brightness in that tint
vec3 model_reference_emissive_base(vec3 color, vec3 diffuse) {
	return emissive_from_luma ? vec3(dot(diffuse, vec3(0.299, 0.587, 0.114))) * emissive_luma_tint : color;
}

// What an EMISSIVE surface adds on top of its shading: its own colour at the glow's strength - past white, as the game's is
vec3 model_reference_emission(vec3 surface) {
	return surface * emissive_tint * (emissive_strength * viewer_emissive);
}

float model_reference_alpha_fade(vec2 uv, vec4 vertex_colour) {
	if (alpha_fade_mode == 0) {
		return 1.0;
	}
	vec4 vc = vertex_colour * vertex_colour_scale;
	if (alpha_fade_mode == 1) {
		return clamp(vc.w, 0.0, 1.0);
	}
	float lane_x = clamp(vc.x + vc.y, 0.0, 1.0);
	float lane_y = (1.0 - abs(lane_x - 0.5) * 2.0) * alphablend_noise_power;
	float noise = use_alphablend_noise ? texture(alphablend_noise_map, uv * alphablend_noise_uv_mult).y * 2.0 - 1.0 : 0.0;
	return clamp(noise * lane_y + lane_x, 0.0, 1.0);
}

vec3 model_reference_surface_layers(vec3 color, vec2 uv, vec4 vertex_colour, vec3 normal, vec3 view, mat3 view_to_world) {
	// The vertex colour lanes as the game's vertex shader leaves them
	vec4 vc = vertex_colour * vertex_colour_scale;
	float lane_x = clamp(vc.x + vc.y, 0.0, 1.0);
	float lane_y = use_alphablend_noise ? (1.0 - abs(lane_x - 0.5) * 2.0) * alphablend_noise_power : vc.y;
	float noise_blend = lane_x;
	if (use_vertex_lanes && use_alphablend_noise && dirt_mode != 0) {
		float noise = texture(alphablend_noise_map, uv * alphablend_noise_uv_mult).y * 2.0 - 1.0;
		noise_blend = clamp(noise * lane_y + lane_x, 0.0, 1.0);
	}

	vec3 vao = vec3(1.0);
	if (use_vertex_lanes && use_vertex_ao) {
		vao = 1.0 - (1.0 - vertex_colour.z * vertex_colour_scale.z) * (1.0 - vertex_ao_tint);
		vao = mix(vec3(1.0), vao, viewer_vertex_ao);
	}
	color *= vao;

	float spec_dirt = 1.0;
	if (dirt_mode != 0 && viewer_dirt > 0.0) {
		vec4 dirt = texture(dirt_map, uv * dirt_uv_mult);
		if (dirt_mode == 1) {
			// Lerp: the dirt is where the lanes are low - a vertex colour of 1 is the clean surface
			float fade = 1.0;
			if (use_vertex_lanes) {
				fade = use_alphablend_noise ? (1.0 - noise_blend) * (1.0 - lane_x) : (1.0 - lane_x) * (1.0 - lane_x);
			}
			vec3 factor = vec3(1.0);
			if (use_vertex_lanes) {
				factor = use_vertex_ao ? dirt_ao_amount * (vao - 1.0) + 1.0 : vec3(dirt_ao_amount * (vc.z - 1.0) + 1.0);
			}
			color = mix(color, dirt.rgb * factor, fade * dirt.a * dirt_amount * viewer_dirt);
		} else {
			// Multiply: washed out to white where the lanes are high
			vec3 overlay = dirt.rgb;
			if (use_vertex_lanes) {
				float keep = use_alphablend_noise ? noise_blend * lane_x : lane_x * lane_x;
				overlay = overlay * (1.0 - keep) + keep;
			}
			overlay = mix(vec3(1.0), overlay, viewer_dirt);
			color *= overlay;
			spec_dirt = pow(clamp(overlay.g, 0.0001, 1.0), dirt_spec_power);
		}
	}

	if (use_specular && (viewer_sheen > 0.0 || viewer_env_reflection > 0.0)) {
		// The game reflects its environment cube by the specular map's intensity - the fresnel's weight head on, rising
		// towards 1 at grazing angles as far as the gloss allows (its LUT, here Schlick's) - tinted by the diffuse's hue, which
		// is most of what chrome, brass and the dark metals look like. Along the mirrored view, blurrier the less glossy (the
		// mip as the game picks it). Without a cube, a constant stands in.
		vec2 s = texture(specular_map, uv * specular_uv_mult).xy;
		float spec = specular_tint * s.x * spec_dirt;
		float gloss = min(specular_power * s.y, 0.996);
		vec3 n = normalize(normal);
		vec3 v = normalize(view);
		float nv = clamp(abs(dot(n, v)), 0.0, 1.0);
		float f0 = reflective_plastic ? spec * viewer_plastic_reflect : spec;
		float schlick = f0 + (max(gloss, f0) - f0) * pow(1.0 - nv, 5.0) * viewer_fresnel_rise;
		float weight = clamp(fresnel_intensity * (schlick - f0) + f0, 0.0, 1.0);
		float peak = max(max(color.r, color.g), max(color.b, 0.0001));
		vec3 hue = reflective_plastic ? vec3(1.0) : clamp(color / peak, vec3(0.0), vec3(1.0));
		vec3 reflected;
		if (use_environment && (use_environment_cube || use_environment_room_cube)) {
			// Looked up along the game's axes, which the viewer's z runs the other way to (CathodeCoordinates): checked against
			// the lobby's own cube, whose +X face is the view along the game's +X from where it was captured
			vec3 r = view_to_world * reflect(-v, n);
			vec3 dir = vec3(r.x, r.y, -r.z);
			float mip = max(log2((1.0 - max(gloss, 0.0)) * 0.75 + 0.015625) + 17.0 / 3.0, 0.0);
			bool room = use_environment_room_cube && (viewer_env_room > 0.5 || !use_environment_cube);
			vec3 seen = room ? textureLod(environment_room_cube, dir, mip).rgb : textureLod(environment_cube, dir, mip).rgb;
			reflected = seen * seen * environment_map_mult * viewer_env_reflection;
		} else {
			reflected = vec3((use_environment ? environment_map_mult : 0.5) * (0.5 + 0.5 * gloss) * viewer_sheen);
		}
		if (reflective_plastic) {
			// Its share dimmed by the vertex AO too, as the game does for plastic
			weight *= vao.g;
			color *= 1.0 - weight;
		}
		color += hue * weight * reflected;
	}
	return color;
}
