// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

uniform int profile = 0;
uniform bool elliptical = false;
uniform float concentration = 1.5;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
        mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0)), f.x), f.y);
}

void fragment() {
    vec2 p = (UV - 0.5) * 2.0;
    float radius = length(p);
    float edge = 1.0 - smoothstep(0.68, 1.0, radius);
    if (profile == 0 && elliptical) {
        ALBEDO = mix(vec3(0.74, 0.59, 0.42), vec3(1.0, 0.93, 0.73), exp(-6.0 * radius));
        ALPHA = edge * min(1.0, 1.4 * exp(-4.8 * radius));
    } else if (profile == 0) {
        float theta = atan(p.y, p.x);
        float arms = 0.5 + 0.5 * cos(2.0 * theta - 10.0 * log(radius + 0.25) + 1.6 * noise(p * 12.0));
        float disk = exp(-3.0 * radius) * (0.3 + 0.7 * pow(arms, 3.0)) * (0.4 + 0.6 * noise(p * 37.0));
        float core = exp(-11.0 * radius);
        ALBEDO = mix(vec3(0.54, 0.68, 1.0), vec3(1.0, 0.82, 0.58), exp(-4.0 * radius));
        ALPHA = min(1.0, edge * (disk * 1.45 + core));
    } else if (profile == 1) {
        float turbulence = noise(p * 6.0) + 0.50 * noise(p * 15.0) + 0.24 * noise(p * 40.0);
        float cloud = smoothstep(0.53, 1.22, turbulence - radius * 0.43);
        ALBEDO = mix(vec3(0.49, 0.19, 0.48), vec3(0.52, 0.86, 1.0), clamp(1.0 - radius, 0.0, 1.0));
        ALPHA = edge * cloud * 0.9;
    } else if (profile == 3) {
        vec2 cell = floor(p * 70.0);
        vec2 local = fract(p * 70.0) - vec2(hash(cell), hash(cell + vec2(37.0, 19.0)));
        float density = exp(-clamp(concentration, 0.8, 3.0) * 3.0 * radius);
        float spark = exp(-55.0 * dot(local, local)) * step(0.98 - 0.82 * density, hash(cell + vec2(6.0)));
        ALBEDO = mix(vec3(0.66, 0.75, 1.0), vec3(1.0, 0.87, 0.64), hash(cell + vec2(4.0)));
        ALPHA = edge * min(1.0, spark + 0.58 * density);
    } else {
        vec2 cell = floor(p * 24.0);
        vec2 local = fract(p * 24.0) - vec2(hash(cell), hash(cell + vec2(31.0, 13.0)));
        float spark = exp(-85.0 * dot(local, local));
        float population = step(0.80 - 0.30 * (1.0 - radius), hash(cell + vec2(9.0)));
        ALBEDO = vec3(0.63, 0.76, 1.0);
        ALPHA = edge * (spark * population + 0.11 * exp(-3.0 * radius) * noise(p * 11.0));
    }
}
