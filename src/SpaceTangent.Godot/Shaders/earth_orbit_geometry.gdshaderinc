// SPDX-License-Identifier: GPL-2.0-or-later

uniform float earth_radius_km;
uniform vec3 sun_direction;

vec2 earth_intersections(vec3 origin, vec3 ray, float radius) {
    float along = dot(origin, ray);
    vec3 nearest = origin - along * ray;
    float remaining = radius * radius - dot(nearest, nearest);
    if (remaining < 0.0) {
        return vec2(-1.0);
    }
    float chord = sqrt(remaining);
    return vec2(-along - chord, -along + chord);
}
