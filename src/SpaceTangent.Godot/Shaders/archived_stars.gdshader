// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://Shaders/earth_atmosphere.gdshaderinc"

uniform mat3 sky_to_camera;
uniform vec3 eye_ly;
uniform vec3 near_origin;
uniform float near_cut;
uniform float years;
uniform float faintest;
uniform float brightness_scale;
uniform float saturation_point;
uniform vec3 ground_beta;
uniform vec3 zenith;
uniform float refraction;
uniform float twinkle;
uniform float animation_time;

varying float opacity;
varying float core;
varying float glare;

void vertex() {
    vec3 position = VERTEX + CUSTOM0.xyz * years;
    vec3 offset = position - eye_ly;
    float distance = max(length(offset), 1e-12);
    float magnitude = UV.x + 5.0 * log(distance) / log(10.0);
    float alpha = (faintest - magnitude) * brightness_scale;
    if (length(position - near_origin) < near_cut) {
        alpha = 0.0;
    }
    float disc = alpha > 1.0 ? clamp(saturation_point - magnitude + 2.0, 2.0, 20.0) : 0.5;
    glare = clamp((disc - 2.0) / 4.0, 0.0, 0.65);
    core = 0.5 / disc;
    vec3 view = normalize(sky_to_camera * (offset / distance) + ground_beta);
    float altitude_sine = clamp(dot(view, zenith), -1.0, 1.0);
    float altitude = degrees(asin(altitude_sine));
    vec3 horizontal = view - zenith * altitude_sine;
    if (refraction > 0.0 && altitude >= -2.0 && length(horizontal) > 1e-6) {
        float lifted = radians(altitude + refraction * 1.02 / tan(radians(altitude + 10.3 / (altitude + 5.11))) / 60.0);
        view = normalize(normalize(horizontal) * cos(lifted) + zenith * sin(lifted));
    }
    float seed = UV.x + VERTEX.x * 7.31 + VERTEX.z * 3.17;
    float phase = fract(sin(seed * 12.9898) * 43758.5453) * TAU;
    float amplitude = twinkle * min(1.0, 0.12 / max(dot(view, zenith), 0.03));
    float scintillation = max(0.0, 1.0 + amplitude * (0.6 * sin(animation_time * 21.0 + phase)
        + 0.4 * sin(animation_time * 34.0 + 2.3 * phase)));
    opacity = clamp(alpha, 0.0, 1.0) * scintillation;
    POSITION = PROJECTION_MATRIX * vec4(mat3(MODELVIEW_MATRIX) * view, 0.0);
    POSITION.z = POSITION.w * CLIP_SPACE_FAR;
    POINT_SIZE = alpha > 0.0 ? 10.0 * disc : 0.0;
}

void fragment() {
    float radius = length(POINT_COORD * 2.0 - 1.0);
    float distance = radius / core;
    float halo = 1.0 - smoothstep(0.0, 1.0, radius);
    ALBEDO = OUTPUT_IS_SRGB ? COLOR.rgb : air_srgb_to_linear(COLOR.rgb);
    ALPHA = clamp(opacity * exp(-8.0 * distance * distance) + glare * halo * halo, 0.0, 1.0);
}
