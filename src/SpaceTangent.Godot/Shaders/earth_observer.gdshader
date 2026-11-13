// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

#include "res://Shaders/earth_atmosphere.gdshaderinc"
#include "res://Shaders/galactic_background.gdshaderinc"

uniform bool show_galactic_sky = false;
uniform bool show_survey = false;
uniform sampler2D survey_view : filter_linear, repeat_disable;
uniform float survey_brightness = 1.0;

uniform sampler2D cloud_texture : filter_linear_mipmap, repeat_enable;
uniform sampler2D moon_texture : source_color, filter_linear_mipmap;
uniform vec3 observer;
uniform float tan_half_field = 0.087488664;
uniform vec3 forward;
uniform vec3 right;
uniform vec3 camera_up;
uniform vec3 earth_to_sun;
uniform vec3 earth_to_moon;
uniform vec3 moon_seen;
uniform float sky_glow = 0.0;
uniform float earth_angle;
uniform float earth_radius;
uniform float moon_radius;
uniform float moon_disk_scale = 4.0;
uniform float lunar_strength;
uniform vec3 site_north;
uniform vec3 site_east;
uniform bool show_horizon_grid = false;
uniform bool show_clouds = true;
uniform bool lunar_cloud_light = true;
uniform int satellite_flare_count = 0;
uniform vec4 satellite_flares[8];

float cloud_hash(vec3 lattice) {
    lattice = fract(lattice * 0.3183099 + vec3(0.13, 0.27, 0.41));
    return fract(17.0 * lattice.x * lattice.y * lattice.z * (lattice.x + lattice.y + lattice.z));
}

float cloud_noise(vec3 position) {
    vec3 corner = floor(position);
    vec3 weight = fract(position);
    weight *= weight * (3.0 - 2.0 * weight);
    float first = mix(
        mix(cloud_hash(corner), cloud_hash(corner + vec3(1.0, 0.0, 0.0)), weight.x),
        mix(cloud_hash(corner + vec3(0.0, 1.0, 0.0)), cloud_hash(corner + vec3(1.0, 1.0, 0.0)), weight.x),
        weight.y);
    float second = mix(
        mix(cloud_hash(corner + vec3(0.0, 0.0, 1.0)), cloud_hash(corner + vec3(1.0, 0.0, 1.0)), weight.x),
        mix(cloud_hash(corner + vec3(0.0, 1.0, 1.0)), cloud_hash(corner + vec3(1.0, 1.0, 1.0)), weight.x),
        weight.y);
    return mix(first, second, weight.z);
}

float cloud_cover(vec3 cloud, float regional_cover) {
    // The map provides regional coverage. A ground observer resolves local cloud detail.
    float c = cos(earth_angle);
    float s = sin(earth_angle);
    vec3 local = vec3(c * cloud.x + s * cloud.y, -s * cloud.x + c * cloud.y, cloud.z);
    float detail = 0.55 * cloud_noise(local / 2.8) + 0.30 * cloud_noise(local / 1.1) +
        0.15 * cloud_noise(local / 0.4);
    float shape = smoothstep(0.33, 0.66, detail + (regional_cover - 0.5) * 0.60);
    return clamp(regional_cover * (0.35 + 1.35 * shape), 0.0, 1.0);
}

vec3 lunar_disc(vec3 ray, vec3 moon_dir, float moon_distance, vec3 background) {
    float radius = min(moon_disk_scale * moon_radius / moon_distance, 0.1);
    if (dot(ray, moon_seen) <= 0.0 || length(cross(ray, moon_seen)) >= radius) {
        return background;
    }

    vec3 east = normalize(cross(vec3(0.0, 0.0, 1.0), moon_dir));
    vec3 north = normalize(cross(moon_dir, east));
    vec3 seen_east = normalize(cross(vec3(0.0, 0.0, 1.0), moon_seen));
    vec3 seen_north = normalize(cross(moon_seen, seen_east));
    float x = dot(ray, seen_east) / radius;
    float y = dot(ray, seen_north) / radius;
    float depth = sqrt(max(0.0, 1.0 - x * x - y * y));
    vec3 normal = x * east + y * north - depth * moon_dir;
    vec3 lunar_point = earth_to_moon + normal * moon_radius;
    vec3 sun_from_moon = earth_to_sun - lunar_point;
    float diffuse = max(0.0, dot(normal, normalize(sun_from_moon))) * solar_visibility(sun_from_moon, -lunar_point, earth_radius);
    vec2 uv = vec2(0.5 + atan(x, depth) / TAU, 0.5 - asin(clamp(y, -1.0, 1.0)) / PI);
    vec3 lunar = texture(moon_texture, uv).rgb;
    return pow(max(lunar, vec3(0.0)), vec3(0.58)) * (0.035 + 1.35 * diffuse);
}

float grid_line(float coordinate) {
    float distance = abs(fract(coordinate + 0.5) - 0.5);
    return 1.0 - smoothstep(0.0, max(fwidth(coordinate), 0.001), distance);
}

