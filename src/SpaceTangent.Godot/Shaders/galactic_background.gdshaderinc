// SPDX-License-Identifier: GPL-2.0-or-later
uniform sampler2D galactic_image : filter_linear, repeat_enable;
uniform vec3 galactic_x = vec3(1.0, 0.0, 0.0);
uniform vec3 galactic_y = vec3(0.0, 1.0, 0.0);
uniform vec3 galactic_z = vec3(0.0, 0.0, 1.0);

vec3 galactic_radiance(vec3 direction) {
    vec3 local = vec3(dot(direction, galactic_x), dot(direction, galactic_y), dot(direction, galactic_z));
    float longitude = atan(local.y, local.x) / TAU;
    float latitude = asin(clamp(local.z, -1.0, 1.0)) / PI;
    float half_texel = 0.5 / float(textureSize(galactic_image, 0).y);
    vec2 uv = vec2(longitude + 0.5, clamp(0.5 - latitude, half_texel, 1.0 - half_texel));
    vec3 light = texture(galactic_image, uv).rgb;
    float luminance = dot(light, vec3(0.2126, 0.7152, 0.0722));
    return mix(vec3(luminance), light, 0.6) * 0.006;
}
