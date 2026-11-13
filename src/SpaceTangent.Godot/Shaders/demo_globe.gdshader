// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/demo_air.gdshaderinc"

uniform sampler2D albedo_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D cloud_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emission_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rings_map : filter_linear_mipmap, repeat_disable;
uniform vec3 fallback_color;
uniform bool has_maps;
uniform bool has_clouds;
uniform bool draw_air = true;
uniform bool has_emission;
uniform bool has_rings;
uniform float cloud_height;
uniform float ring_inner;
uniform float ring_outer;
uniform bool use_terrain;
uniform bool terrain_shadows = true;
uniform vec3 terrain_eye;
varying vec3 terrain_normal;
varying vec4 terrain_color;
varying vec4 terrain_horizon_east;
varying vec4 terrain_horizon_west;


vec2 surface_uv(vec3 n) {
    return vec2(0.5 + atan(n.z, -n.x) / TAU, 0.5 - asin(clamp(n.y, -1.0, 1.0)) / PI);
}

float ring_shade(vec3 point, vec3 light) {
    if (!has_rings || abs(light.y) < 0.0001) return 1.0;
    float travel = -point.y / light.y;
    float radial = length((point + light * travel).xz);
    return travel > 0.0 && radial > ring_inner && radial < ring_outer ?
        1.0 - texture(rings_map, vec2((radial - ring_inner) / (ring_outer - ring_inner), 0.5)).a : 1.0;
}

vec3 reflected(vec3 n, vec3 point, vec3 eye, vec3 light, vec3 irradiance, vec3 albedo, float water) {
    float lambert = max(dot(n, light), 0.0);
    float specular = water * pow(max(dot(reflect(-light, n), eye), 0.0), 72.0) * 0.55;
    return (lambert * albedo + specular) * irradiance * ring_shade(point, light);
}

vec3 decode_normal(vec2 encoded) {
    vec3 normal = vec3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0) {
        normal.xy = (1.0 - abs(normal.yx)) * mix(vec2(-1.0), vec2(1.0), greaterThanEqual(normal.xy, vec2(0.0)));
    }
    return normalize(normal);
}

vec4 unpack_horizon(vec4 packed, float parent) {
    vec4 fine = floor(packed / 256.0);
    vec4 coarse = packed - fine * 256.0;
    return mix(fine, coarse, parent) * (1.25 / 255.0) - 0.25;
}

float terrain_visibility(vec3 up, vec3 light) {
    if (!use_terrain || !terrain_shadows) return 1.0;
    vec3 east = cross(vec3(0.0, 1.0, 0.0), up);
    east = length(east) > 0.00001 ? normalize(east) : vec3(1.0, 0.0, 0.0);
    vec3 north = cross(up, east);
    float azimuth = mod(atan(dot(light, north), dot(light, east)) / (TAU / 8.0) + 8.0, 8.0);
    float angles[8] = float[8](terrain_horizon_east.x, terrain_horizon_east.y, terrain_horizon_east.z, terrain_horizon_east.w,
        terrain_horizon_west.x, terrain_horizon_west.y, terrain_horizon_west.z, terrain_horizon_west.w);
    int index = int(floor(azimuth));
    float horizon = mix(angles[index], angles[(index + 1) % 8], fract(azimuth));
    return smoothstep(horizon - 0.012, horizon + 0.012, dot(up, light));
}

void vertex() {
    terrain_normal = NORMAL;
    terrain_color = COLOR;
    float parent = 0.0;
    if (use_terrain && CUSTOM0.w > 0.0) {
        float distance = length(VERTEX + vec3(UV, UV2.x) - terrain_eye);
        float focal = 0.5 * VIEWPORT_SIZE.y * abs(PROJECTION_MATRIX[1][1]);
        float pixels = CUSTOM0.w * focal / max(distance, 1e-12);
        parent = 1.0 - smoothstep(5.0, 8.0, pixels);
        VERTEX += CUSTOM0.xyz * parent;
        terrain_normal = normalize(mix(NORMAL, decode_normal(CUSTOM1.xy), parent));
        float blue = floor(CUSTOM2.z / 256.0);
        float water = CUSTOM2.z - blue * 256.0;
        terrain_color = mix(COLOR, vec4(CUSTOM2.xy, blue / 255.0, water / 255.0), parent);
    }
    terrain_horizon_east = unpack_horizon(CUSTOM3, parent);
    terrain_horizon_west = unpack_horizon(vec4(UV2.y, CUSTOM1.w, CUSTOM2.w, CUSTOM1.z), parent);
    mat4 world_to_mesh = inverse(MODEL_MATRIX);
    air_surface = use_terrain ? VERTEX + vec3(UV, UV2.x) : VERTEX;
    air_eye = use_terrain ? terrain_eye : (world_to_mesh * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 n = normalize(air_surface);
    vec3 eye = normalize(air_eye - air_surface);
    vec2 uv = surface_uv(n);
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    dx.x -= round(dx.x);
    dy.x -= round(dy.x);
    vec4 material = has_maps ? textureGrad(albedo_map, uv, dx, dy) : vec4(fallback_color, 0.0);
    vec3 albedo = has_maps && OUTPUT_IS_SRGB ? air_srgb_linear(material.rgb) : material.rgb;
    vec3 detail = n;
    if (use_terrain) {
        material = terrain_color;
        albedo = material.rgb;
        detail = normalize(terrain_normal);
    } else if (has_maps) {
        vec3 east = normalize(vec3(n.z, 0.0, -n.x) + vec3(0.0, 0.0, 1e-7));
        vec3 south = cross(east, n);
        vec3 tangent = textureGrad(normal_map, uv, dx, dy).xyz * 2.0 - 1.0;
        detail = normalize(east * tangent.x + south * tangent.y + n * max(tangent.z, 0.1));
    }
    vec3 color = 0.008 * albedo;
    color += reflected(detail, n, eye, first_direction, first_irradiance, albedo, material.a) * terrain_visibility(n, first_direction);
    color += reflected(detail, n, eye, second_direction, second_irradiance, albedo, material.a) * terrain_visibility(n, second_direction);
    if (has_emission) {
        vec3 glow = textureGrad(emission_map, uv, dx, dy).rgb;
        color += (OUTPUT_IS_SRGB ? air_srgb_linear(glow) : glow) * (1.0 - 0.85 * max(dot(n, first_direction), 0.0));
    }
    if (has_clouds) {
        vec3 cloud_normal = normalize(n + cloud_height * eye / max(dot(n, eye), 0.12));
        float cover = textureGrad(cloud_map, surface_uv(cloud_normal), dx, dy).r;
        vec3 light = max(dot(cloud_normal, first_direction), 0.0) * first_irradiance +
            max(dot(cloud_normal, second_direction), 0.0) * second_irradiance;
        color = mix(color, 0.82 * light + vec3(0.008), cover * 0.9);
    }
    if (draw_air) {
        vec3 transmission, radiance;
        vec3 segment = air_surface - air_eye;
        demo_air(air_eye * demo_radius, normalize(segment), use_terrain ? length(segment) * demo_radius : 0.0,
            transmission, radiance);
        color = color * transmission + radiance;
    }
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(color) : color;
}
