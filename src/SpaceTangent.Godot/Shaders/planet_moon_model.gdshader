shader_type spatial;
render_mode cull_back, diffuse_lambert;

uniform vec4 base_color : source_color = vec4(1.0);
uniform sampler2D diffuse_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_texture : filter_linear_mipmap, repeat_enable;
uniform bool has_diffuse;
uniform bool has_normal;
uniform bool packed_normal;

void fragment() {
    vec3 surface_color = base_color.rgb;
    if (has_diffuse) {
        surface_color *= texture(diffuse_texture, UV).rgb;
    }
    ALBEDO = surface_color;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
    if (has_normal) {
        vec4 encoded = texture(normal_texture, UV);
        vec2 xy = (packed_normal ? encoded.ag : encoded.rg) * 2.0 - 1.0;
        float z = sqrt(max(0.0, 1.0 - dot(xy, xy)));
        NORMAL_MAP = vec3(xy, z) * 0.5 + 0.5;
    }
}
