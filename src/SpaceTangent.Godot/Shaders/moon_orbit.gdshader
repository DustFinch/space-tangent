// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

#include "res://Shaders/solar_occultation.gdshaderinc"

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_surface_map;
uniform vec3 sun_from_moon;
uniform vec3 earth_from_moon;
uniform float earth_radius_km;
uniform float moon_radius_km;
varying vec3 globe_direction;

void vertex() {
    globe_direction = normalize(VERTEX);
}

void fragment() {
    vec3 normal = normalize(globe_direction);
    vec3 point = normal * moon_radius_km;
    vec3 toward_sun = sun_from_moon - point;
    float diffuse = max(0.0, dot(normal, normalize(toward_sun)));
    float visible = diffuse > 0.0 ? solar_visibility(toward_sun, earth_from_moon - point, earth_radius_km) : 1.0;
    vec3 albedo = has_surface_map ? texture(surface_map, UV).rgb : vec3(0.65, 0.61, 0.56);
    // Geometric direct light with a small floor for the visible surface in shadow.
    float intensity = 0.02 + 0.98 * diffuse * visible;
    ALBEDO = albedo * (OUTPUT_IS_SRGB ? pow(intensity, 1.0 / 2.2) : intensity);
}
