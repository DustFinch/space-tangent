// SPDX-License-Identifier: GPL-2.0-or-later
shader_type spatial;
render_mode unshaded;

uniform vec4 photosphere : source_color = vec4(1.0, 0.84, 0.61, 1.0);
varying vec3 surface_direction;

void vertex() {
    surface_direction = normalize(VERTEX);
}

void fragment() {
    float toward_eye = max(dot(normalize(NORMAL), VIEW), 0.0);
    float limb = mix(0.51, 1.0, pow(toward_eye, 0.55));
    vec3 p = normalize(surface_direction);
    float granulation = sin(dot(p, vec3(731.0, 191.0, -317.0))) *
        sin(dot(p, vec3(-401.0, 619.0, 271.0)));
    vec3 center = mix(photosphere.rgb, vec3(1.0), 0.32 * toward_eye);
    ALBEDO = center * limb * (0.98 + 0.02 * granulation);
}
