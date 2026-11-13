// SPDX-License-Identifier: GPL-2.0-or-later
#include "res://Shaders/atmosphere_lookup.gdshaderinc"

uniform vec3 light_in_body = vec3(1.0, 0.0, 0.0);
uniform float ground_radius_km;
uniform float shell_height_km;
uniform float air_base_km = 0.0;
uniform vec3 air_color = vec3(0.82, 0.62, 0.36);
uniform float air_scale_height_km = 9.0;
uniform float air_extinction_per_km = 0.012;
uniform bool venus_scattering = false;
varying vec3 air_surface;
varying flat vec3 air_eye;

vec2 air_span(vec3 eye, vec3 ray, float radius) {
    float nearest = dot(eye, ray);
    vec3 closest = eye - ray * nearest;
    float squared = radius * radius - dot(closest, closest);
    if (squared < 0.0) {
        return vec2(-1.0);
    }
    float width = sqrt(squared);
    return vec2(-nearest - width, -nearest + width);
}

// Legacy Venus catalogue, with distances in kilometres and coefficients in inverse kilometres.
const vec3 venus_rayleigh = vec3(0.45296, 1.07549, 2.69291);
const float venus_mie = 0.5;
const float venus_height = 11.4;
const float venus_asymmetry = 0.6;
const float venus_schlick = 1.55 * venus_asymmetry - 0.55 * venus_asymmetry * venus_asymmetry * venus_asymmetry;

float venus_column(float radius, float cosine) {
    float mu = clamp(cosine, -1.0, 1.0);
    float density = exp(min((ground_radius_km - radius) / venus_height, 0.0));
    float grazing = sqrt(1.57079632679 * radius / venus_height);
    float outward = density * venus_height * grazing / (1.0 + (grazing - 1.0) * abs(mu));
    if (mu >= 0.0) {
        return outward;
    }
    float tangent = radius * sqrt(max(1e-8, 1.0 - mu * mu));
    return max(0.0, 2.0 * venus_height * sqrt(1.57079632679 * tangent / venus_height) *
        exp(min((ground_radius_km - tangent) / venus_height, 0.0)) - outward);
}

void venus_air(vec3 eye, vec3 ray, float solid_radius, out vec3 transmission, out vec3 radiance) {
    float shell_radius = ground_radius_km + shell_height_km;
    vec2 outer = air_span(eye, ray, shell_radius);
    vec2 inner = air_span(eye, ray, solid_radius);
    float start = max(0.0, outer.x);
    float end = inner.x >= 0.0 ? min(outer.y, inner.x) : outer.y;
    float step_km = max(0.0, end - start) / 6.0;
    vec3 extinction = venus_rayleigh + vec3(venus_mie);
    float total = 0.0;
    vec3 scattered = vec3(0.0);
    float boundary_density = exp(-shell_height_km / venus_height);
    float boundary_grazing = sqrt(1.57079632679 * shell_radius / venus_height);
    float shadow_width = (shell_radius + ground_radius_km) * 0.5 *
        min(0.2, sqrt(2.0 * venus_height / ground_radius_km));

    vec3 segment_start = eye + ray * start;
    for (int index = 0; index < 6; index++) {
        vec3 segment_end = segment_start + ray * step_km;
        float r0 = length(segment_start);
        float r1 = length(segment_end);
        bool inward = r1 < r0;
        vec3 direction = inward ? -ray : ray;
        float column0 = venus_column(r0, dot(segment_start, direction) / r0);
        float column1 = venus_column(r1, dot(segment_end, direction) / r1);
        float depth = max(0.0, inward ? column1 - column0 : column0 - column1);
        vec3 point = mix(segment_start, segment_end, 0.5);
        float r = length(point);
        float sun_dot = dot(point, light_in_body);
        float horizon = sqrt(max(0.0, r * r - ground_radius_km * ground_radius_km));
        float lit = smoothstep(-shadow_width, shadow_width, sun_dot + horizon);
        if (lit > 0.0) {
            float exit_cos = sqrt(max(0.0, sun_dot * sun_dot - r * r + shell_radius * shell_radius)) / shell_radius;
            float exit_column = boundary_density * venus_height * boundary_grazing /
                (1.0 + (boundary_grazing - 1.0) * clamp(exit_cos, 0.0, 1.0));
            float sun_column = max(0.0, venus_column(r, sun_dot / r) - exit_column);
            vec3 tau = extinction * depth;
            vec3 segment = (vec3(1.0) - exp(-tau)) / extinction;
            vec3 thin_segment = depth * (vec3(1.0) - 0.5 * tau + tau * tau / 6.0);
            segment = mix(thin_segment, segment, step(vec3(0.001), tau));
            scattered += exp(-extinction * (total + sun_column)) * segment * lit;
        }
        total += depth;
        segment_start = segment_end;
    }
    transmission = exp(-extinction * total);
    float cosine = dot(-ray, light_in_body);
    float mie_denominator = 1.0 + venus_schlick * cosine;
    float mie_phase = (1.0 - venus_schlick * venus_schlick) / (mie_denominator * mie_denominator);
    float rayleigh_phase = 0.75 * (1.0 + cosine * cosine);
    radiance = (venus_rayleigh * rayleigh_phase + vec3(venus_mie * mie_phase)) * scattered;
}

