// SPDX-License-Identifier: GPL-2.0-or-later
// Glare around a star whose disc is resolved (below a pixel or so its fuzzy point carries the glare instead): a bright
// glow just outside the limb falling off over a few disc radii, and a faint halo out to many radii, like the light an
// eye or a lens spreads around the Sun. A camera-facing quad on the star's node, drawn additively before the sky shells
// so the air dims and reddens it as it does the disc.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec3 glare_color = vec3(1.0);
uniform float strength = 1.0;
uniform float clip_near = 0.001;
// Star radius in scene units.
uniform float star_radius = 1.0;
// Quad half-size in disc radii (angularly).
const float EXTENT = 40.0;

varying vec2 v_local;
varying float v_disc;

void vertex() {
    vec3 center = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float dist = length(center);
    // Angular radius of the disc, and the quad laid in the plane facing the camera at the star's distance.
    float angle = asin(clamp(star_radius / max(dist, star_radius * 1.0001), 0.0, 1.0));
    float half_size = dist * tan(min(EXTENT * angle, 1.2));
    v_disc = tan(angle) / tan(min(EXTENT * angle, 1.2));
    v_local = VERTEX.xy;
    vec3 view = center + vec3(VERTEX.xy * half_size, 0.0);
    POSITION = PROJECTION_MATRIX * vec4(view, 1.0);
    POSITION.z = clip_near;
}

void fragment() {
    // Distance from the centre in disc radii.
    float rho = length(v_local) / max(v_disc, 1e-6);
    float outside = max(rho - 1.0, 0.0);
    float glow = 0.85 * exp(-outside * 1.6) + 0.25 / (1.0 + outside * outside * 0.35) + 0.04 / (1.0 + rho * rho * 0.004);
    float edge = 1.0 - smoothstep(0.7, 1.0, length(v_local));
    ALBEDO = glare_color;
    ALPHA = clamp(glow * edge * strength, 0.0, 1.0);
}
