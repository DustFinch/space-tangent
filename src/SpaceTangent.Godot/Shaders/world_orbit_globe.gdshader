// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/world_orbit_air.gdshaderinc"
uniform sampler2D globe_map : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_globe_map;
uniform vec4 globe_color : source_color = vec4(1.0);

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 normal = normalize(air_surface);
    vec3 surface = has_globe_map ? texture(globe_map, UV).rgb : globe_color.rgb;
    surface = OUTPUT_IS_SRGB ? air_srgb_linear(surface) : surface;
    float sun = max(0.0, dot(normal, light_in_body));
    vec3 transmission;
    vec3 radiance;
    world_orbit_air(air_eye * ground_radius_km, normalize(air_surface - air_eye), transmission, radiance);
    vec3 color = surface * (0.013 + 0.94 * sun) * transmission + radiance;
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(color) : color;
}
