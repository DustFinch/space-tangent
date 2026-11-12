// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D landscape_ground : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D landscape_lights : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D landscape_fog : source_color, filter_linear_mipmap, repeat_enable;
uniform vec3 ground_bounds;
uniform vec3 lights_bounds;
uniform vec3 fog_bounds;
uniform bool has_lights = false;
uniform bool has_fog = false;
uniform vec3 forward;
uniform vec3 right;
uniform vec3 camera_up;
uniform vec3 north;
uniform vec3 east;
uniform vec3 zenith;
uniform float tan_half_field;
uniform float day_factor;
uniform float night_factor;

vec4 sample_panorama(sampler2D panorama, vec3 bounds, float azimuth, float altitude) {
    float row = (bounds.x - altitude) / (bounds.x - bounds.y);
    if (row < 0.0 || row > 1.0) {
        return vec4(0.0);
    }
    float column = fract((azimuth - 90.0 - bounds.z) / 360.0);
    float half_texel = 0.5 / float(textureSize(panorama, 0).y);
    return texture(panorama, vec2(column, clamp(row, half_texel, 1.0 - half_texel)));
}

void fragment() {
    vec2 screen = vec2(2.0 * UV.x - 1.0, 1.0 - 2.0 * UV.y);
    screen.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec3 ray = normalize(forward + tan_half_field * (screen.x * right + screen.y * camera_up));
    float altitude = degrees(asin(clamp(dot(ray, zenith), -1.0, 1.0)));
    float azimuth = degrees(atan(dot(ray, east), dot(ray, north)));
    vec4 ground = sample_panorama(landscape_ground, ground_bounds, azimuth, altitude);
    vec3 emission = vec3(0.0);
    if (has_lights) {
        vec4 lights = sample_panorama(landscape_lights, lights_bounds, azimuth, altitude);
        emission += lights.rgb * lights.a * night_factor;
    }
    if (has_fog) {
        vec4 fog = sample_panorama(landscape_fog, fog_bounds, azimuth, altitude);
        emission += fog.rgb * fog.a * (0.05 + 0.15 * day_factor);
    }
    float alpha = max(ground.a, min(1.0, max(emission.r, max(emission.g, emission.b))));
    if (alpha <= 0.001) {
        discard;
    }
    vec3 surface = ground.rgb * ground.a * day_factor;
    COLOR = vec4((surface + emission) / alpha, alpha);
}
