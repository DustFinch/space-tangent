// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec3 point_color = vec3(1.0);
uniform float magnitude = 30.0;
uniform float fade = 1.0;
varying float core_alpha;
varying float core_scale;
varying float halo;

void vertex() {
    float brightness = (7.8 - magnitude) * 0.19;
    float disc = brightness > 1.0 ? clamp(4.6 - magnitude, 2.0, 3.0) : 0.5;
    POINT_SIZE = 10.0 * disc;
    core_scale = 2.0 * disc;
    core_alpha = clamp(brightness, 0.0, 1.0) * fade;
    halo = max(0.0, disc - 2.0) * 0.25 * fade;
}

void fragment() {
    float r = length(POINT_COORD * 2.0 - 1.0);
    float core = core_alpha * exp(-8.0 * r * r * core_scale * core_scale);
    float falloff = 1.0 - smoothstep(0.0, 1.0, r);
    ALBEDO = point_color;
    ALPHA = clamp(core + halo * falloff * falloff, 0.0, 1.0);
}
