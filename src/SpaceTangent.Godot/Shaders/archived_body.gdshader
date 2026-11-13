// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_map;
uniform vec4 tint : source_color = vec4(1.0);
uniform bool emissive;
uniform vec3 camera_light;

void fragment() {
    vec3 color = has_map ? texture(surface_map, UV).rgb : tint.rgb;
    float light = length(camera_light) > 0.0 ? max(0.0, dot(normalize(NORMAL), normalize(camera_light))) : 0.0;
    float illumination = emissive ? 1.0 : 0.04 + 0.96 * light;
    ALBEDO = color * (OUTPUT_IS_SRGB ? pow(illumination, 1.0 / 2.2) : illumination);
}
