// SPDX-License-Identifier: GPL-2.0-or-later
vec3 swarm_position(vec4 orbit, vec4 rotation, float clock) {
    float mean = orbit.z + clock * orbit.w;
    float eccentric = mean;
    for (int i = 0; i < 4; i++) {
        eccentric = mean + orbit.y * sin(eccentric);
    }
    vec3 planar = vec3(orbit.x * (cos(eccentric) - orbit.y),
        orbit.x * sin(eccentric) * sqrt(1.0 - orbit.y * orbit.y), 0.0);
    vec3 q = rotation.yzw;
    vec3 cross_point = cross(q, planar) + rotation.x * planar;
    return cross(cross_point, -q) + dot(q, planar) * q + rotation.x * cross_point;
}

vec4 swarm_appearance(float age, vec4 color) {
    return age < 0.0 ? vec4(0.0) : mix(vec4(1.0), color, min(age / (86400.0 * 50.0), 1.0));
}
