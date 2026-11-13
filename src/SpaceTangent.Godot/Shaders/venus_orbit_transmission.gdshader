// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_mul, cull_front, depth_draw_never;

#include "res://Shaders/world_orbit_air.gdshaderinc"

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 ray = normalize(air_surface - air_eye);
    vec3 transmission = venus_sky_transmission(air_eye * (ground_radius_km + shell_height_km), ray);
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(transmission) : transmission;
    ALPHA = 1.0;
}
