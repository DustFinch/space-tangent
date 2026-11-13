// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://Shaders/archived_model.gdshaderinc"

void fragment() {
    vec4 texel = has_map ? texture(surface_map, UV) : vec4(1.0);
    if (texel.a * tint.a < alpha_cutoff) {
        discard;
    }
    ALBEDO = archived_model_color(texel, normalize(NORMAL), TANGENT, BINORMAL, UV, VIEW);
}