void world_orbit_air_to(vec3 eye, vec3 ray, float solid_radius_km, out vec3 transmission, out vec3 radiance) {
    if (use_atmosphere_lookup) {
        vec3 entry;
        sample_atmosphere_lookup(eye, ray, light_in_body, ground_radius_km, transmission, radiance, entry);
        return;
    }
    if (venus_scattering) {
        venus_air(eye, ray, solid_radius_km, transmission, radiance);
        return;
    }
    // Approximate cloud-top haze in the visible band. The deck is opaque below its top.
    vec2 outer = air_span(eye, ray, ground_radius_km + shell_height_km);
    vec2 inner = air_span(eye, ray, solid_radius_km);
    float start = max(0.0, outer.x);
    float end = inner.x >= 0.0 ? min(outer.y, inner.x) : outer.y;
    float step_km = max(0.0, end - start) / 24.0;
    float optical_depth = 0.0;
    radiance = vec3(0.0);
    float phase = 0.3 + 0.7 * pow(max(0.0, 0.5 + 0.5 * dot(ray, light_in_body)), 3.0);
    for (int index = 0; index < 24; index++) {
        vec3 point = eye + ray * (start + (float(index) + 0.5) * step_km);
        float altitude = length(point) - ground_radius_km;
        float depth = exp(-max(0.0, altitude - air_base_km) / air_scale_height_km) * air_extinction_per_km * step_km;
        vec2 occlusion = air_span(point, light_in_body, solid_radius_km);
        if (occlusion.x <= 0.0) {
            radiance += exp(-optical_depth - depth * 0.5) * depth * phase * air_color;
        }
        optical_depth += depth;
    }
    transmission = vec3(exp(-optical_depth));
}

void world_orbit_air(vec3 eye, vec3 ray, out vec3 transmission, out vec3 radiance) {
    world_orbit_air_to(eye, ray, ground_radius_km, transmission, radiance);
}

vec3 air_linear_srgb(vec3 value) {
    return mix(12.92 * value, 1.055 * pow(max(value, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055,
        greaterThan(value, vec3(0.0031308)));
}

vec3 air_srgb_linear(vec3 value) {
    return mix(value / 12.92, pow((value + 0.055) / 1.055, vec3(2.4)),
        greaterThan(value, vec3(0.04045)));
}

vec3 air_display_color(vec3 base, vec3 transmission, vec3 radiance) {
    if (venus_scattering) {
        vec3 display = air_linear_srgb(base) * transmission + radiance;
        return OUTPUT_IS_SRGB ? display : air_srgb_linear(display);
    }
    vec3 color = base * transmission + radiance;
    return OUTPUT_IS_SRGB ? air_linear_srgb(color) : color;
}