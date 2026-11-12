shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

uniform sampler2D particle_texture : source_color, filter_linear, repeat_disable;
uniform bool textured;

void fragment() {
    vec2 point = UV * 2.0 - 1.0;
    float falloff = max(0.0, 1.0 - dot(point, point));
    vec4 spot = textured ? texture(particle_texture, UV) : vec4(vec3(1.0), falloff * falloff);
    ALBEDO = spot.rgb * COLOR.rgb;
    ALPHA = spot.a * COLOR.a;
}
