// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

varying vec4 star;

void vertex() {
    star = COLOR;
    POINT_SIZE = 1.8 + 3.4 * COLOR.a;
}

void fragment() {
    float distance_to_center = length(POINT_COORD * 2.0 - 1.0);
    ALBEDO = star.rgb;
    ALPHA = (1.0 - smoothstep(0.12, 1.0, distance_to_center)) * sqrt(star.a);
}
