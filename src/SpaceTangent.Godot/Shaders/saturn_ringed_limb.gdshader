// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never;

#include "res://Shaders/ring_shadow.gdshaderinc"

uniform float radius_km;
uniform float polar_ratio;
uniform float shell_ratio;
varying vec3 shell_point;
varying flat vec3 shell_eye;

void vertex() {
    shell_point = VERTEX;
    shell_eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 eye = shell_eye * shell_ratio;
    vec3 ray = normalize(shell_point - shell_eye);
    float along = dot(eye, ray);
    vec3 closest = eye - ray * along;
    float chord = dot(closest, closest);
    float outer = shell_ratio * shell_ratio - chord;
    if (outer <= 0.0) discard;
    float start = max(0.0, -along - sqrt(outer));
    float end = -along + sqrt(outer);
    float inner = 1.0 - chord;
    if (inner > 0.0 && -along - sqrt(inner) >= 0.0) end = min(end, -along - sqrt(inner));
    if (end <= start) discard;

    // Saturn's archived scattering coefficients and 34 km scale height.
    const vec3 rayleigh = vec3(0.00053, 0.00126, 0.00318);
    const vec3 absorption = vec3(0.00027, 0.00003, 0.0);
    const float mie = 0.001;
    const float g = 0.6;
    const float scale_height = 34.0;
    vec3 shape = vec3(1.0, polar_ratio, 1.0);
    vec3 sun = normalize(sun_in_body / shape);
    float cosine = dot(normalize(ray * shape), sun_in_body);
    float phase_r = 3.0 * (1.0 + cosine * cosine) / (16.0 * PI);
    float phase_m = (1.0 - g * g) / (4.0 * PI * pow(1.0 + g * g - 2.0 * g * cosine, 1.5));
    vec3 extinction = rayleigh + absorption + vec3(mie);
    vec3 radiance = vec3(0.0);
    float column = 0.0;
    float step_distance = (end - start) / 24.0;
    float step_km = step_distance * length(ray * shape) * radius_km;
    for (int i = 0; i < 24; i++) {
        vec3 point = eye + ray * (start + (float(i) + 0.5) * step_distance);
        float r = length(point);
        float altitude = max(0.0, r - 1.0) * radius_km;
        float density = exp(-altitude / scale_height);
        float mu = dot(point / r, sun);
        float horizon = -sqrt(max(0.0, 1.0 - 1.0 / (r * r)));
        float sunlight = smoothstep(horizon - 0.0005, horizon + 0.0005, mu);
        if (sunlight > 0.0) {
            float grazing = sqrt(0.5 * PI * radius_km / scale_height);
            float solar_column = density * scale_height * grazing / (1.0 + (grazing - 1.0) * abs(mu));
            if (mu < 0.0) {
                float tangent = r * sqrt(max(0.0, 1.0 - mu * mu));
                float tangent_density = exp(-max(0.0, tangent - 1.0) * radius_km / scale_height);
                solar_column = max(0.0, 2.0 * tangent_density * scale_height * grazing - solar_column);
            }
            radiance += exp(-extinction * (column + 0.5 * density * step_km + solar_column)) *
                (rayleigh * phase_r + vec3(mie * phase_m)) * density * step_km *
                sunlight * ring_visibility(point * shape, true);
        }
        column += density * step_km;
    }
    vec3 color = 1.0 - exp(-2.3 * radiance);
    ALBEDO = OUTPUT_IS_SRGB ? mix(12.92 * color, 1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055,
        greaterThan(color, vec3(0.0031308))) : color;
    ALPHA = 1.0;
}
