// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://Shaders/model_emission.gdshaderinc"

void fragment() {
    vec4 base = base_color * texture(albedo_map, UV);
    ALBEDO = add_model_emission(base.rgb, texture(emission_map, UV).rgb);
    ALPHA = base.a;
}
