// SPDX-License-Identifier: GPL-2.0-or-later
uniform bool use_atmosphere_lookup = false;
uniform sampler2D air_transmission : filter_linear, repeat_disable;
uniform sampler3D air_inscatter : filter_linear, repeat_disable;
uniform float air_lookup_height_km = 64.0;
uniform vec3 air_mie_ratios = vec3(1.0);
uniform float air_mie_asymmetry = 0.0;

float air_lookup_view(float cosine) {
    float biased = clamp(cosine, -1.0, 1.0) + 0.15;
    return 0.5 + 0.55 * biased / (0.1 + abs(biased));
}

void sample_atmosphere_lookup(vec3 eye, vec3 ray, vec3 light, float radius,
    out vec3 transmission, out vec3 radiance, out vec3 entry) {
    transmission = vec3(1.0);
    radiance = vec3(0.0);
    entry = eye;
    float outer = radius + air_lookup_height_km;
    float projection = dot(eye, ray);
    vec3 closest = eye - ray * projection;
    float discriminant = outer * outer - dot(closest, closest);
    if (discriminant <= 0.0) {
        return;
    }
    float distance = sqrt(discriminant);
    if (distance <= projection) {
        return;
    }
    entry += ray * max(0.0, -projection - distance);
    float length_km = length(entry);
    vec3 normal = entry / max(length_km, 0.001);
    float height = sqrt(clamp((length_km - radius) / air_lookup_height_km, 0.0, 1.0));
    float view = air_lookup_view(dot(ray, normal));
    float sun = (1.0 - exp(-2.0 * dot(light, normal) - 0.6)) / (1.0 - exp(-2.6));
    transmission = texture(air_transmission, vec2(view, height)).rgb;
    vec4 inscatter = texture(air_inscatter, vec3(sun, view, height));
    float cosine = clamp(dot(ray, light), -1.0, 1.0);
    float rayleigh = 0.1875 * (1.0 + cosine * cosine);
    float g = air_mie_asymmetry;
    float mie = 0.25 * (1.0 - g * g) * pow(max(1.0e-5, 1.0 + g * g - 2.0 * g * cosine), -1.5);
    vec3 aerosol = inscatter.rgb * inscatter.a * air_mie_ratios / max(inscatter.r, 1.0e-4);
    aerosol = mix(vec3(inscatter.a), aerosol, greaterThan(air_mie_ratios, vec3(0.0)));
    radiance = 3.0 * (inscatter.rgb * rayleigh + aerosol * mie);
}
