// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/world_orbit_air.gdshaderinc"
#include "res://Shaders/surface_tiles.gdshaderinc"
uniform sampler2D globe_map : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_globe_map;
uniform bool draw_air = false;
uniform float prime_meridian_offset;
uniform vec4 globe_color : source_color = vec4(1.0);

void vertex() {
    air_surface = VERTEX;
    air_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 normal = normalize(air_surface);
    vec2 uv = UV + vec2(prime_meridian_offset, 0.0);
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    vec3 surface = has_globe_map ? textureGrad(globe_map, uv, dx, dy).rgb : globe_color.rgb;
    if (native_base_enabled || native_normal_enabled) {
        vec4 native_color, native_normal;
        bool color_found, normal_found;
        native_tile_samples(uv, dx, dy, native_color, native_normal, color_found, normal_found);
        if (color_found) surface = native_color.rgb;
        if (normal_found) normal = native_surface_normal(normal, native_normal);
    }
    surface = OUTPUT_IS_SRGB ? air_srgb_linear(surface) : surface;
    float sun = max(0.0, dot(normal, light_in_body));
    vec3 transmission = vec3(1.0);
    vec3 radiance = vec3(0.0);
    if (draw_air) {
        world_orbit_air(air_eye * ground_radius_km, normalize(air_surface - air_eye), transmission, radiance);
    }
    vec3 illumination = vec3(sun);
    if (draw_air && venus_scattering) {
        vec3 point = normalize(air_surface) * ground_radius_km;
        illumination = sun * exp(-venus_extinction(venus_columns(point, light_in_body))) + venus_diffuse(point, sun);
    }
    ALBEDO = air_display_color(surface * (0.013 + 0.94 * illumination), transmission, radiance);
}
