// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D frame : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform float blur_pixels = 0.0;
uniform vec2 wander_pixels;

void fragment() {
    vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
    vec2 relative = (SCREEN_UV - vec2(0.5)) * size;
    float radius = min(size.x, size.y) * 0.5;
    float field = 1.0 - smoothstep(radius - 1.5, radius, length(relative));
    vec2 seen = SCREEN_UV + wander_pixels * SCREEN_PIXEL_SIZE;
    vec3 image = textureLod(frame, seen, log2(max(1.0, blur_pixels))).rgb;
    float reticle = (1.0 - smoothstep(0.4, 1.2, min(abs(relative.x), abs(relative.y)))) *
        smoothstep(7.0, 9.0, length(relative)) * (1.0 - smoothstep(21.0, 23.0, length(relative)));
    image = mix(image, vec3(0.65, 0.72, 0.64), 0.35 * reticle);
    COLOR = vec4(image * field, 1.0);
}
