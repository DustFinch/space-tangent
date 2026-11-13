// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

void vertex() {
    POINT_SIZE = COLOR.a > 0.9 ? 3.5 : 2.5;
}

void fragment() {
    float radius = length(2.0 * POINT_COORD - 1.0);
    ALBEDO = COLOR.rgb;
    ALPHA = COLOR.a * exp(-3.0 * radius * radius) * (1.0 - smoothstep(0.8, 1.0, radius));
}
