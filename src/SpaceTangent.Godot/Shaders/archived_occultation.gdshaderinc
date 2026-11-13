// SPDX-License-Identifier: GPL-2.0-or-later
uniform vec4 archived_blockers[4];
uniform int archived_shadow_count = 0;
uniform vec3 archived_sun;
uniform float archived_sun_radius;
uniform float archived_light_angle;
uniform float archived_second_angle;
float archived_disc_visibility(float separation, float star, float blocker) {
    if (star <= 0.0 || blocker <= 0.0) return 1.0;
    float d = separation / star;
    float r = blocker / star;
    if (d >= 1.0 + r) return 1.0;
    if (d <= abs(1.0 - r)) return 1.0 - min(1.0, r * r);
    float x = clamp((d * d + 1.0 - r * r) / (2.0 * d), -1.0, 1.0);
    float y = clamp((d * d + r * r - 1.0) / (2.0 * d * r), -1.0, 1.0);
    float lens = max(0.0, (1.0 - (d-r)*(d-r)) * ((d+r)*(d+r) - 1.0));
    return clamp(1.0 - (acos(x) + r*r*acos(y) - 0.5*sqrt(lens)) / PI, 0.0, 1.0);
}

float archived_light_visibility(vec3 receiver_point, vec3 direction, float angle, bool use_sun) {
    if (archived_shadow_count == 0 || dot(direction, direction) <= 0.0) return 1.0;
    vec3 light = normalize(direction);
    float limit = 1e30;
    if (use_sun && angle <= 0.0 && archived_sun_radius > 0.0) {
        vec3 offset = archived_sun - receiver_point;
        limit = length(offset);
        light = offset / max(limit, 1e-10);
        angle = asin(clamp(archived_sun_radius / max(limit, 1e-10), 0.0, 1.0));
    }
    float visible = 1.0;
    for (int i = 0; i < archived_shadow_count; i++) {
        vec4 blocker = archived_blockers[i];
        vec3 delta = blocker.xyz - receiver_point;
        float distance = length(delta);
        if (distance <= blocker.w || distance >= limit || dot(delta, light) <= 0.0) continue;
        vec3 axis = delta / distance;
        float separation = atan(length(cross(axis, light)), dot(axis, light));
        visible *= archived_disc_visibility(separation, angle > 0.0 ? angle : 0.0047, asin(clamp(blocker.w / distance, 0.0, 1.0)));
    }
    return visible;
}
