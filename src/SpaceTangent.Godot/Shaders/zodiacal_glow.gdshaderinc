// SPDX-License-Identifier: GPL-2.0-or-later
uniform bool show_zodiacal_light = false;
uniform sampler2D zodiacal_map : filter_linear, repeat_enable;
uniform float zodiacal_sun_longitude = 0.0;
uniform float zodiacal_strength = 0.0;

vec3 zodiacal_radiance(vec3 ray) {
    const float cos_obliquity = 0.9174820621;
    const float sin_obliquity = 0.3977771559;
    float longitude = atan(ray.y * cos_obliquity + ray.z * sin_obliquity, ray.x);
    float latitude = asin(clamp(ray.z * cos_obliquity - ray.y * sin_obliquity, -1.0, 1.0));
    vec2 uv = vec2(fract(0.5 - (longitude - zodiacal_sun_longitude) / TAU), clamp(0.5 - latitude / PI, 0.002, 0.998));
    return vec3(texture(zodiacal_map, uv).r * zodiacal_strength);
}
