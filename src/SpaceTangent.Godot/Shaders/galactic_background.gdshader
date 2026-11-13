// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

#include "res://Shaders/galactic_background.gdshaderinc"
#include "res://Shaders/zodiacal_glow.gdshaderinc"

uniform bool show_galactic_image = true;

uniform vec3 camera_forward;
uniform vec3 camera_right;
uniform vec3 camera_up;
uniform float half_field;

void fragment() {
    vec2 offset = vec2(2.0 * UV.x - 1.0, 1.0 - 2.0 * UV.y);
    offset.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec3 ray = normalize(camera_forward + half_field * (camera_right * offset.x + camera_up * offset.y));
    vec3 background = pow(vec3(0.02745, 0.03529, 0.04706), vec3(2.2));
    if (show_galactic_image) {
        background += galactic_radiance(ray);
    }
    if (show_zodiacal_light) {
        background += zodiacal_radiance(ray);
    }
    COLOR = vec4(pow(background, vec3(1.0 / 2.2)), 1.0);
}
