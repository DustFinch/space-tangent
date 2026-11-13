// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/earth_atmosphere.gdshaderinc"

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D night_map : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D cloud_map : filter_linear_mipmap, repeat_enable;
uniform bool has_map;
uniform bool has_night_map;
uniform bool has_cloud_map;
uniform vec4 tint : source_color = vec4(1.0);
uniform bool emissive;
uniform vec3 camera_light;
uniform vec3 light_color = vec3(1.0);
uniform vec3 camera_secondary_light;
uniform vec3 secondary_light_color;

#include "res://Shaders/archived_occultation.gdshaderinc"
#include "res://Shaders/ring_shadow.gdshaderinc"
uniform float archived_ring_polar_ratio = 1.0;
varying vec3 ring_receiver_point;
uniform float archived_render_radius = 1.0;
varying vec3 receiver_point;

void vertex() {
    receiver_point = mat3(MODELVIEW_MATRIX) * VERTEX / archived_render_radius;
    ring_receiver_point = VERTEX * vec3(1.0, archived_ring_polar_ratio, 1.0);
}

float incidence(vec3 normal, vec3 light) {
    float length_squared = dot(light, light);
    return length_squared > 0.0 ? max(0.0, dot(normal, light * inversesqrt(length_squared))) : 0.0;
}

void fragment() {
    vec3 color = has_map ? texture(surface_map, UV).rgb : tint.rgb;
    if (OUTPUT_IS_SRGB) {
        color = air_srgb_to_linear(color);
    }
    vec3 normal = normalize(NORMAL);
    float primary = incidence(normal, camera_light);
    float secondary = incidence(normal, camera_secondary_light);
    primary *= archived_light_visibility(receiver_point, camera_light, archived_light_angle, true);
    if (primary > 0.0 && cast_ring_shadows) {
        primary *= ring_visibility(ring_receiver_point, false);
    }
    secondary *= archived_light_visibility(receiver_point, camera_secondary_light, archived_second_angle, false);
    vec3 daylight = primary * light_color + secondary * secondary_light_color;
    vec3 surface = emissive ? color : color * (vec3(0.04) + 0.96 * daylight);
    if (has_night_map) {
        float city = texture(night_map, UV).r;
        if (OUTPUT_IS_SRGB) {
            city = air_srgb_to_linear(vec3(city)).r;
        }
        surface += vec3(1.0, 0.69, 0.35) * city * (1.0 - smoothstep(0.0, 0.16, max(daylight.r, max(daylight.g, daylight.b))));
    }
    if (has_cloud_map) {
        float coverage = smoothstep(0.06, 0.93, texture(cloud_map, UV).r);
        surface = mix(surface, vec3(0.016) + 0.82 * daylight, 0.88 * coverage);
    }
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_to_srgb(surface) : surface;
}
