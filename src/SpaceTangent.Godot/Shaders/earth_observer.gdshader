// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D cloud_texture : filter_linear_mipmap, repeat_enable;
uniform sampler2D moon_texture : source_color, filter_linear_mipmap;
uniform vec3 observer;
uniform vec3 forward;
uniform vec3 right;
uniform vec3 camera_up;
uniform vec3 earth_to_sun;
uniform vec3 earth_to_moon;
uniform float earth_angle;
uniform float earth_radius;
uniform float moon_radius;
uniform float lunar_strength;
uniform bool show_clouds = true;
uniform bool lunar_cloud_light = true;

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
    // The established observer preset displays the Moon at four times its physical angular size.
    float radius = min(4.0 * moon_radius / moon_distance, 0.1);
    if (dot(ray, moon_dir) <= 0.0 || length(cross(ray, moon_dir)) >= radius) {
        return background;
    }

    vec3 east = normalize(cross(vec3(0.0, 0.0, 1.0), moon_dir));
    vec3 north = normalize(cross(moon_dir, east));
    float x = dot(ray, east) / radius;
    float y = dot(ray, north) / radius;
    float depth = sqrt(max(0.0, 1.0 - x * x - y * y));
    vec3 normal = x * east + y * north - depth * moon_dir;
    float diffuse = max(0.0, dot(normal, normalize(earth_to_sun - earth_to_moon)));
    vec2 uv = vec2(0.5 + atan(x, depth) / TAU, 0.5 - asin(clamp(y, -1.0, 1.0)) / PI);
    vec3 lunar = texture(moon_texture, uv).rgb;
    return pow(max(lunar, vec3(0.0)), vec3(0.58)) * (0.035 + 1.35 * diffuse);
}

void fragment() {
    vec2 screen = vec2(2.0 * UV.x - 1.0, 1.0 - 2.0 * UV.y);
    screen.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    const float TAN_HALF_FIELD = 0.087488664;
    vec3 ray = normalize(forward + TAN_HALF_FIELD * (screen.x * right + screen.y * camera_up));
    vec3 site_up = normalize(observer);
    vec3 sun_dir = normalize(earth_to_sun - observer);
    vec3 moon_delta = earth_to_moon - observer;
    vec3 moon_dir = normalize(moon_delta);
    float solar_elevation = dot(site_up, sun_dir);
    float sky_elevation = clamp(dot(ray, site_up), 0.0, 1.0);
    float day = smoothstep(-0.105, 0.08, solar_elevation);
    vec3 day_sky = mix(vec3(0.49, 0.67, 0.87), vec3(0.09, 0.28, 0.61), sky_elevation);
    vec3 color = mix(vec3(0.012, 0.018, 0.035), day_sky, day);

    if (dot(moon_dir, site_up) > 0.0) {
        color = lunar_disc(ray, moon_dir, length(moon_delta), color);
    }

    if (show_clouds && dot(ray, site_up) > 0.0) {
        float shell = earth_radius + 8.0;
        float b = dot(observer, ray);
        float c = dot(observer, observer) - shell * shell;
        vec3 cloud = observer + ray * (-b + sqrt(max(0.0, b * b - c)));
        vec3 normal = normalize(cloud);
        float longitude = atan(normal.y, normal.x) - earth_angle;
        float latitude = asin(clamp(normal.z, -1.0, 1.0));
        float regional_cover = texture(cloud_texture, vec2(fract(0.5 + longitude / TAU), 0.5 - latitude / PI)).r;
        float coverage = cloud_cover(cloud, regional_cover);
        float solar_incidence = dot(normal, sun_dir);
        float night = 1.0 - smoothstep(-0.105, 0.0, solar_incidence);
        float sunlight = max(0.0, solar_incidence);
        float moonlight = 0.0;
        if (lunar_cloud_light) {
            moonlight = lunar_strength * night * max(0.0, dot(normal, normalize(earth_to_moon - cloud)));
        }
        float cloud_value = (0.045 + 0.88 * sunlight + 1.65 * moonlight) * (1.0 - 0.40 * coverage);
        color = mix(color, vec3(cloud_value), coverage);
    }

    COLOR = vec4(color, 1.0);
}
