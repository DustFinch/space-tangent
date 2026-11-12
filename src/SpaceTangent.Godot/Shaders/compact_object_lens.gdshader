// SPDX-License-Identifier: GPL-2.0-or-later
// Gravitational lensing by a Schwarzschild black hole (SpaceTangent.Core.Observing.SchwarzschildLens, ShaderSweptAngle). A screen-
// filling triangle at infinite depth (reverse Z: 0) bends every view ray: with u = r_s / r of the observer and psi the
// angle from the hole, the ray's impact parameter is b = sin(psi) / (u sqrt(1 - u)); rays that end in the hole are the
// shadow, the others sweep an angle about the hole on their way from the sky, which is then read from six cube faces
// of the static sky captured in the world frame. Pixels the hole moves by less than a quarter pixel are discarded so
// the ordinary sky shows through. A thin accretion disk (with bent disk intersections) is found by tracing
// the ray backward through the same metric; every image along the ray is drawn at its observed blackbody temperature
// and composed in front of the sky or the shadow. Lengths are in units of r_s.
//
// A spinning hole (hole_spin = a / M about the disk's axis) is traced instead by SpaceTangent.Core.Observing.KerrLens: for rays whose
// Schwarzschild impact parameter is below kerr_limit(), Carter's equations in Mino time are integrated per pixel (in
// units of M), for the sky, the shadow and the disk at once; beyond it the frame dragging is negligible and the
// Schwarzschild sky above is used, blended over the last fifth so no seam shows.
//
// Bodies are lensed too, in screen space: the triangle tests no depth itself. A pixel whose body (opaque pass) is nearer
// than the hole keeps it; elsewhere the light's straight path far behind the hole (the line it left its source along,
// which passes the hole at the impact parameter) is followed to the body drawn on the screen: first in its direction,
// as if the body were at infinity, then at the distance the depth buffer gives there, twice. A body at any distance
// behind the hole is seen where the bent ray meets it; only what the opaque pass drew shows (no atmospheres or
// clouds), and a body off the screen is not seen in the lens.
//
// Cinematic holes (disk_cinematic) also draw the gas off the plane: the disk as a slab of Gaussian thickness, a corona
// of hot, thin plasma over its inner part and two jets along the axis, Doppler-beamed. Illustration, not measurement:
// Cygnus X-1's corona shines in X-rays and its jet in radio waves, not in visible light.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled;

uniform vec3 lens_position;
uniform float lens_radius = 1.0;
uniform mat3 camera_to_sky;
uniform float pixel_angle = 0.0013;
// alpha(b) at v = i / (size - 1), v = -ln(1 - b_c / b) / lambda.
uniform sampler2D deflection_table : filter_nearest, repeat_disable;
uniform float table_size = 4096.0;
uniform float table_lambda = 18.0;
// Cube faces +X, -X, +Y, -Y, +Z, -Z; each spans tan = face_extent from its axis.
uniform sampler2D face0 : source_color, filter_linear, repeat_disable;
uniform sampler2D face1 : source_color, filter_linear, repeat_disable;
uniform sampler2D face2 : source_color, filter_linear, repeat_disable;
uniform sampler2D face3 : source_color, filter_linear, repeat_disable;
uniform sampler2D face4 : source_color, filter_linear, repeat_disable;
uniform sampler2D face5 : source_color, filter_linear, repeat_disable;
uniform float face_extent = 1.0;
// The accretion disk: camera-space axis (zero: none), outer radius (r_s), peak temperature (K), and the blackbody table
// (BlackbodyColor.Table: linear sRGB of unit luminance and log10 luminance at log10 T from color_log_min to color_log_max).
uniform vec3 disk_normal;
// Azimuth zero of the gas pattern in the disk plane (camera space, fixed in the world).
uniform vec3 disk_reference = vec3(1.0, 0.0, 0.0);
uniform float disk_outer = 30.0;
// Spin a / M about disk_normal (0: Schwarzschild); the disk's inner edge moves in to the innermost stable orbit.
uniform float hole_spin = 0.0;
uniform float disk_temperature = 1.0e6;
// Display exposure of the disk's peak at rest: 0.4 physical (the beamed side stays below white); higher when cinematic,
// where the tone mapping and glow take the highlights.
uniform float disk_exposure = 0.4;
// SceneDiskStyle: 0 physical (optically thick, smooth), 1 cinematic (streaked gas of optical depth DISK_DEPTH).
uniform float disk_cinematic = 0.0;
const float DISK_DEPTH = 1.4, CINEMATIC_RANGE = 0.4;
// Cinematic gas off the plane: the disk's scale height over r, the corona's and the jets' glow (per r_s of path, in
// units of the disk's display exposure), the corona's scale (inner edges) and the jets' speed (c) and opening.
const float DISK_THICKNESS = 0.02, CORONA_GLOW = 0.015, CORONA_SCALE = 1.0, JET_GLOW = 0.12, JET_SPEED = 0.6, JET_OPENING = 0.06;
uniform sampler2D color_table : filter_nearest, repeat_disable;
uniform float color_log_min = 3.0;
uniform float color_log_max = 9.0;
uniform float color_size = 512.0;
const float STEP_FRACTION = 0.05;
const float KERR_FAR = 1000.0, KERR_STEP = 0.05;
// KerrLens.PolarCap: a step that comes within this sine of the angle to the spin axis takes the singular b / sin^2
// term of phi' in closed form, as the change of the sweep acos(s_min / sin) about the axis (negative while nearing it).
const float KERR_CAP = 0.02;
const int KERR_STEPS = 900;
const int MAX_STEPS = 600;

