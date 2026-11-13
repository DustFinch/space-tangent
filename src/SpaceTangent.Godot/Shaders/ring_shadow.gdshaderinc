// SPDX-License-Identifier: GPL-2.0-or-later
uniform sampler2D rings_map : filter_linear_mipmap, repeat_disable;
uniform bool cast_ring_shadows;
uniform float ring_inner;
uniform float ring_outer;
uniform vec3 sun_in_body;

// Position is measured in equatorial radii with the rings in the XZ plane.
float ring_visibility(vec3 point, bool integrate) {
    if (!cast_ring_shadows || abs(sun_in_body.y) <= 0.00001) return 1.0;
    float along = -point.y / sun_in_body.y;
    if (along <= 0.0) return 1.0;
    float radius = length(point.xz + sun_in_body.xz * along);
    if (radius < ring_inner || radius > ring_outer) return 1.0;
    float u = (radius - ring_inner) / (ring_outer - ring_inner);
    vec2 uv = vec2(u, 0.5);
    float opacity = integrate ? textureLod(rings_map, uv, 0.0).a : texture(rings_map, uv).a;
    return 1.0 - 0.9 * opacity;
}
