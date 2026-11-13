// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_back;

#include "res://Shaders/world_orbit_air.gdshaderinc"

uniform sampler2D cloud_map : source_color, filter_linear_mipmap, repeat_enable;
uniform float cloud_offset;
uniform float cloud_radius_km;
uniform bool draw_air = true;

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 ray = normalize(air_surface - air_eye);
    vec3 transmission = vec3(1.0);
    vec3 radiance = vec3(0.0);
    if (draw_air) {
        world_orbit_air_to(air_eye * cloud_radius_km, ray, cloud_radius_km, transmission, radiance);
    }
    vec3 clouds = texture(cloud_map, UV + vec2(0.25 + cloud_offset, 0.0)).rgb;
    clouds = OUTPUT_IS_SRGB ? air_srgb_linear(clouds) : clouds;
    float sun = max(0.0, dot(normalize(air_surface), light_in_body));
    ALBEDO = air_display_color(clouds * (0.013 + 0.94 * sun), transmission, radiance);
}
