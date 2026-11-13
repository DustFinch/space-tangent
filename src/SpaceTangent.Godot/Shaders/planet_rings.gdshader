// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

#include "res://Shaders/archived_occultation.gdshaderinc"

uniform sampler2D rings_map : source_color, filter_linear_mipmap, repeat_disable;
uniform vec3 sun_in_body;
uniform float polar_ratio = 0.902;
uniform float solar_radius_radians;
varying vec3 ring_position;

void vertex() {
    ring_position = VERTEX;
}

void fragment() {
    vec4 ring = texture(rings_map, UV);
    vec3 point = ring_position / vec3(1.0, polar_ratio, 1.0);
    vec3 ray = normalize(sun_in_body / vec3(1.0, polar_ratio, 1.0));
    float distance = -dot(point, ray);
    float visibility = 1.0;
    if (distance > 0.0) {
        float miss = length(point + ray * distance);
        float penumbra = max(distance * solar_radius_radians / polar_ratio, 0.00005);
        visibility = smoothstep(1.0 - penumbra, 1.0 + penumbra, miss);
    }
    visibility *= archived_light_visibility(ring_position, sun_in_body, archived_light_angle, true);
    float intensity = 0.035 + 0.965 * visibility;
    ALBEDO = ring.rgb * (OUTPUT_IS_SRGB ? pow(intensity, 1.0 / 2.2) : intensity);
    ALPHA = ring.a;
}
