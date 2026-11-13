// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://Shaders/model_emission.gdshaderinc"

uniform float alpha_cutoff = -1.0;

void fragment() {
    vec4 base = base_color * texture(albedo_map, UV);
    if (alpha_cutoff >= 0.0 && base.a < alpha_cutoff) {
        discard;
    }
    ALBEDO = add_model_emission(base.rgb, texture(emission_map, UV).rgb);
}
