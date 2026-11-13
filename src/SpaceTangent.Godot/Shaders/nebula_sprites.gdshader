// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, depth_test_disabled;

varying vec4 sprite_color;

void vertex() {
    sprite_color = COLOR;
    float diameter = UV2.x * length(MODELVIEW_MATRIX[0].xyz);
    vec3 center = VERTEX - vec3((UV - 0.5) * UV2.x, 0.0);
    vec4 view = MODELVIEW_MATRIX * vec4(center, 1.0);
    view.xy += (UV - 0.5) * diameter;
    POSITION = PROJECTION_MATRIX * view;
}

void fragment() {
    vec2 radial = UV * 2.0 - 1.0;
    float squared = dot(radial, radial);
    ALBEDO = OUTPUT_IS_SRGB ? sprite_color.rgb : pow(sprite_color.rgb, vec3(2.2));
    ALPHA = sprite_color.a * exp(-6.0 * squared) * step(squared, 1.0);
}
