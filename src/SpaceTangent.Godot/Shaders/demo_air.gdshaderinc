// SPDX-License-Identifier: GPL-2.0-or-later
#include "res://Shaders/world_orbit_air.gdshaderinc"

uniform vec3 first_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 second_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 first_irradiance;
uniform vec3 second_irradiance;
uniform vec3 demo_rayleigh;
uniform vec3 demo_absorption;
uniform vec3 demo_mie_albedo = vec3(1.0);
uniform float demo_mie;
uniform float demo_mie_g;
uniform float demo_rayleigh_height;
uniform float demo_mie_height;
uniform float demo_radius;
uniform float demo_air_top;

vec2 demo_density(vec3 point) {
    float altitude = max(0.0, length(point) - demo_radius);
    return exp(-altitude / vec2(demo_rayleigh_height, demo_mie_height));
}

vec3 demo_extinction(vec2 column) {
    return column.x * demo_rayleigh + column.y * (vec3(demo_mie) + demo_absorption);
}

vec3 demo_scattering(vec3 point, vec3 ray, vec3 light, vec3 flux, vec2 density) {
    if (dot(flux, flux) <= 0.0) return vec3(0.0);
    if (air_span(point, light, demo_radius).x > 0.0) return vec3(0.0);
    float step_km = air_span(point, light, demo_radius + demo_air_top).y / 6.0;
    vec2 light_column = vec2(0.0);
    for (int step = 0; step < 6; step++) {
        light_column += demo_density(point + light * ((float(step) + 0.5) * step_km)) * step_km;
    }
    float mu = dot(ray, light);
    float g = demo_mie_g;
    float rayleigh_phase = 0.0596831 * (1.0 + mu * mu);
    float mie_phase = 0.0795775 * (1.0 - g * g) / pow(max(0.0001, 1.0 + g * g - 2.0 * g * mu), 1.5);
    return flux * exp(-demo_extinction(light_column)) *
        (rayleigh_phase * density.x * demo_rayleigh + mie_phase * density.y * demo_mie * demo_mie_albedo);
}

void demo_air(vec3 eye, vec3 ray, float surface_distance, out vec3 transmission, out vec3 radiance) {
    transmission = vec3(1.0);
    radiance = vec3(0.0);
    if (demo_air_top <= 0.0) return;
    vec2 outer = air_span(eye, ray, demo_radius + demo_air_top);
    vec2 ground = air_span(eye, ray, demo_radius);
    float start = max(0.0, outer.x);
    float end = surface_distance > 0.0 ? min(outer.y, surface_distance) : (ground.x >= start ? min(outer.y, ground.x) : outer.y);
    float step_km = max(0.0, end - start) / 16.0;
    vec2 column = vec2(0.0);
    for (int step = 0; step < 16; step++) {
        vec3 point = eye + ray * (start + (float(step) + 0.5) * step_km);
        vec2 density = demo_density(point);
        vec3 sources = demo_scattering(point, ray, first_direction, first_irradiance, density) +
            demo_scattering(point, ray, second_direction, second_irradiance, density);
        radiance += sources * exp(-demo_extinction(column + 0.5 * step_km * density)) * step_km;
        column += step_km * density;
    }
    transmission = exp(-demo_extinction(column));
}
