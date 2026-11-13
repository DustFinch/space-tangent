// SPDX-License-Identifier: GPL-2.0-or-later

#include "res://Shaders/solar_occultation.gdshaderinc"

uniform bool solar_eclipse_active = false;
uniform vec3 eclipse_sun_km;
uniform vec3 eclipse_moon_km;

const float AIR_TOP_KM = 100.0;
const vec2 AIR_SCALE_KM = vec2(8.5, 1.2);
const vec3 AIR_RAYLEIGH = vec3(0.004847, 0.01149, 0.02870);
const vec3 AIR_ABSORPTION = vec3(0.00407, 0.00254, 0.00001);
const float AIR_MIE = 0.01;
const float AIR_MIE_G = 0.76;
const float AIR_EXPOSURE = 7.0;

float earth_solar_visibility(vec3 position) {
    float visibility = solar_eclipse_active ? solar_visibility(eclipse_sun_km - position, eclipse_moon_km - position, 1737.4) : 1.0;
#ifdef ARCHIVED_EARTH_LIMB
    visibility *= archived_light_visibility(position * vec3(1.0, archived_polar_ratio, 1.0) / earth_radius_km,
        sun_direction, archived_light_angle, true);
#endif
    return visibility;
}

float earth_air_visibility(vec3 eye, vec3 ray, float start, float end, float ground_radius) {
    bool eclipsed = solar_eclipse_active;
#ifdef ARCHIVED_EARTH_LIMB
    eclipsed = eclipsed || archived_shadow_count > 0;
#endif
    if (!eclipsed || end <= start) {
        return 1.0;
    }
    float density = 0.0;
    float lit_density = 0.0;
    for (int i = 0; i < 8; i++) {
        vec3 point = eye + ray * mix(start, end, (float(i) + 0.5) / 8.0);
        float weight = exp(-max(length(point) - ground_radius, 0.0) / AIR_SCALE_KM.x);
        density += weight;
        lit_density += weight * earth_solar_visibility(point);
    }
    return density > 0.0 ? lit_density / density : 1.0;
}

float air_exit(vec3 position, vec3 direction, float radius) {
    float along = dot(position, direction);
    float position_radius = length(position);
    float delta = (radius - position_radius) * (radius + position_radius);
    return max(0.0, -along + sqrt(max(0.0, along * along + delta)));
}

vec3 air_extinction(vec2 column) {
    return (AIR_RAYLEIGH + AIR_ABSORPTION) * column.x + vec3(AIR_MIE * column.y);
}

// Column to space along a spherical exponential profile, in kilometres at sea-level density.
vec2 air_solar_column(float radius, float mu, float ground_radius) {
    vec2 grazing = sqrt((PI / 2.0) * radius / AIR_SCALE_KM);
    vec2 density = exp(-max(radius - ground_radius, 0.0) / AIR_SCALE_KM);
    vec2 ascending = density * AIR_SCALE_KM * grazing / (1.0 + (grazing - 1.0) * abs(mu));
    if (mu >= 0.0) {
        return ascending;
    }
    float tangent_radius = radius * sqrt(max(0.0, 1.0 - mu * mu));
    vec2 tangent_density = exp(-max(tangent_radius - ground_radius, 0.0) / AIR_SCALE_KM);
    vec2 tangent_half = tangent_density * AIR_SCALE_KM * sqrt((PI / 2.0) * tangent_radius / AIR_SCALE_KM);
    return max(vec2(0.0), 2.0 * tangent_half - ascending);
}

void air_integrate(vec3 eye, vec3 ray, vec3 sun, float ground_radius, float start, float end, int steps,
        inout vec2 view_column, inout vec3 radiance) {
    float cosine = clamp(dot(ray, sun), -1.0, 1.0);
    float rayleigh_phase = 3.0 * (1.0 + cosine * cosine) / (16.0 * PI);
    float g = AIR_MIE_G;
    float denominator = 1.0 + g * g - 2.0 * g * cosine;
    float mie_phase = (1.0 - g * g) / (4.0 * PI * denominator * sqrt(denominator));
    float view_projection = dot(eye, ray);
    float solar_projection = dot(eye, sun);
    float eye_radius_squared = dot(eye, eye);
    float previous = start;
    for (int i = 0; i < steps; i++) {
        float fraction = float(i + 1) / float(steps);
        float next = mix(start, end, fraction * fraction);
        float step_km = next - previous;
        float midpoint = 0.5 * (previous + next);
        float radius = sqrt(eye_radius_squared + midpoint * (2.0 * view_projection + midpoint));
        vec2 density = exp(-max(radius - ground_radius, 0.0) / AIR_SCALE_KM);
        vec2 segment_column = density * step_km;
        float mu = clamp((solar_projection + midpoint * cosine) / radius, -1.0, 1.0);
        float ground_ratio = ground_radius / radius;
        float horizon = -sqrt(max(0.0, 1.0 - ground_ratio * ground_ratio));
        // The Sun has a finite angular radius of about 0.27 degrees.
        float visibility = smoothstep(horizon - 0.00465, horizon + 0.00465, mu);
        if (visibility > 0.0) {
            visibility *= earth_solar_visibility(eye + ray * midpoint);
            vec2 column = view_column + 0.5 * segment_column + air_solar_column(radius, mu, ground_radius);
            vec3 source = AIR_RAYLEIGH * (density.x * rayleigh_phase) + vec3(AIR_MIE * density.y * mie_phase);
            radiance += exp(-air_extinction(column)) * source * (step_km * visibility);
        }
        view_column += segment_column;
        previous = next;
    }
}

vec3 air_srgb_to_linear(vec3 color) {
    return mix(color / 12.92, pow((color + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), color));
}

vec3 air_linear_to_srgb(vec3 color) {
    color = max(color, vec3(0.0));
    return mix(12.92 * color, 1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), color));
}

vec3 air_compose(vec3 background, vec2 view_column, vec3 radiance) {
    vec3 transmission = exp(-air_extinction(view_column));
    vec3 sky = 1.0 - exp(-AIR_EXPOSURE * radiance);
    return sky + transmission * air_srgb_to_linear(background);
}
