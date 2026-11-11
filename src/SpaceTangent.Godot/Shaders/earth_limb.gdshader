// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never;

#include "res://Shaders/earth_atmosphere.gdshaderinc"
#include "res://Shaders/earth_orbit_geometry.gdshaderinc"

varying vec3 model_surface;
varying flat vec3 model_eye;

void vertex() {
    model_surface = VERTEX;
    model_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    float shell_radius = earth_radius_km + AIR_TOP_KM;
    vec3 eye = model_eye * shell_radius;
    vec3 ray = normalize(model_surface - model_eye);
    vec2 ground = earth_intersections(eye, ray, earth_radius_km);
    vec2 span = earth_intersections(eye, ray, shell_radius);
    float start = max(0.0, span.x);
    // Depth testing hides this pass behind the globe. Integrate to the ground at the
    // analytic limb to close subpixel gaps left by the tessellated surface.
    float end = ground.x >= 0.0 ? min(ground.x, span.y) : span.y;
    if (end <= start) {
        discard;
    }
    vec2 column = vec2(0.0);
    vec3 radiance = vec3(0.0);
    air_integrate(eye, ray, sun_direction, earth_radius_km, start, end, 32, column, radiance);
    vec3 color = 1.0 - exp(-2.3 * radiance);
    ALBEDO = OUTPUT_IS_SRGB ? air_linear_to_srgb(color) : color;
    ALPHA = 1.0;
}
