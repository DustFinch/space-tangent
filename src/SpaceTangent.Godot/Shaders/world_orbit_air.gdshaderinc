// SPDX-License-Identifier: GPL-2.0-or-later
#include "res://Shaders/atmosphere_lookup.gdshaderinc"
#include "res://Shaders/solar_occultation.gdshaderinc"

#include "res://Shaders/archived_occultation.gdshaderinc"

uniform float occulting_radius_km = 0.0;
uniform vec3 to_occluder_km;
uniform vec3 to_sun_km;

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

uniform float archived_polar_ratio = 1.0;

float world_solar_visibility(vec3 point) {
    float visibility = occulting_radius_km > 0.0 ?
        solar_visibility(to_sun_km - point, to_occluder_km - point, occulting_radius_km) : 1.0;
    if (archived_shadow_count > 0) {
        visibility *= archived_light_visibility(point * vec3(1.0, archived_polar_ratio, 1.0) / ground_radius_km,
            light_in_body, archived_light_angle, true);
    }
    return visibility;
}

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

#include "res://Shaders/venus_orbit_scattering.gdshaderinc"

void world_orbit_air_to(vec3 eye, vec3 ray, float solid_radius_km, out vec3 transmission, out vec3 radiance) {
    if (use_atmosphere_lookup) {
        vec3 entry;
        sample_atmosphere_lookup(eye, ray, light_in_body, ground_radius_km, transmission, radiance, entry);
        if (occulting_radius_km > 0.0 || archived_shadow_count > 0) {
            vec2 shell = air_span(eye, ray, ground_radius_km + shell_height_km);
            vec2 ground = air_span(eye, ray, solid_radius_km);
            float start = max(0.0, shell.x);
            float end = ground.x >= 0.0 ? min(shell.y, ground.x) : shell.y;
            float lit = 0.0;
            float density = 0.0;
            for (int index = 0; index < 8; index++) {
                vec3 point = eye + ray * mix(start, end, (float(index) + 0.5) / 8.0);
                float weight = exp(-max(length(point) - ground_radius_km, 0.0) / air_scale_height_km);
                lit += weight * world_solar_visibility(point);
                density += weight;
            }
            if (end > start && density > 0.0) radiance *= lit / density;
        }
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
            radiance += exp(-optical_depth - depth * 0.5) * depth * phase * air_color * world_solar_visibility(point);
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
    vec3 color = base * transmission + radiance;
    return OUTPUT_IS_SRGB ? air_linear_srgb(color) : color;
}