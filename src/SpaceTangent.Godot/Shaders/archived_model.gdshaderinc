// SPDX-License-Identifier: GPL-2.0-or-later
uniform vec4 tint : source_color = vec4(1.0);
uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform sampler2D emission_map : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform sampler2D normal_map : filter_linear_mipmap, repeat_enable;
uniform bool has_map;
uniform bool has_normal;
uniform bool packed_normal;
uniform vec3 specular_color;
uniform float specular_power;
uniform float alpha_cutoff = -1.0;
uniform vec3 emission_factor;
uniform vec3 camera_light;
uniform vec3 light_color;
uniform vec3 camera_secondary_light;
uniform vec3 secondary_light_color;
uniform float archived_render_radius = 1.0;
varying vec3 archived_model_position;

#include "res://Shaders/earth_atmosphere.gdshaderinc"
#include "res://Shaders/archived_occultation.gdshaderinc"

void vertex() {
    archived_model_position = mat3(MODELVIEW_MATRIX) * VERTEX / archived_render_radius;
}

float model_light(vec3 normal, vec3 light, float angle, bool primary) {
    float squared = dot(light, light);
    if (squared == 0.0) return 0.0;
    return max(0.0, dot(normal, light * inversesqrt(squared))) *
        archived_light_visibility(archived_model_position, light, angle, primary);
}

vec3 archived_model_color(vec4 texel, vec3 normal, vec3 tangent, vec3 binormal, vec2 uv, vec3 view) {
    vec3 base = texel.rgb;
    vec3 color = tint.rgb;
    vec3 glow = texture(emission_map, uv).rgb;
    if (OUTPUT_IS_SRGB) {
        base = air_srgb_to_linear(base);
        color = air_srgb_to_linear(color);
        glow = air_srgb_to_linear(glow);
    }
    if (has_normal) {
        vec4 encoded = texture(normal_map, uv);
        vec2 xy = (packed_normal ? encoded.ag : encoded.rg) * 2.0 - 1.0;
        float z = sqrt(max(0.0, 1.0 - dot(xy, xy)));
        normal = normalize(mat3(normalize(tangent), normalize(binormal), normal) * vec3(xy, z));
    }
    float first = model_light(normal, camera_light, archived_light_angle, true);
    float second = model_light(normal, camera_secondary_light, archived_second_angle, false);
    vec3 daylight = first * light_color + second * secondary_light_color;
    vec3 surface = base * color * (vec3(0.04) + 0.96 * daylight) + glow * emission_factor;
    if (specular_power > 0.0) {
        if (first > 0.0) {
            vec3 halfway = normalize(view + normalize(camera_light));
            surface += first * light_color * specular_color * pow(max(0.0, dot(normal, halfway)), specular_power);
        }
        if (second > 0.0) {
            vec3 halfway = normalize(view + normalize(camera_secondary_light));
            surface += second * secondary_light_color * specular_color * pow(max(0.0, dot(normal, halfway)), specular_power);
        }
    }
    return OUTPUT_IS_SRGB ? air_linear_to_srgb(surface) : surface;
}
