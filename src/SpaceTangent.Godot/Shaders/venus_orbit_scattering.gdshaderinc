// SPDX-License-Identifier: GPL-2.0-or-later
// The Venus profile uses kilometre distances and inverse-kilometre coefficients.
const vec3 venus_rayleigh = vec3(0.463, 1.082, 2.64) * (15.9 / 8.7);
const float venus_rayleigh_height = 8.7;
const float venus_mie = 0.0001;
const float venus_mie_height = 10.0;
const float venus_cloud_bottom = 48.0;
const float venus_cloud_top = 70.0;
const float venus_cloud_extinction = 30.0 / 22.0;
const vec3 venus_cloud_albedo = vec3(0.9999, 0.9995, 0.996);
const float venus_haze_height = 3.3;
const vec3 venus_haze = (0.2 / 3.3) * vec3(550.0 / 680.0, 1.0, 550.0 / 440.0);

float venus_chapman(float base, float radius, float cosine, float height) {
    float mu = clamp(cosine, -1.0, 1.0);
    float density = exp(min((base - radius) / height, 0.0));
    float grazing = sqrt(1.57079632679 * radius / height);
    float outward = density * height * grazing / (1.0 + (grazing - 1.0) * abs(mu));
    if (mu >= 0.0) {
        return outward;
    }
    float tangent = radius * sqrt(max(1e-8, 1.0 - mu * mu));
    return max(0.0, 2.0 * height * sqrt(1.57079632679 * tangent / height) *
        exp(min((base - tangent) / height, 0.0)) - outward);
}

float venus_sphere_path(vec3 point, vec3 ray, float radius, float limit) {
    vec2 span = air_span(point, ray, radius);
    return max(0.0, min(span.y, limit) - max(span.x, 0.0));
}

vec4 venus_columns(vec3 point, vec3 ray) {
    float radius = length(point);
    float mu = dot(point, ray) / radius;
    float top = ground_radius_km + venus_cloud_top;
    float bottom = ground_radius_km + venus_cloud_bottom;
    float clouds = venus_sphere_path(point, ray, top, 1e9) - venus_sphere_path(point, ray, bottom, 1e9);
    float haze = 0.0;
    if (radius >= top) {
        haze = venus_chapman(top, radius, mu, venus_haze_height);
    } else {
        float tangent_squared = radius * radius * (1.0 - mu * mu);
        float crossing = sqrt(max(0.0, 1.0 - tangent_squared / (top * top)));
        float exit_column = venus_chapman(top, top, crossing, venus_haze_height);
        if (mu >= 0.0) {
            haze = exit_column;
        } else if (tangent_squared >= ground_radius_km * ground_radius_km) {
            haze = 2.0 * exit_column;
        }
    }
    return vec4(venus_chapman(ground_radius_km, radius, mu, venus_rayleigh_height),
        venus_chapman(ground_radius_km, radius, mu, venus_mie_height), max(clouds, 0.0), haze);
}

vec3 venus_extinction(vec4 column) {
    return venus_rayleigh * column.x + vec3(venus_mie * column.y + venus_cloud_extinction * column.z) +
        venus_haze * column.w;
}

// Eddington two-stream diffuse irradiance, including absorption in the cloud droplets.
vec3 venus_diffuse(vec3 point, float cosine) {
    if (cosine <= 0.0) {
        return vec3(0.0);
    }
    vec4 column = venus_columns(point, normalize(point));
    vec3 rayleigh = venus_rayleigh * column.x;
    float mie = venus_mie * column.y;
    vec3 clouds = venus_cloud_extinction * venus_cloud_albedo * column.z;
    vec3 haze = venus_haze * venus_cloud_albedo * column.w;
    vec3 scattering = rayleigh + vec3(mie) + clouds + haze;
    vec3 reduced = rayleigh + vec3(0.3 * mie) + 0.25 * clouds + 0.5 * haze;
    vec3 absorption = max(venus_extinction(column) - scattering, vec3(0.0));
    vec3 beam = exp(-scattering / cosine);
    vec3 total = (vec3(0.5 + 0.75 * cosine) + (0.5 - 0.75 * cosine) * beam) / (vec3(1.0) + 0.75 * reduced);
    return cosine * max(total - beam, vec3(0.0)) * exp(-sqrt(3.0 * absorption * (absorption + reduced)));
}

