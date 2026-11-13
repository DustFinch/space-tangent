// SPDX-License-Identifier: GPL-2.0-or-later
uniform sampler2D galactic_image : filter_linear, repeat_enable;
uniform vec3 galactic_x = vec3(1.0, 0.0, 0.0);
uniform vec3 galactic_y = vec3(0.0, 1.0, 0.0);
uniform vec3 galactic_z = vec3(0.0, 0.0, 1.0);

// Combine pairs of cubic B-spline weights so the sampler can interpolate each pair in one fetch.
void galactic_spline_axis(float coordinate, out vec2 position, out vec2 weight) {
    float cell = floor(coordinate);
    float t = coordinate - cell;
    float s = 1.0 - t;
    vec4 w = vec4(s * s * s, (3.0 * t - 6.0) * t * t + 4.0,
        ((-3.0 * t + 3.0) * t + 3.0) * t + 1.0, t * t * t) / 6.0;
    weight = w.xz + w.yw;
    position = vec2(cell - 1.0, cell + 1.0) + w.yw / weight;
}

vec3 sample_galactic_map(vec2 uv) {
    vec2 size = vec2(textureSize(galactic_image, 0));
    vec2 pixel = uv * size - 0.5;
    vec2 x, y, wx, wy;
    galactic_spline_axis(pixel.x, x, wx);
    galactic_spline_axis(pixel.y, y, wy);
    x = (x + 0.5) / size.x;
    y = (clamp(y, vec2(0.0), vec2(size.y - 1.0)) + 0.5) / size.y;
    vec3 north = wx.x * texture(galactic_image, vec2(x.x, y.x)).rgb
        + wx.y * texture(galactic_image, vec2(x.y, y.x)).rgb;
    vec3 south = wx.x * texture(galactic_image, vec2(x.x, y.y)).rgb
        + wx.y * texture(galactic_image, vec2(x.y, y.y)).rgb;
    return wy.x * north + wy.y * south;
}

vec3 galactic_radiance(vec3 direction) {
    vec3 local = vec3(dot(direction, galactic_x), dot(direction, galactic_y), dot(direction, galactic_z));
    float longitude = atan(local.y, local.x) / TAU;
    float latitude = asin(clamp(local.z, -1.0, 1.0)) / PI;
    vec3 light = sample_galactic_map(vec2(longitude + 0.5, 0.5 - latitude));
    float luminance = dot(light, vec3(0.2126, 0.7152, 0.0722));
    return mix(vec3(luminance), light, 0.6) * 0.006;
}