const float BC = 2.598076211353316; // 3 sqrt(3) / 2
// SchwarzschildLens.Gauss16: 16-point Gauss-Legendre on [0, 1].
const float GL_X[16] = float[](0.005299532504175031, 0.0277124884633837, 0.06718439880608412, 0.1222977958224985,
    0.19106187779867811, 0.2709916111713863, 0.35919822461037054, 0.4524937450811813, 0.5475062549188188,
    0.6408017753896295, 0.7290083888286136, 0.8089381222013219, 0.8777022041775016, 0.9328156011939159,
    0.9722875115366163, 0.994700467495825);
const float GL_W[16] = float[](0.013576229705877029, 0.031126761969323888, 0.04757925584124645, 0.06231448562776697,
    0.07479799440828841, 0.08457825969750128, 0.0913017075224618, 0.09472530522753424, 0.09472530522753424,
    0.0913017075224618, 0.08457825969750128, 0.07479799440828841, 0.06231448562776697, 0.04757925584124645,
    0.031126761969323888, 0.013576229705877029);

varying vec3 v_ray;
// The opaque pass: colour and reverse-Z depth.
uniform sampler2D screen_texture : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
uniform float clip_near = 0.001;
uniform float clip_far = 12000.0;

float view_depth(float depth) {
    return clip_near * clip_far / (clip_near + (clip_far - clip_near) * depth);
}
// The glare of the brightest resolved star (world_star_glare.gdshader), drawn in the lens about the star's bent image:
// its centre and radius (camera space), colour and strength (0: none).
uniform vec3 glare_position;
uniform float glare_radius = 0.0;
uniform vec3 glare_color = vec3(1.0);
uniform float glare_strength = 0.0;

// What the light that left its source along the line origin + s dir (camera space, s > 0 away from the hole) shows: a
// body drawn where the line meets it, farther than the hole (hole_depth, view distance), or else the sky colour given.
// The line is looked up first by its direction (a body at infinity), then at the distance drawn there, twice. here_uv
// and here_ndc place this pixel on the screen; flip is the sign between NDC y and screen v.
vec3 behind_hole(vec3 origin, vec3 dir, vec3 sky, mat4 projection, vec2 here_uv, vec2 here_ndc, float flip, float hole_depth) {
    if (dir.z > -1.0e-4) return sky;
    vec4 clip = projection * vec4(dir, 0.0);
    vec2 uv = here_uv + (clip.xy / clip.w - here_ndc) * vec2(0.5, 0.5 * flip);
    for (int pass = 0; pass < 3; pass++) {
        if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) return sky;
        float depth = textureLod(depth_texture, uv, 0.0).r;
        if (depth <= 0.0 || view_depth(depth) <= hole_depth) return sky;
        if (pass == 2) break;
        // Reverse Z gives an axial distance. Intersect the bent ray with that view-space depth plane.
        float s = (-view_depth(depth) - origin.z) / dir.z;
        if (s <= 0.0) return sky;
        clip = projection * vec4(origin + dir * s, 1.0);
        uv = here_uv + (clip.xy / clip.w - here_ndc) * vec2(0.5, 0.5 * flip);
    }
    return textureLod(screen_texture, uv, 0.0).rgb;
}

float kerr_isco(float a) {
    float z1 = 1.0 + pow(1.0 - a * a, 1.0 / 3.0) * (pow(1.0 + a, 1.0 / 3.0) + pow(max(1.0 - a, 0.0), 1.0 / 3.0));
    float z2 = sqrt(3.0 * a * a + z1 * z1);
    return 3.0 + z2 - sqrt(max(0.0, (3.0 - z1) * (3.0 + z1 + 2.0 * z2)));
}
// The disk's inner edge in r_s: half the prograde innermost stable orbit in M (3 r_s without spin).
float disk_inner() { return 0.5 * kerr_isco(hole_spin); }

void vertex() {
    POSITION = vec4(VERTEX.xy, 0.0, 1.0);
    vec4 near_point = INV_PROJECTION_MATRIX * vec4(VERTEX.xy, 1.0, 1.0);
    v_ray = near_point.xyz / near_point.w;
}

float deflection(float b) {
    float x = BC / b;
    float v = (x < 1.0e-3 ? x * (1.0 + 0.5 * x) : -log(1.0 - x)) / table_lambda;
    float last = table_size - 1.0, t = v * last;
    if (t >= last) return texelFetch(deflection_table, ivec2(int(last), 0), 0).r + (t - last) / last * table_lambda;
    int i = int(t);
    float a0 = texelFetch(deflection_table, ivec2(i, 0), 0).r, a1 = texelFetch(deflection_table, ivec2(i + 1, 0), 0).r;
    return mix(a0, a1, t - float(i));
}

