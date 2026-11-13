uniform vec4 base_color : source_color = vec4(1.0);
uniform sampler2D diffuse_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_texture : filter_linear_mipmap, repeat_enable;
uniform bool has_diffuse;
uniform bool has_normal;
uniform bool packed_normal;
uniform vec3 emission_color;
uniform vec3 specular_color;
uniform float specular_power;
uniform sampler2D emission_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_emission_texture;

void fragment() {
    vec3 surface_color = base_color.rgb;
    if (has_diffuse) {
        surface_color *= texture(diffuse_texture, UV).rgb;
    }
    ALBEDO = surface_color;
    vec3 glow = has_emission_texture ? texture(emission_texture, UV).rgb : vec3(1.0);
    if (OUTPUT_IS_SRGB) {
        glow = mix(glow / 12.92, pow((glow + 0.055) / 1.055, vec3(2.4)), greaterThan(glow, vec3(0.04045)));
    }
    EMISSION = glow * emission_color;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
    if (has_normal) {
        vec4 encoded = texture(normal_texture, UV);
        vec2 xy = (packed_normal ? encoded.ag : encoded.rg) * 2.0 - 1.0;
        float z = sqrt(max(0.0, 1.0 - dot(xy, xy)));
        NORMAL_MAP = vec3(xy, z) * 0.5 + 0.5;
    }
}

#ifdef USE_SPECULAR_HIGHLIGHT
void light() {
    float incidence = max(0.0, dot(NORMAL, LIGHT));
    DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * incidence / PI;
    if (specular_power > 0.0 && incidence > 0.0) {
        vec3 halfway = VIEW + LIGHT;
        float length_squared = dot(halfway, halfway);
        if (length_squared > 1e-8) {
            float highlight = max(0.0, dot(NORMAL, halfway * inversesqrt(length_squared)));
            SPECULAR_LIGHT += specular_color * LIGHT_COLOR * ATTENUATION * incidence * pow(highlight, specular_power) / PI;
        }
    }
}
#endif