float venus_phase(float cosine, float asymmetry) {
    return 3.0 * (1.0 - asymmetry * asymmetry) * (1.0 + cosine * cosine) /
        (8.0 * (2.0 + asymmetry * asymmetry) * pow(max(1e-4, 1.0 + asymmetry * asymmetry - 2.0 * asymmetry * cosine), 1.5));
}

vec3 venus_sky_transmission(vec3 eye, vec3 ray) {
    vec2 outer = air_span(eye, ray, ground_radius_km + shell_height_km);
    vec2 ground = air_span(eye, ray, ground_radius_km);
    float begin = max(0.0, outer.x);
    float end = ground.x >= 0.0 ? min(outer.y, ground.x) : outer.y;
    if (end <= begin) {
        return vec3(1.0);
    }
    vec4 column = venus_columns(eye + begin * ray, ray) - venus_columns(eye + end * ray, ray);
    return exp(-max(venus_extinction(column), vec3(0.0)));
}

void venus_air(vec3 eye, vec3 ray, float solid_radius, out vec3 transmission, out vec3 radiance) {
    float shell_radius = ground_radius_km + shell_height_km;
    vec2 outer = air_span(eye, ray, shell_radius);
    vec2 inner = air_span(eye, ray, solid_radius);
    float start = max(0.0, outer.x);
    float end = inner.x >= 0.0 ? min(outer.y, inner.x) : outer.y;
    float distance = max(0.0, end - start);
    vec3 entry = eye + ray * start;
    float top = ground_radius_km + venus_cloud_top;
    float bottom = ground_radius_km + venus_cloud_bottom;
    float shadow_width = 0.5 * (shell_radius + ground_radius_km) * sqrt(2.0 * venus_rayleigh_height / ground_radius_km);
    float cosine = dot(ray, light_in_body);
    // Cornette-Shanks parameters for mean cosines 0.7, 0.75, and 0.5.
    vec4 phase = vec4(0.1875 * (1.0 + cosine * cosine), venus_phase(cosine, 0.63706852),
        venus_phase(cosine, 0.69178581), venus_phase(cosine, 0.43553118));
    bool inside = length(eye) < shell_radius;
    vec3 depth = vec3(0.0);
    radiance = vec3(0.0);
    for (int index = 0; index < 16; index++) {
        float u = float(index) / 16.0;
        float v = float(index + 1) / 16.0;
        float from = distance * (inside ? u * u : u);
        float to = distance * (inside ? v * v : v);
        float length_km = to - from;
        vec3 segment = entry + ray * from;
        vec3 point = segment + ray * (0.5 * length_km);
        float radius = length(point);
        float altitude = max(0.0, radius - ground_radius_km);
        float clouds = venus_sphere_path(segment, ray, top, length_km) - venus_sphere_path(segment, ray, bottom, length_km);
        float haze = radius >= top ? length_km * exp((top - radius) / venus_haze_height) : 0.0;
        vec4 column = vec4(length_km * exp(-altitude / venus_rayleigh_height),
            length_km * exp(-altitude / venus_mie_height), max(clouds, 0.0), haze);
        vec3 tau = venus_extinction(column);
        float sun_dot = dot(point, light_in_body);
        float horizon = sqrt(max(0.0, radius * radius - ground_radius_km * ground_radius_km));
        float lit = smoothstep(-shadow_width, shadow_width, sun_dot + horizon);
        if (lit > 0.0) {
            vec3 rayleigh = venus_rayleigh * column.x;
            vec3 mie = vec3(venus_mie * column.y);
            vec3 cloud = venus_cloud_extinction * venus_cloud_albedo * column.z;
            vec3 upper = venus_haze * venus_cloud_albedo * column.w;
            vec3 direct = exp(-venus_extinction(venus_columns(point, light_in_body)));
            vec3 single_scatter = (rayleigh * phase.x + mie * phase.y + cloud * phase.z + upper * phase.w) * direct;
            vec3 multiple_scatter = (rayleigh + mie + cloud + upper) * venus_diffuse(point, sun_dot / radius);
            radiance += lit * exp(-(depth + 0.5 * tau)) * (single_scatter + multiple_scatter);
        }
        depth += tau;
    }
    transmission = exp(-depth);
}