// The periapsis u_t: the trigonometric root of u^3 - u^2 + 1/b^2 = 0, polished by two Newton steps.
float turning_point(float b) {
    float theta = acos(clamp(1.0 - 13.5 / (b * b), -1.0, 1.0));
    float u = max(1.0 / b, 1.0 / 3.0 + 2.0 / 3.0 * cos(theta / 3.0 - 2.0 * PI / 3.0));
    for (int step = 0; step < 2; step++) {
        float slope = 2.0 * u - 3.0 * u * u;
        if (slope > 0.0) u -= (u * u * (1.0 - u) - 1.0 / (b * b)) / slope;
    }
    return u;
}

// Angle swept about the hole between the sky and the observer; negative when the ray ends in the hole.
float swept_angle(float u, float psi, bool inward) {
    float b = sin(psi) / (u * sqrt(1.0 - u)), beta = b * u;
    if (u < 2.0 / 3.0 ? inward && b <= BC : inward || b >= BC) return -1.0;
    float sum = 0.0;
    if (b <= BC || beta < 0.5) {
        // From infinity to the observer, u = u_o t (2 - t).
        for (int k = 0; k < 16; k++) {
            float t = GL_X[k], s = t * (2.0 - t), q = 1.0 - beta * beta * s * s * (1.0 - u * s);
            sum += GL_W[k] * 2.0 * (1.0 - t) * beta / sqrt(max(q, 1.0e-30));
        }
        if (b <= BC || !inward) return sum;
        return PI + deflection(b) - sum;
    }
    // From the observer to the periapsis, u = u_t (1 - w^2): half the path from infinity, plus or minus this.
    float ut = turning_point(b), wo = sqrt(max(0.0, 1.0 - u / ut));
    for (int k = 0; k < 16; k++) {
        float w = wo * GL_X[k], v = ut * (1.0 - w * w);
        sum += GL_W[k] / sqrt(max(ut + v - (ut * ut + ut * v + v * v), 1.0e-30));
    }
    float between = 2.0 * sqrt(ut) * wo * sum;
    return 0.5 * (PI + deflection(b)) + (inward ? between : -between);
}

// AccretionDisk.ShaderHits: d^2 p / d lambda^2 = -1.5 h^2 p / |p|^5 by RK4 in steps of STEP_FRACTION r, each plane
// crossing placed on the step's Hermite arc.
vec3 accel(vec3 x, float h2) { float l = length(x); return x * (-1.5 * h2 / (l * l * l * l * l)); }
vec3 hermite(vec3 p0, vec3 m0, vec3 p1, vec3 m1, float t) {
    float t2 = t * t, t3 = t2 * t;
    return p0 * (2.0 * t3 - 3.0 * t2 + 1.0) + m0 * (t3 - 2.0 * t2 + t) + p1 * (-2.0 * t3 + 3.0 * t2) + m1 * (t3 - t2);
}
vec3 hermite_slope(vec3 p0, vec3 m0, vec3 p1, vec3 m1, float t) {
    float t2 = t * t;
    return p0 * (6.0 * t2 - 6.0 * t) + m0 * (3.0 * t2 - 4.0 * t + 1.0) + p1 * (-6.0 * t2 + 6.0 * t) + m1 * (3.0 * t2 - 2.0 * t);
}
vec4 blackbody(float kelvin) {
    float t = clamp((log(kelvin) / log(10.0) - color_log_min) / (color_log_max - color_log_min), 0.0, 1.0) * (color_size - 1.0);
    int i = min(int(t), int(color_size) - 2);
    return mix(texelFetch(color_table, ivec2(i, 0), 0), texelFetch(color_table, ivec2(i + 1, 0), 0), t - float(i));
}
float disk_profile(float r) { return pow(r, -0.75) * pow(max(0.0, 1.0 - sqrt(disk_inner() / r)), 0.25); }
// A blackbody seen at g T: its colour, and its luminance relative to the disk's peak at rest. The cinematic disk keeps
// the order of brightness but compresses its range (the Wien tail falls by orders of magnitude to the outer edge).
vec3 disk_color(float radius, float redshift) {
    float kelvin = disk_temperature * disk_profile(radius) / disk_profile(49.0 / 36.0 * disk_inner()) * redshift;
    vec4 seen = blackbody(kelvin), peak = blackbody(disk_temperature);
    float luminance = (seen.a - peak.a) * (disk_cinematic > 0.5 ? CINEMATIC_RANGE : 1.0);
    return seen.rgb * pow(10.0, luminance) * disk_exposure;
}

