// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never;

#include "res://Shaders/world_orbit_air.gdshaderinc"

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 transmission;
    vec3 radiance;
    world_orbit_air(air_eye * (ground_radius_km + shell_height_km), normalize(air_surface - air_eye), transmission, radiance);
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(radiance) : radiance;
    ALPHA = 1.0;
}
