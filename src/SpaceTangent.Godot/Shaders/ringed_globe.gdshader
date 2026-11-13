// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
#include "res://Shaders/ring_shadow.gdshaderinc"
uniform vec4 surface_color : source_color = vec4(1.0);
uniform bool has_surface_map;
uniform float prime_meridian_offset;
uniform float polar_ratio = 0.902;
varying vec3 globe_direction;

void vertex() {
    globe_direction = normalize(VERTEX);
}

void fragment() {
    vec3 unit = normalize(globe_direction);
    vec3 point = unit * vec3(1.0, polar_ratio, 1.0);
    vec3 normal = normalize(unit / vec3(1.0, polar_ratio, 1.0));
    float incidence = max(dot(normal, sun_in_body), 0.0);
    float visibility = incidence > 0.0 ? ring_visibility(point, false) : 1.0;
    vec3 color = has_surface_map ? texture(surface_map, UV + vec2(prime_meridian_offset, 0.0)).rgb : surface_color.rgb;
    float intensity = 0.035 + 0.965 * incidence * visibility;
    ALBEDO = color * (OUTPUT_IS_SRGB ? pow(intensity, 1.0 / 2.2) : intensity);
}