// Cinematic gas: value noise in (ln r, a point on the circle of the azimuth), so the pattern is periodic in azimuth and
// its rings narrow with r; each ring turns at the Keplerian rate (display time: the inner edge in INNER_PERIOD s).
float hash3(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453123); }
float value_noise(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0); // quintic: no creases at the cell edges
    return mix(mix(mix(hash3(i), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}
float streaks(float r, float azimuth, float seed) {
    vec3 q = vec3(cos(azimuth) * 1.6, sin(azimuth) * 1.6, log(r) * 9.0) + seed;
    float sum = 0.0, amplitude = 0.5;
    // Each octave turned, so the cell grids of the octaves never line up into blocks.
    const mat3 turn = mat3(vec3(0.00, 0.80, 0.60), vec3(-0.80, 0.36, -0.48), vec3(-0.60, -0.48, 0.64));
    for (int octave = 0; octave < 5; octave++) { sum += amplitude * value_noise(q); q = turn * q * 2.03 + 1.7; amplitude *= 0.5; }
    return sum / 0.96875;
}
const float INNER_PERIOD = 8.0, FLOW_PERIOD = 30.0;
// The shear of differential rotation grows without bound, so two layers restart in turn and cross-fade (flow mapping).
float gas_density(float r, float azimuth, float time) {
    float spin = 6.283185307 / INNER_PERIOD * pow(disk_inner() / r, 1.5);
    float s1 = fract(time / FLOW_PERIOD), s2 = fract(time / FLOW_PERIOD + 0.5);
    float d = mix(streaks(r, azimuth - spin * s1 * FLOW_PERIOD, 0.0), streaks(r, azimuth - spin * s2 * FLOW_PERIOD, 31.7), abs(2.0 * s1 - 1.0));
    return (0.08 + 0.92 * smoothstep(0.32, 0.78, d)) * smoothstep(disk_outer, 0.7 * disk_outer, r);
}

// Abramowitz & Stegun 7.1.26 (error below 1.5e-7).
float erf_approx(float x) {
    float t = 1.0 / (1.0 + 0.3275911 * abs(x));
    float y = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
    return x < 0.0 ? -y : y;
}
// The cinematic gas over one straight step of the traced ray from a to b (r_s, hole at the origin, disk axis n,
// azimuth zero e1), added to light behind what has passed so far (pass): the disk as a Gaussian slab of scale height
// DISK_THICKNESS rho and vertical optical depth DISK_DEPTH x density, seen at redshift g; the corona and the jets
// optically thin. The light travels from b to a.
void cinematic_step(vec3 a, vec3 b, vec3 n, vec3 e1, float g, float time, inout vec3 light, inout float pass) {
    float len = length(b - a);
    if (len <= 0.0) return;
    vec3 mid = 0.5 * (a + b);
    float za = dot(a, n), zb = dot(b, n), zm = dot(mid, n), rho = length(mid - n * zm), inner = disk_inner();
    float h = DISK_THICKNESS * rho;
    if (rho >= inner && rho <= disk_outer && (za * zb <= 0.0 || min(abs(za), abs(zb)) < 5.0 * h)) {
        float density = gas_density(rho, atan(dot(mid, cross(n, e1)), dot(mid, e1)), time);
        // The column of exp(-z^2 / 2 h^2) along the step, normalised to 1 straight through the slab.
        float column = abs(zb - za) > 0.01 * h
            ? len * (erf_approx(zb / (1.41421356 * h)) - erf_approx(za / (1.41421356 * h))) / (2.0 * (zb - za))
            : len * exp(-0.5 * zm * zm / (h * h)) / (2.50662827 * h);
        float opacity = 1.0 - exp(-DISK_DEPTH * density * column);
        light += pass * opacity * disk_color(rho, g);
        pass *= 1.0 - opacity;
    }
    // The corona: a glow over the inner disk, hotter than the disk, falling off with distance from the hole.
    float r = length(mid);
    vec3 hot = blackbody(3.0 * disk_temperature).rgb * disk_exposure;
    light += pass * hot * (CORONA_GLOW * exp(-r / (CORONA_SCALE * inner)) * smoothstep(1.0, 1.5, r) * len);
    // The jets: narrow cones along the axis from above the inner disk, fading out toward the disk's outer radius, the
    // one coming toward the light's path brightened by delta^3 (Doppler factor of gas at JET_SPEED).
    float height = abs(zm), width = 0.3 * inner + JET_OPENING * height;
    float jet = JET_GLOW * exp(-rho * rho / (width * width)) * smoothstep(inner, 2.0 * inner, height) *
        (1.0 - smoothstep(0.6 * disk_outer, disk_outer, height)) * inner / max(height, inner);
    if (jet > 1.0e-5) {
        float cosine = dot(sign(zm) * n, (a - b) / len), gamma = inversesqrt(1.0 - JET_SPEED * JET_SPEED);
        float delta = 1.0 / (gamma * (1.0 - JET_SPEED * cosine));
        light += pass * vec3(0.55, 0.7, 1.0) * disk_exposure * (jet * delta * delta * delta * len);
    }
}

// The disk images along the ray, nearest first, composed as emitting slabs: each adds I (1 - e^-tau) of what still
// passes, tau = depth x density / |cos| of the ray to the axis. Returns the light and the transmittance left for the sky.
// A cinematic disk is gas off the plane instead (cinematic_step), every step.
vec4 disk_trace(vec3 observer, vec3 d, vec3 n, vec3 e1, float time) {
    float r0 = length(observer), u0 = 1.0 / r0;
    vec3 er = observer / r0;
    vec3 v = d + er * (dot(d, er) * (sqrt(1.0 - u0) - 1.0));
    vec3 p = observer;
    vec3 momentum = cross(p, v);
    float h2 = dot(momentum, momentum);
    // Rays from outside the disk that never come within its outer radius: b > r_out / sqrt(1 - 1/r_out) or outward.
    if (r0 > disk_outer && (dot(d, er) > 0.0 || h2 / (1.0 - u0) > disk_outer * disk_outer / (1.0 - 1.0 / disk_outer))) return vec4(0.0, 0.0, 0.0, 1.0);
    float lz = -dot(momentum, n) / sqrt(1.0 - u0);
    vec3 e2 = cross(n, e1);
    bool cinematic = disk_cinematic > 0.5;
    vec3 light = vec3(0.0);
    float pass = 1.0;
    int images = 0;
    float side = dot(p, n);
    for (int step = 0; step < MAX_STEPS; step++) {
        float r = length(p);
        if (r < 1.0 || (r > disk_outer && dot(p, v) > 0.0)) break;
        float dl = STEP_FRACTION * r;
        vec3 k1v = accel(p, h2), k1p = v;
        vec3 k2v = accel(p + k1p * (dl * 0.5), h2), k2p = v + k1v * (dl * 0.5);
        vec3 k3v = accel(p + k2p * (dl * 0.5), h2), k3p = v + k2v * (dl * 0.5);
        vec3 k4v = accel(p + k3p * dl, h2), k4p = v + k3v * dl;
        vec3 np = p + (k1p + 2.0 * k2p + 2.0 * k3p + k4p) * (dl / 6.0);
        vec3 nv = v + (k1v + 2.0 * k2v + 2.0 * k3v + k4v) * (dl / 6.0);
        float next_side = dot(np, n);
        if (cinematic) {
            vec3 mid = 0.5 * (p + np);
            float rho = max(length(mid - n * dot(mid, n)), 1.6);
            float g = sqrt(1.0 - 1.5 / rho) / (1.0 - sqrt(0.5 / (rho * rho * rho)) * lz) / sqrt(1.0 - u0);
            cinematic_step(p, np, n, e1, g, time, light, pass);
            if (pass < 0.003) break;
        }
        else if (side * next_side < 0.0) {
            float f = side / (side - next_side);
            for (int k = 0; k < 2; k++) {
                float s = dot(hermite(p, v * dl, np, nv * dl, f), n), ds = dot(hermite_slope(p, v * dl, np, nv * dl, f), n);
                if (ds != 0.0) f = clamp(f - s / ds, 0.0, 1.0);
            }
            vec3 at = hermite(p, v * dl, np, nv * dl, f);
            float radius = length(at);
            if (radius >= disk_inner() && radius <= disk_outer) {
                float g = sqrt(1.0 - 1.5 / radius) / (1.0 - sqrt(0.5 / (radius * radius * radius)) * lz) / sqrt(1.0 - u0);
                float density = cinematic ? gas_density(radius, atan(dot(at, e2), dot(at, e1)), time) : 1.0;
                float slant = abs(dot(normalize(hermite_slope(p, v * dl, np, nv * dl, f)), n));
                float opacity = 1.0 - exp(-(cinematic ? DISK_DEPTH : 1.0e4) * density / max(slant, 0.05));
                light += pass * opacity * disk_color(radius, g);
                pass *= 1.0 - opacity;
                images++;
                if (pass < 0.003 || images == 4) break;
            }
        }
        p = np; v = nv; side = next_side;
    }
    return vec4(light, pass);
}

// KerrLens.Start: the photon at p (hole frame, M) moving along unit d, seen by the zero-angular-momentum observer.
// Returns (b, q, E) and the state (u, u', mu, mu') of the ray followed back in time, and phi.
void kerr_start(float a, vec3 p, vec3 d, out vec3 constants, out vec4 state, out float phi) {
    float k0 = dot(p, p) - a * a;
    float r = sqrt(0.5 * (k0 + sqrt(k0 * k0 + 4.0 * a * a * p.z * p.z)));
    float mu = clamp(p.z / r, -1.0, 1.0), sn = sqrt(max(1.0e-12, 1.0 - mu * mu));
    phi = atan(p.y, p.x);
    vec3 er = vec3(sn * cos(phi), sn * sin(phi), mu), et = vec3(mu * cos(phi), mu * sin(phi), -sn), ep = vec3(-sin(phi), cos(phi), 0.0);
    float nr = dot(d, er), nt = dot(d, et), np = dot(d, ep);
    float sigma = r * r + a * a * mu * mu, delta = r * r - 2.0 * r + a * a;
    float big = (r * r + a * a) * (r * r + a * a) - a * a * delta * sn * sn;
    float alpha = sqrt(sigma * delta / big), omega = 2.0 * a * r / big, varpi = sn * sqrt(big / sigma);
    float e = alpha + omega * varpi * np, b = varpi * np / e, pt = sqrt(sigma) * nt / e;
    float q = pt * pt + mu * mu * (b * b / (sn * sn) - a * a);
    float vr = sqrt(sigma * delta) * nr / e;
    constants = vec3(b, q, e);
    // Back in time: r' and mu' negated, u' = -r' / r^2.
    state = vec4(1.0 / r, vr / (r * r), mu, sn * pt);
}
// U'(u) / 2, M'(mu) / 2 and phi' (negated, back in time): its regular part, and the singular -b / sin^2 part.
vec4 kerr_rates(float a, vec3 k, vec4 y, out float dphi, out float dsing) {
    float b = k.x, q = k.y, kk = q + (b - a) * (b - a), c = a * a - a * b, u = y.x, mu = y.z;
    dphi = -(a * (1.0 + c * u * u) / (1.0 - 2.0 * u + a * a * u * u) - a);
    dsing = -b / max(1.0 - mu * mu, KERR_CAP * KERR_CAP);
    return vec4(y.y, 2.0 * c * u * (1.0 + c * u * u) - kk * (u - 3.0 * u * u + 2.0 * a * a * u * u * u),
        y.w, -q * mu + a * a * mu * (1.0 - 2.0 * mu * mu) - b * b * mu);
}
// The sweep in phi about the spin axis from the closest approach s_min to sin(theta), negative while still nearing it.
float kerr_sweep(float mu, float vmu, float closest) {
    float sn = sqrt(max(0.0, 1.0 - mu * mu)), angle = acos(sn > 0.0 ? min(1.0, closest / sn) : 0.0);
    return mu * vmu > 0.0 ? -angle : angle;
}
// KerrLens.Trace: the observer at o and view direction d in the hole frame (M). Returns the disk's light and the
// transmittance left; sky gets the direction the ray came from (hole frame), or zero when the hole captured it, and
// origin a point of the straight line it came along (where it escaped, KERR_FAR out).
vec4 kerr_trace(float a, vec3 o, vec3 d, float outer, float time, out vec3 sky, out vec3 origin) {
    sky = vec3(0.0); origin = o;
    vec4 none = vec4(0.0, 0.0, 0.0, 1.0);
    vec3 start = o;
    if (length(o) > KERR_FAR) {
        float along = -dot(o, d), closest2 = dot(cross(o, d), cross(o, d));
        if (along <= 0.0 || closest2 >= KERR_FAR * KERR_FAR) { sky = d; return none; }
        start = o + d * (along - sqrt(KERR_FAR * KERR_FAR - closest2));
    }
    float horizon = 1.0 + sqrt(1.0 - a * a), inner = 2.0 * disk_inner();
    vec3 k; vec4 y; float phi;
    kerr_start(a, start, -d, k, y, phi);
    if (y.x >= 1.0 / horizon) return none;
    float escape = 1.0 / (max(KERR_FAR, 1.0 / y.x) * 1.0001), rate = sqrt(k.x * k.x + max(0.0, k.y));
    // The polar cap (KerrLens.PolarCap): the closest approach to the axis, the sense of the turn, where the cap was entered.
    float closest = abs(k.x) / sqrt(max(k.y + a * a, 1.0e-30)), turn = k.x < 0.0 ? 1.0 : -1.0;
    bool cinematic = disk_cinematic > 0.5;
    vec3 light = vec3(0.0);
    float pass = 1.0;
    int images = 0;
    for (int step = 0; step < KERR_STEPS; step++) {
        float sin2 = 1.0 - y.z * y.z;
        float dt = KERR_STEP * y.x / (1.0 + y.x * rate);
        if (sin2 >= KERR_CAP * KERR_CAP && k.x != 0.0) dt = min(dt, KERR_STEP * sin2 / abs(k.x));
        float p1, p2, p3, p4, s1, s2, s3, s4;
        vec4 k1 = kerr_rates(a, k, y, p1, s1);
        vec4 k2 = kerr_rates(a, k, y + k1 * (0.5 * dt), p2, s2);
        vec4 k3 = kerr_rates(a, k, y + k2 * (0.5 * dt), p3, s3);
        vec4 k4 = kerr_rates(a, k, y + k3 * dt, p4, s4);
        vec4 ny = y + (k1 + 2.0 * k2 + 2.0 * k3 + k4) * (dt / 6.0);
        float nphi = phi + (p1 + 2.0 * p2 + 2.0 * p3 + p4) * (dt / 6.0);
        if (sin2 < KERR_CAP * KERR_CAP || 1.0 - ny.z * ny.z < KERR_CAP * KERR_CAP)
            nphi += turn * (kerr_sweep(ny.z, ny.w, closest) - kerr_sweep(y.z, y.w, closest));
        else nphi += (s1 + 2.0 * s2 + 2.0 * s3 + s4) * (dt / 6.0);
        if (cinematic && outer > 0.0) {
            // The step's ends in r_s, and the redshift of gas circling at the mid cylindrical radius (M).
            float sa = sqrt(max(0.0, 1.0 - y.z * y.z)), sb = sqrt(max(0.0, 1.0 - ny.z * ny.z));
            vec3 pa = 0.5 / y.x * vec3(sa * cos(phi), sa * sin(phi), y.z), pb = 0.5 / ny.x * vec3(sb * cos(nphi), sb * sin(nphi), ny.z);
            vec3 mid = pa + pb;
            float rho = max(length(mid.xy), inner), x = pow(rho, 1.5), omega = 1.0 / (x + a);
            float ut = (x + a) / (pow(rho, 0.75) * sqrt(max(1.0e-9, x - 3.0 * sqrt(rho) + 2.0 * a)));
            cinematic_step(pa, pb, vec3(0.0, 0.0, 1.0), vec3(1.0, 0.0, 0.0), 1.0 / (ut * k.z * (1.0 - omega * k.x)), time, light, pass);
            if (pass < 0.003) return vec4(light, pass);
        }
        else if (ny.z * y.z < 0.0 && outer > 0.0) {
            float f = y.z / (y.z - ny.z), radius = 1.0 / mix(y.x, ny.x, f);
            if (radius >= inner && radius <= outer) {
                float x = pow(radius, 1.5), omega = 1.0 / (x + a);
                float ut = (x + a) / (pow(radius, 0.75) * sqrt(max(1.0e-9, x - 3.0 * sqrt(radius) + 2.0 * a)));
                float g = 1.0 / (ut * k.z * (1.0 - omega * k.x));
                float azimuth = mix(phi, nphi, f);
                float density = cinematic ? gas_density(0.5 * radius, azimuth, time) : 1.0;
                // |cos| of the ray to the axis: mu' against the whole velocity (-u'/u, -mu', phi') at the equator.
                vec3 velocity = vec3(-mix(y.y, ny.y, f) * radius, mix(y.w, ny.w, f), p1);
                float slant = abs(velocity.y) / max(length(velocity), 1.0e-9);
                float opacity = 1.0 - exp(-(cinematic ? DISK_DEPTH : 1.0e4) * density / max(slant, 0.05));
                light += pass * opacity * disk_color(0.5 * radius, g);
                pass *= 1.0 - opacity;
                images++;
                if (pass < 0.003 || images == 4) return vec4(light, pass);
            }
        }
        y = ny; phi = nphi;
        if (y.x > 1.0 / (1.01 * horizon)) return vec4(light, pass);
        if (y.x < escape && y.y < 0.0) {
            float sn = sqrt(max(1.0e-12, 1.0 - y.z * y.z)), dphi, dsing;
            kerr_rates(a, k, y, dphi, dsing);
            dphi -= k.x / (sn * sn);
            vec3 er = vec3(sn * cos(phi), sn * sin(phi), y.z), et = vec3(y.z * cos(phi), y.z * sin(phi), -sn), ep = vec3(-sin(phi), cos(phi), 0.0);
            sky = normalize(er * (-y.y / y.x) + et * (-y.w / sn) + ep * (sn * dphi));
            origin = er / y.x;
            return vec4(light, pass);
        }
    }
    return vec4(light, pass);
}
// Below this Schwarzschild impact parameter (r_s) rays are traced through the Kerr metric: the disk and a margin.
float kerr_limit() { return max(20.0, 1.25 * disk_outer); }

// The star's glare along the line origin + s dir that bent light left its source by: the same glow as the star's quad,
// in disc radii of the angle between the line and the star seen from the line's start, outside the disc only.
vec3 lensed_glare(vec3 origin, vec3 dir) {
    if (glare_strength <= 0.0) return vec3(0.0);
    vec3 to_star = glare_position - origin;
    float dist = length(to_star);
    float disc = asin(clamp(glare_radius / max(dist, glare_radius * 1.0001), 0.0, 1.0));
    float angle = atan(length(cross(to_star, dir)), dot(to_star, dir));
    float rho = angle / max(disc, 1.0e-9);
    // On the disc the star itself is seen (the quad lies behind it in depth).
    if (rho < 1.0) return vec3(0.0);
    float outside = rho - 1.0;
    float glow = 0.85 * exp(-outside * 1.6) + 0.25 / (1.0 + outside * outside * 0.35) + 0.04 / (1.0 + rho * rho * 0.004);
    // The quad reaches 40 radii (fewer for a disc near 1.2 / 40 rad, as there).
    float extent = min(40.0, 1.2 / max(disc, 1.0e-9));
    float edge = 1.0 - smoothstep(0.7 * extent, extent, rho);
    return glare_color * clamp(glow * edge * glare_strength, 0.0, 1.0);
}
vec3 face_color(vec3 d) {
    vec3 a = abs(d);
    // Camera axes of each face: forward F, up U, right F x U (as SceneRenderer orients the face cameras).
    vec3 f, up;
    int face;
    if (a.x >= a.y && a.x >= a.z) { face = d.x > 0.0 ? 0 : 1; f = vec3(sign(d.x), 0.0, 0.0); up = vec3(0.0, 1.0, 0.0); }
    else if (a.y >= a.z) { face = d.y > 0.0 ? 2 : 3; f = vec3(0.0, sign(d.y), 0.0); up = vec3(0.0, 0.0, sign(d.y)); }
    else { face = d.z > 0.0 ? 4 : 5; f = vec3(0.0, 0.0, sign(d.z)); up = vec3(0.0, 1.0, 0.0); }
    vec3 right = cross(f, up);
    float depth = dot(d, f);
    vec2 uv = vec2(0.5 + 0.5 * dot(d, right) / (depth * face_extent), 0.5 - 0.5 * dot(d, up) / (depth * face_extent));
    if (face == 0) return textureLod(face0, uv, 0.0).rgb;
    if (face == 1) return textureLod(face1, uv, 0.0).rgb;
    if (face == 2) return textureLod(face2, uv, 0.0).rgb;
    if (face == 3) return textureLod(face3, uv, 0.0).rgb;
    if (face == 4) return textureLod(face4, uv, 0.0).rgb;
    return textureLod(face5, uv, 0.0).rgb;
}

void fragment() {
    vec3 d = normalize(v_ray);
    float r = length(lens_position);
    vec3 h = lens_position / r;
    float u = lens_radius / r;
    // This pixel on the screen, for looking bodies up along bent rays (derivatives before any branch).
    vec4 here_clip = PROJECTION_MATRIX * vec4(d, 0.0);
    vec2 here_ndc = here_clip.xy / here_clip.w;
    float flip = dFdy(here_ndc.y) * dFdy(SCREEN_UV.y) < 0.0 ? -1.0 : 1.0;
    float hole_depth = -lens_position.z;
    // A body nearer than the hole hides the lens.
    float here_depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
    if (here_depth > 0.0 && view_depth(here_depth) < hole_depth) discard;
    float kerr_weight = 0.0;
    vec3 kerr_color = vec3(0.0);
    if (hole_spin > 0.0005 && u < 1.0 && dot(disk_normal, disk_normal) > 0.5) {
        float b_rs = length(cross(h, d)) / (u * sqrt(1.0 - u));
        float limit = kerr_limit();
        if (b_rs < limit) {
            // The hole frame: z along the spin (the disk's axis), x the disk's reference; lengths in M = r_s / 2.
            vec3 ez = disk_normal, ex = disk_reference, ey = cross(ez, ex);
            mat3 to_hole = transpose(mat3(ex, ey, ez));
            vec3 sky, origin;
            vec4 traced = kerr_trace(hole_spin, to_hole * (-lens_position / lens_radius * 2.0), to_hole * d, 2.0 * disk_outer, TIME, sky, origin);
            vec3 sky_dir = ex * sky.x + ey * sky.y + ez * sky.z;
            vec3 line = lens_position + (ex * origin.x + ey * origin.y + ez * origin.z) * (0.5 * lens_radius);
            vec3 seen = dot(sky, sky) > 0.5 ? behind_hole(line, sky_dir, face_color(camera_to_sky * sky_dir), PROJECTION_MATRIX, SCREEN_UV,
                here_ndc, flip, hole_depth) : vec3(0.0);
            if (dot(sky, sky) > 0.5) seen += lensed_glare(line, sky_dir);
            kerr_color = traced.rgb + traced.a * seen;
            kerr_weight = 1.0 - smoothstep(0.8 * limit, limit, b_rs);
            if (kerr_weight >= 1.0) { ALBEDO = kerr_color; ALPHA = 1.0; }
        }
    }
    if (kerr_weight < 1.0) {
    // Below 1e-5 r_s / r the whole disk spans less than a pixel.
    vec4 disk = vec4(0.0, 0.0, 0.0, 1.0);
    if (u < 1.0 && u > 1.0e-5 && dot(disk_normal, disk_normal) > 0.5)
        disk = disk_trace(-lens_position / lens_radius, d, disk_normal, disk_reference, TIME);
    vec3 behind = vec3(0.0);
    if (u < 1.0 && disk.a > 0.003) {
        float c = dot(d, h);
        vec3 side = cross(h, d);
        float psi = atan(length(side), c);
        float swept = swept_angle(u, psi, c > 0.0);
        if (swept >= 0.0) {
            if (disk.a >= 0.999 && abs(swept - (PI - psi)) < 0.25 * pixel_angle) discard;
            // The sky lies at the swept angle from the hole-to-observer direction, turned toward the view ray.
            vec3 er = -h, t = d - dot(d, er) * er;
            float tl = length(t);
            t = tl > 1.0e-12 ? t / tl : normalize(cross(er, abs(er.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            vec3 from = cos(swept) * er + sin(swept) * t;
            // The light's straight path far from the hole passes it at the impact parameter (SchwarzschildLens.SourceLine).
            float b = sin(psi) / (u * sqrt(1.0 - u));
            vec3 line = lens_position + (sin(swept) * er - cos(swept) * t) * (b * lens_radius);
            behind = behind_hole(line, from, face_color(camera_to_sky * from), PROJECTION_MATRIX, SCREEN_UV, here_ndc, flip, hole_depth) +
                lensed_glare(line, from);
        }
    }
    ALBEDO = mix(disk.rgb + disk.a * behind, kerr_color, kerr_weight);
    ALPHA = 1.0;
    }
}
