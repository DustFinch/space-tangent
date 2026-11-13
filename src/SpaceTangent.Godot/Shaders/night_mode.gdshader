// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen : hint_screen_texture, filter_nearest, repeat_disable;

void fragment() {
    float brightness = dot(textureLod(screen, SCREEN_UV, 0.0).rgb, vec3(0.2126, 0.7152, 0.0722));
    COLOR = vec4(brightness, 0.0, 0.0, 1.0);
}
