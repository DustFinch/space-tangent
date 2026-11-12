// SPDX-License-Identifier: GPL-2.0-or-later

const float OCCULTATION_SUN_RADIUS_KM = 695700.0;

float solar_visibility(vec3 to_sun, vec3 to_occluder, float occluder_radius) {
    float sun_distance = length(to_sun);
    float foreground_distance = length(to_occluder);
    if (foreground_distance >= sun_distance) {
        return 1.0;
    }
    if (foreground_distance <= occluder_radius) {
        return 0.0;
    }
    vec3 sun = to_sun / sun_distance;
    vec3 foreground = to_occluder / foreground_distance;
    float separation = atan(length(cross(sun, foreground)), dot(sun, foreground));
    float r = asin(OCCULTATION_SUN_RADIUS_KM / sun_distance);
    float q = asin(occluder_radius / foreground_distance);
    if (separation >= r + q) {
        return 1.0;
    }
    if (separation <= abs(r - q)) {
        return q >= r ? 0.0 : 1.0 - q * q / (r * r);
    }
    float d2 = separation * separation;
    float r2 = r * r;
    float q2 = q * q;
    float sun_angle = acos(clamp((d2 + r2 - q2) / (2.0 * separation * r), -1.0, 1.0));
    float foreground_angle = acos(clamp((d2 + q2 - r2) / (2.0 * separation * q), -1.0, 1.0));
    float radical = (-separation + r + q) * (separation + r - q) * (separation - r + q) * (separation + r + q);
    float area = r2 * sun_angle + q2 * foreground_angle - 0.5 * sqrt(max(0.0, radical));
    return clamp(1.0 - area / (PI * r2), 0.0, 1.0);
}
