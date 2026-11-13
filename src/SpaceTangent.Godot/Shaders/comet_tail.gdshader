// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never, shadows_disabled;

uniform vec3 tint = vec3(0.85, 0.90, 1.0);
uniform float brightness = 0.5;
uniform bool radial = false;
uniform bool recorded_profile = false;

void fragment() {
    float across = 2.0 * UV.x - 1.0;
    float intensity;
    if (radial) {
        vec2 radius = 2.0 * UV - vec2(1.0);
        float r2 = dot(radius, radius);
        intensity = 1.5 * exp(-5.0 * r2) * (1.0 - smoothstep(0.75, 1.0, r2));
    } else {
        float along = recorded_profile ? UV.y : UV.y * UV.y;
        float edge = max(0.0, 1.0 - across * across);
        float fade = pow(max(0.0, 1.0 - along), 1.5) * smoothstep(0.0, 0.02, along);
        intensity = fade * edge * edge;
    }
    ALBEDO = tint;
    ALPHA = brightness * intensity;
}
