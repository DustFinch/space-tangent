// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, shadows_disabled;

void vertex() {
    POSITION = PROJECTION_MATRIX * vec4(mat3(MODELVIEW_MATRIX) * VERTEX, 0.0);
    POSITION.z = POSITION.w * CLIP_SPACE_FAR;
}

void fragment() {
    float edge = max(abs(UV.x - 0.5), abs(UV.y - 0.5)) * 2.0;
    ALBEDO = COLOR.rgb;
    ALPHA = COLOR.a * (1.0 - smoothstep(0.35, 1.0, edge));
}
