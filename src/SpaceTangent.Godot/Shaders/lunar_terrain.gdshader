// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D heights : filter_linear, repeat_enable;
uniform float radius_km = 1737.4;
uniform bool regional = false;
uniform vec3 site_up;
uniform vec3 site_east;
uniform vec3 site_north;
uniform float patch_span;
uniform vec3 sun_direction;

varying vec3 terrain_normal;

float elevation_km(vec2 uv, float half_row) {
    uv.y = clamp(uv.y, half_row, 1.0 - half_row);
    return 0.001 * textureLod(heights, uv, 0.0).r;
}

void vertex() {
    vec3 radial = normalize(VERTEX);
    if (regional) {
        radial = normalize(site_up + patch_span * (VERTEX.x * site_east - VERTEX.z * site_north));
    }
    vec2 step_uv = 1.0 / vec2(textureSize(heights, 0));
    float half_row = 0.5 * step_uv.y;
    float longitude = atan(-radial.z, radial.x);
    float latitude = asin(clamp(radial.y, -1.0, 1.0));
    vec2 uv = vec2(0.5 + longitude / TAU, 0.5 - latitude / PI);
    float height = elevation_km(uv, half_row);
    vec3 east = vec3(-sin(longitude), 0.0, -cos(longitude));
    vec3 north = cross(radial, east);
    vec2 x_step = vec2(step_uv.x, 0.0);
    vec2 y_step = vec2(0.0, step_uv.y);
    float longitude_distance = 2.0 * radius_km * TAU * step_uv.x * max(cos(latitude), 0.02);
    float latitude_distance = 2.0 * radius_km * PI * step_uv.y;
    float east_slope = (elevation_km(uv + x_step, half_row) - elevation_km(uv - x_step, half_row)) / longitude_distance;
    float north_slope = (elevation_km(uv - y_step, half_row) - elevation_km(uv + y_step, half_row)) / latitude_distance;
    terrain_normal = normalize(radial - east_slope * east - north_slope * north);
    VERTEX = radial * (radius_km + height);
}

void fragment() {
    float illumination = 0.055 + 0.945 * max(dot(normalize(terrain_normal), sun_direction), 0.0);
    ALBEDO = vec3(0.59, 0.57, 0.54) * illumination;
}