void fragment() {
    vec2 screen = vec2(2.0 * UV.x - 1.0, 1.0 - 2.0 * UV.y);
    screen.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec3 ray = normalize(forward + tan_half_field * (screen.x * right + screen.y * camera_up));
    vec3 site_up = normalize(observer);
    vec3 sun_dir = normalize(earth_to_sun - observer);
    vec3 moon_delta = earth_to_moon - observer;
    vec3 moon_dir = normalize(moon_delta);
    float eye_projection = dot(observer, ray);
    float eye_radius = length(observer);
    float ground_offset = max(0.0, eye_radius - earth_radius) * (eye_radius + earth_radius);
    float ground_discriminant = eye_projection * eye_projection - ground_offset;
    bool ground = eye_projection < 0.0 && ground_discriminant >= 0.0;
    vec3 background = ground ? vec3(0.010, 0.009, 0.008) : vec3(0.012, 0.018, 0.035);

    if (!ground && show_galactic_sky) {
        float dark = 1.0 - smoothstep(-0.208, -0.087, dot(sun_dir, site_up));
        float contrast = dark / (1.0 + 64.0 * sky_glow);
        background += galactic_radiance(ray) * contrast;
    }
    if (!ground && show_survey) {
        float dark = 1.0 - smoothstep(-0.208, -0.087, dot(sun_dir, site_up));
        background += texture(survey_view, UV).rgb * (survey_brightness * dark / (1.0 + 64.0 * sky_glow));
    }
    if (!ground && dot(moon_seen, site_up) > 0.0) {
        background = lunar_disc(ray, moon_dir, length(moon_delta), background);
    }

    if (!ground && satellite_flare_count > 0) {
        float width = max(length(dFdx(ray)), length(dFdy(ray)));
        for (int index = 0; index < satellite_flare_count; index++) {
            vec4 flare = satellite_flares[index];
            float radius = length(ray - flare.xyz) / max(width, 0.000001);
            background += vec3(flare.w * exp(-0.35 * radius * radius));
        }
    }

    vec2 view_column = vec2(0.0);
    vec3 air_radiance = vec3(0.0);
    vec3 cloud_radiance = vec3(0.0);
    float cloud_distance = 0.0;
    float coverage = 0.0;
    if (show_clouds && eye_radius < earth_radius + 8.0 && dot(ray, site_up) > 0.0) {
        cloud_distance = air_exit(observer, ray, earth_radius + 8.0);
        vec3 cloud = observer + ray * cloud_distance;
        vec3 normal = normalize(cloud);
        float longitude = atan(normal.y, normal.x) - earth_angle;
        float latitude = asin(clamp(normal.z, -1.0, 1.0));
        float regional_cover = texture(cloud_texture, vec2(fract(0.5 + longitude / TAU), 0.5 - latitude / PI)).r;
        coverage = cloud_cover(cloud, regional_cover);
        float solar_incidence = dot(normal, sun_dir);
        float night = 1.0 - smoothstep(-0.105, 0.0, solar_incidence);
        float sunlight = max(0.0, solar_incidence) * earth_solar_visibility(cloud);
        float moonlight = 0.0;
        if (lunar_cloud_light) {
            moonlight = lunar_strength * night * max(0.0, dot(normal, normalize(earth_to_moon - cloud)));
        }
        float cloud_value = (0.045 + 0.88 * sunlight + 1.65 * moonlight) * (1.0 - 0.40 * coverage);
        if (coverage > 0.0) {
            air_integrate(observer, ray, sun_dir, earth_radius, 0.0, cloud_distance, 12, view_column, air_radiance);
            cloud_radiance = air_compose(vec3(cloud_value), view_column, air_radiance);
        }
    }

    float sky_distance = air_exit(observer, ray, earth_radius + AIR_TOP_KM);
    if (ground) {
        sky_distance = ground_offset / (-eye_projection + sqrt(ground_discriminant));
    }
    float start = coverage > 0.0 ? cloud_distance : 0.0;
    air_integrate(observer, ray, sun_dir, earth_radius, start, sky_distance, 20, view_column, air_radiance);
    vec3 sky = air_compose(background, view_column, air_radiance);
    vec3 composite = mix(sky, cloud_radiance, coverage);
    if (!ground && sky_glow > 0.0) {
        float night = 1.0 - smoothstep(-0.208, -0.087, dot(sun_dir, site_up));
        float horizon = 1.0 + 1.1 * pow(1.0 - max(0.0, dot(ray, site_up)), 2.0);
        composite += sky_glow * night * horizon * vec3(1.0, 0.58, 0.28);
    }
    vec3 color = air_linear_to_srgb(composite);
    if (show_horizon_grid) {
        float altitude = asin(clamp(dot(ray, site_up), -1.0, 1.0));
        float azimuth = atan(dot(ray, site_east), dot(ray, site_north));
        float lines = max(grid_line(altitude / radians(10.0)), grid_line(azimuth / radians(15.0)));
        color = mix(color, vec3(0.40, 0.66, 0.83), 0.50 * lines);
    }
    COLOR = vec4(color, 1.0);
}
