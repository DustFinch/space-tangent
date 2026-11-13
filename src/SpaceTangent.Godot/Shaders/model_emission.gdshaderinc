// SPDX-License-Identifier: GPL-2.0-or-later
uniform vec4 base_color : source_color = vec4(1.0);
uniform sampler2D albedo_map : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform sampler2D emission_map : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;
uniform vec3 emission_factor;

vec3 emission_to_linear(vec3 value) {
    return mix(value / 12.92, pow((value + 0.055) / 1.055, vec3(2.4)), greaterThan(value, vec3(0.04045)));
}

vec3 emission_to_display(vec3 value) {
    return mix(12.92 * value, 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055, greaterThan(value, vec3(0.0031308)));
}

vec3 add_model_emission(vec3 base, vec3 glow) {
    if (OUTPUT_IS_SRGB) {
        return emission_to_display(emission_to_linear(base) + emission_to_linear(glow) * emission_factor);
    }
    return base + glow * emission_factor;
}
