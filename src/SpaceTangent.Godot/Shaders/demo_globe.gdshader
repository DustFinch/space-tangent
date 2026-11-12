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
uniform bool has_emission;
uniform bool has_rings;
uniform float cloud_height;
uniform float ring_inner;
uniform float ring_outer;


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

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
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
    if (has_maps) {
        vec3 east = normalize(vec3(n.z, 0.0, -n.x) + vec3(0.0, 0.0, 1e-7));
        vec3 south = cross(east, n);
        vec3 tangent = textureGrad(normal_map, uv, dx, dy).xyz * 2.0 - 1.0;
        detail = normalize(east * tangent.x + south * tangent.y + n * max(tangent.z, 0.1));
    }
    vec3 color = 0.008 * albedo;
    color += reflected(detail, n, eye, first_direction, first_irradiance, albedo, material.a);
    color += reflected(detail, n, eye, second_direction, second_irradiance, albedo, material.a);
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
    vec3 transmission, radiance;
    demo_air(air_eye * demo_radius, normalize(air_surface - air_eye), transmission, radiance);
    color = color * transmission + radiance;
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_srgb(color) : color;
}
