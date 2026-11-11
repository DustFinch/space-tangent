// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/earth_atmosphere.gdshaderinc"
#include "res://Shaders/earth_orbit_geometry.gdshaderinc"

uniform sampler2D day_map : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D night_map : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D cloud_map : filter_linear_mipmap, repeat_enable;
uniform bool has_night_map = false;
uniform bool has_cloud_map = false;
uniform bool show_atmosphere = true;
uniform float earth_angle;
uniform float lunar_strength;
uniform vec3 moon_direction;
varying vec3 model_surface;
varying flat vec3 model_eye;

void vertex() {
    model_surface = VERTEX;
    model_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

vec2 globe_uv(vec3 normal) {
    float longitude = atan(-normal.z, normal.x) - earth_angle;
    float latitude = asin(clamp(normal.y, -1.0, 1.0));
    return vec2(fract(0.5 + longitude / TAU), clamp(0.5 - latitude / PI, 0.0001, 0.9999));
}

void fragment() {
    vec3 eye = model_eye * earth_radius_km;
    vec3 surface = normalize(model_surface) * earth_radius_km;
    vec3 ray = normalize(model_surface - model_eye);
    vec3 normal = normalize(surface);
    vec2 coordinates = globe_uv(normal);
    float direct = max(0.0, dot(normal, sun_direction));
    float reflected = max(0.0, dot(normal, moon_direction)) * lunar_strength;
    vec3 color = texture(day_map, coordinates).rgb;
    if (OUTPUT_IS_SRGB) {
        color = air_srgb_to_linear(color);
    }
    color *= 0.012 + 0.98 * direct + 0.075 * reflected;
    if (has_night_map) {
        float city = texture(night_map, coordinates).r;
        city = OUTPUT_IS_SRGB ? air_srgb_to_linear(vec3(city)).r : city;
        color += vec3(1.0, 0.69, 0.35) * (1.1 * city * (1.0 - smoothstep(0.0, 0.16, direct)));
    }

    float end = max(0.0, dot(surface - eye, ray));
    vec2 column = vec2(0.0);
    vec3 radiance = vec3(0.0);
    vec2 air_path = earth_intersections(eye, ray, earth_radius_km + AIR_TOP_KM);
    float start = max(0.0, air_path.x);
    if (has_cloud_map) {
        vec2 cloud_path = earth_intersections(eye, ray, earth_radius_km + 8.0);
        float cloud_distance = cloud_path.x;
        if (cloud_distance >= 0.0 && cloud_distance < end) {
            vec3 cloud = normalize(eye + ray * cloud_distance);
            float coverage = texture(cloud_map, globe_uv(cloud)).r;
            float daylight = max(0.0, dot(cloud, sun_direction));
            float moonlight = max(0.0, dot(cloud, moon_direction)) * lunar_strength;
            float brightness = 0.016 + 0.82 * daylight + 0.37 * moonlight;
            color = mix(color, vec3(brightness), smoothstep(0.06, 0.93, coverage) * 0.88);
        }
    }
    if (show_atmosphere && end > start) {
        air_integrate(eye, ray, sun_direction, earth_radius_km, start, end, 24, column, radiance);
        color = color * exp(-air_extinction(column)) + (1.0 - exp(-2.3 * radiance));
    }
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_to_srgb(color) : color;
}
