// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D rings_map : filter_linear_mipmap, repeat_disable;
uniform bool has_surface_map;
uniform bool cast_ring_shadows;
uniform float ring_inner;
uniform float ring_outer;
uniform float polar_ratio = 0.902;
uniform vec3 sun_in_body;
varying vec3 globe_direction;

void vertex() {
    globe_direction = normalize(VERTEX);
}

void fragment() {
    vec3 unit = normalize(globe_direction);
    vec3 point = unit * vec3(1.0, polar_ratio, 1.0);
    vec3 normal = normalize(unit / vec3(1.0, polar_ratio, 1.0));
    float incidence = max(dot(normal, sun_in_body), 0.0);
    float visibility = 1.0;
    if (cast_ring_shadows && incidence > 0.0 && abs(sun_in_body.y) > 0.00001) {
        float along = -point.y / sun_in_body.y;
        float radius = length(point.xz + sun_in_body.xz * along);
        if (along > 0.0 && radius >= ring_inner && radius <= ring_outer) {
            float u = (radius - ring_inner) / (ring_outer - ring_inner);
            visibility = 1.0 - 0.9 * texture(rings_map, vec2(u, 0.5)).a;
        }
    }
    vec3 color = has_surface_map ? texture(surface_map, UV).rgb : vec3(0.82, 0.72, 0.58);
    float intensity = 0.035 + 0.965 * incidence * visibility;
    ALBEDO = color * (OUTPUT_IS_SRGB ? pow(intensity, 1.0 / 2.2) : intensity);
}
