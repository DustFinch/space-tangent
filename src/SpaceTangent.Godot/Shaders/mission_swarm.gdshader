// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform sampler2D source_data : filter_nearest, repeat_disable;
uniform float swarm_clock;
uniform vec4 swarm_color = vec4(1.0);
uniform float point_size = 1.0;
varying vec4 point_color;
#include "res://Shaders/mission_swarm.gdshaderinc"

vec4 object_data(int at) {
    int width = textureSize(source_data, 0).x;
    return texelFetch(source_data, ivec2(at % width, at / width), 0);
}

void vertex() {
    int index = 3 * int(UV.x);
    vec4 orbit = object_data(index);
    vec4 rotation = object_data(index + 1);
    float age = swarm_clock - object_data(index + 2).x;
    VERTEX = swarm_position(orbit, rotation, swarm_clock);
    POINT_SIZE = point_size;
    point_color = swarm_appearance(age, swarm_color);
}

void fragment() {
    vec2 radius = POINT_COORD - vec2(0.5);
    float coverage = clamp(1.0 - dot(radius, radius) * 4.0, 0.0, 1.0);
    ALBEDO = OUTPUT_IS_SRGB ? point_color.rgb : pow(point_color.rgb, vec3(2.2));
    ALPHA = point_color.a * coverage;
}
