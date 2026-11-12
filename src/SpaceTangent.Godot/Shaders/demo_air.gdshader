// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never;

#include "res://Shaders/demo_air.gdshaderinc"

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 eye = air_eye * (demo_radius + demo_air_top);
    vec3 ray = normalize(air_surface - air_eye);
    if (air_span(eye, ray, demo_radius).x >= 0.0) discard;
    vec3 transmittance, radiance;
    demo_air(eye, ray, 0.0, transmittance, radiance);
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(radiance) : radiance;
    ALPHA = 1.0;
}
