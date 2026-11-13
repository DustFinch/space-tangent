// SPDX-License-Identifier: GPL-2.0-or-later
#include "res://Shaders/atmosphere_lookup.gdshaderinc"

uniform vec3 light_in_body = vec3(1.0, 0.0, 0.0);
uniform float ground_radius_km;
uniform float shell_height_km;
uniform float air_base_km = 0.0;
uniform vec3 air_color = vec3(0.82, 0.62, 0.36);
uniform float air_scale_height_km = 9.0;
uniform float air_extinction_per_km = 0.012;
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

void world_orbit_air_to(vec3 eye, vec3 ray, float solid_radius_km, out vec3 transmission, out vec3 radiance) {
    if (use_atmosphere_lookup) {
        vec3 entry;
        sample_atmosphere_lookup(eye, ray, light_in_body, ground_radius_km, transmission, radiance, entry);
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
