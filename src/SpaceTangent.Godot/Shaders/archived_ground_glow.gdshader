// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_front, shadows_disabled;

#include "res://Shaders/earth_atmosphere.gdshaderinc"

uniform vec3 zenith = vec3(0.0, 1.0, 0.0);
uniform float zenith_glow;
uniform float darkness;
varying vec3 sky_direction;

void vertex() {
    sky_direction = VERTEX;
    POSITION = PROJECTION_MATRIX * vec4(VERTEX, 0.0);
    POSITION.z = POSITION.w * CLIP_SPACE_FAR;
}

void fragment() {
    float elevation = dot(normalize(sky_direction), zenith);
    if (elevation < -0.05) {
        discard;
    }
    float low = 1.0 - clamp(elevation, 0.0, 1.0);
    float glow = zenith_glow * (1.0 + 3.0 * pow(low, 4.0) + 4.0 * pow(low, 16.0));
    vec3 color = vec3(1.0, 0.78, 0.55) * glow * darkness;
    // In Compatibility the blend happens in display space.
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_to_srgb(color) : color;
    ALPHA = 1.0;
}
