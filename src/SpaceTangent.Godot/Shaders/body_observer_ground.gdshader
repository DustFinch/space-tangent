// SPDX-License-Identifier: GPL-2.0-or-later
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D surface_map : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_map = false;
uniform vec3 surface_color : source_color = vec3(0.35);
uniform vec3 eye;
uniform float eye_metric;
uniform float polar_ratio = 1.0;
uniform vec3 camera_forward;
uniform vec3 camera_right;
uniform vec3 camera_up;
uniform vec3 sun_direction;
uniform float tangent_half_field;
uniform float aspect_ratio;

void fragment() {
    vec2 screen = vec2((UV.x * 2.0 - 1.0) * aspect_ratio, 1.0 - UV.y * 2.0);
    vec3 ray = normalize(camera_forward + tangent_half_field * (camera_right * screen.x + camera_up * screen.y));
    vec3 ellipsoid_eye = eye * vec3(1.0, 1.0, polar_ratio);
    vec3 ellipsoid_ray = ray * vec3(1.0, 1.0, polar_ratio);
    float a = dot(ellipsoid_ray, ellipsoid_ray);
    float b = dot(ellipsoid_eye, ellipsoid_ray);
    // eye_metric is computed in double precision on the CPU. Subtracting 1.0 here
    // would erase the few metres separating the observer from a large body's surface.
    float discriminant = b * b - a * eye_metric;
    vec4 result = vec4(0.0);
    if (b < 0.0 && discriminant >= 0.0) {
        float near_distance = eye_metric / (-b + sqrt(discriminant));
        vec3 point = eye + near_distance * ray;
        vec3 normal = normalize(point * vec3(1.0, 1.0, polar_ratio * polar_ratio));
        vec2 map_uv = vec2(atan(point.y, point.x) / TAU + 0.5, acos(clamp(point.z / length(point), -1.0, 1.0)) / PI);
        vec3 albedo = surface_color;
        if (has_map) albedo = texture(surface_map, map_uv).rgb;
        float illumination = 0.06 + 0.94 * max(0.0, dot(normal, sun_direction));
        result = vec4(albedo * illumination, 1.0);
    }
    COLOR = result;
}
