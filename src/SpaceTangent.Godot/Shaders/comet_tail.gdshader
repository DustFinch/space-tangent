// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

void fragment() {
    float distance = 1.0 - UV.y;
    float across = abs(2.0 * UV.x - 1.0);
    float spread = mix(0.18, 0.95, distance);
    float plume = 1.0 - smoothstep(0.0, spread, across);
    ALBEDO = vec3(0.72, 0.80, 1.0);
    ALPHA = 0.54 * plume * exp(-3.0 * distance) * (1.0 - smoothstep(0.68, 1.0, distance));
}
