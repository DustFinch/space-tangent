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

uniform vec4 archived_blockers[4];
uniform int archived_shadow_count = 0;
uniform float archived_render_radius = 1.0;
uniform vec3 archived_sun;
uniform float archived_sun_radius;
uniform float archived_light_angle;
uniform float archived_second_angle;
varying vec3 receiver_point;

void vertex() {
    receiver_point = mat3(MODELVIEW_MATRIX) * VERTEX / archived_render_radius;
}

float archived_disc_visibility(float separation, float star, float blocker) {
    float d = separation / star;
    float r = blocker / star;
    if (d >= 1.0 + r) return 1.0;
    if (d <= abs(1.0 - r)) return 1.0 - min(1.0, r * r);
    float x = clamp((d * d + 1.0 - r * r) / (2.0 * d), -1.0, 1.0);
    float y = clamp((d * d + r * r - 1.0) / (2.0 * d * r), -1.0, 1.0);
    float lens = max(0.0, (1.0 - (d-r)*(d-r)) * ((d+r)*(d+r) - 1.0));
    return clamp(1.0 - (acos(x) + r*r*acos(y) - 0.5*sqrt(lens)) / PI, 0.0, 1.0);
}

float archived_light_visibility(vec3 direction, float angle, bool use_sun) {
    if (archived_shadow_count == 0 || dot(direction, direction) <= 0.0) return 1.0;
    vec3 light = normalize(direction);
    float limit = 1e30;
    if (use_sun && angle <= 0.0 && archived_sun_radius > 0.0) {
        vec3 offset = archived_sun - receiver_point;
        limit = length(offset);
        light = offset / max(limit, 1e-10);
        angle = asin(clamp(archived_sun_radius / max(limit, 1e-10), 0.0, 1.0));
    }
    float visible = 1.0;
    for (int i = 0; i < archived_shadow_count; i++) {
        vec4 blocker = archived_blockers[i];
        vec3 delta = blocker.xyz - receiver_point;
        float distance = length(delta);
        if (distance <= blocker.w || distance >= limit || dot(delta, light) <= 0.0) continue;
        vec3 axis = delta / distance;
        float separation = atan(length(cross(axis, light)), dot(axis, light));
        visible *= archived_disc_visibility(separation, angle > 0.0 ? angle : 0.0047, asin(clamp(blocker.w / distance, 0.0, 1.0)));
    }
    return visible;
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
    primary *= archived_light_visibility(camera_light, archived_light_angle, true);
    secondary *= archived_light_visibility(camera_secondary_light, archived_second_angle, false);
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
