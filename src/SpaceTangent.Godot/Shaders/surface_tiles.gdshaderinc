// SPDX-License-Identifier: GPL-2.0-or-later
// Sparse local NameTemplate pages. Internal rows run south to north; image rows run north to south.
uniform bool native_base_enabled = false;
uniform bool native_normal_enabled = false;
// Set by NativeTiledTexture.Bind. Defaults only keep disabled/unbound materials well-defined.
uniform int native_layers_per_pool = 1;
uniform int native_page_probe_limit = 1;
uniform sampler2DArray native_base_tiles[4] : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2DArray native_normal_tiles[4] : filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D native_base_pages : filter_nearest, repeat_disable;
uniform sampler2D native_normal_pages : filter_nearest, repeat_disable;
uniform int native_base_levels = 1;
uniform int native_normal_levels = 1;
uniform int native_base_size = 307;
uniform int native_normal_size = 307;
uniform int native_base_mask = 0;
uniform int native_normal_mask = 0;
uniform ivec2 native_base_axes = ivec2(1, 1);
uniform ivec2 native_normal_axes = ivec2(1, 1);
uniform float native_base_border = 0.0;
uniform float native_normal_border = 0.0;
uniform int native_normal_encoding = 1; // 1=DXT5nm AG; 2=source ordinary normalized RGB.

struct NativeTileRegion { vec4 rect; vec4 page; };

vec4 native_page(sampler2D pages, int mask, ivec3 key) {
    uint hash = uint(key.y) * 1664525u ^ uint(key.z) * 1013904223u ^ uint(key.x) * 2654435761u;
    int slot = int(hash & uint(mask));
    int width = textureSize(pages, 0).x;
    for (int probe = 0; probe < native_page_probe_limit; probe++) {
        vec4 page = texelFetch(pages, ivec2(slot % width, slot / width), 0);
        if (page.r == 0.0) return vec4(0.0);
        if (ivec3(page.rgb) == ivec3(key.x + 1, key.y, key.z)) return page;
        slot = (slot + 1) & mask;
    }
    return vec4(0.0);
}

NativeTileRegion native_region(sampler2D pages, int mask, int levels, float border, ivec2 axes, ivec3 requested) {
    NativeTileRegion result;
    result.rect = vec4(border, border, 1.0 - border, 1.0 - border);
    result.page = vec4(0.0);
    ivec3 test = requested;
    for (int attempt = 0; attempt < 16; attempt++) {
        if (test.x < levels) result.page = native_page(pages, mask, ivec3(test.x, test.yz * axes));
        if (result.page.a > 0.0) return result;
        test = ivec3(test.x - 1, test.y / 2, test.z / 2);
        vec2 extent = (result.rect.zw - result.rect.xy) * 0.5;
        int low_mask = (1 << (requested.x - test.x)) - 1;
        result.rect.xy = vec2(border) + extent * vec2(requested.yz & ivec2(low_mask));
        result.rect.zw = result.rect.xy + extent;
        if (test.x < 0) break;
    }
    return result;
}

vec4 native_base_sample(int pool, vec3 uv, vec2 dx, vec2 dy) {
    if (pool == 0) return textureGrad(native_base_tiles[0], uv, dx, dy);
    if (pool == 1) return textureGrad(native_base_tiles[1], uv, dx, dy);
    if (pool == 2) return textureGrad(native_base_tiles[2], uv, dx, dy);
    if (pool == 3) return textureGrad(native_base_tiles[3], uv, dx, dy);
    return vec4(1.0);
}

vec4 native_normal_sample(int pool, vec3 uv, vec2 dx, vec2 dy) {
    if (pool == 0) return textureGrad(native_normal_tiles[0], uv, dx, dy);
    if (pool == 1) return textureGrad(native_normal_tiles[1], uv, dx, dy);
    if (pool == 2) return textureGrad(native_normal_tiles[2], uv, dx, dy);
    if (pool == 3) return textureGrad(native_normal_tiles[3], uv, dx, dy);
    return vec4(1.0);
}

void native_tile_samples(vec2 uv, vec2 dx, vec2 dy, out vec4 base, out vec4 normal, out bool base_found, out bool normal_found) {
    int levels = native_base_enabled ? native_base_levels : native_normal_levels;
    float size = float(native_base_enabled ? native_base_size : native_normal_size);
    float footprint = max(length(dx * vec2(2.0, 1.0)), length(dy * vec2(2.0, 1.0)));
    int level = int(clamp(ceil(-log2(max(1e-12, footprint * size))), 0.0, float(levels - 1)));
    ivec2 dimensions = ivec2(2 << level, 1 << level);
    vec2 location = vec2(fract(uv.x), clamp(uv.y, 0.0, 0.99999994)) * vec2(dimensions);
    ivec3 requested = ivec3(level, int(floor(location.x)), dimensions.y - 1 - int(floor(location.y)));
    ivec3 common = requested;
    NativeTileRegion br;
    NativeTileRegion nr;
    br.rect = vec4(0.0, 0.0, 1.0, 1.0); br.page = vec4(0.0);
    nr = br;
    if (native_base_enabled) br = native_region(native_base_pages, native_base_mask, native_base_levels, native_base_border, native_base_axes, common);
    if (native_normal_enabled) nr = native_region(native_normal_pages, native_normal_mask, native_normal_levels, native_normal_border, native_normal_axes, common);
    if (native_base_enabled && native_normal_enabled) {
        float be = br.rect.z - br.rect.x;
        float ne = nr.rect.z - nr.rect.x;
        if (be > ne) {
            for (int i = 0; i < 16; i++) {
                if (common.x == 0 || be <= ne) break;
                common = ivec3(common.x - 1, common.y / 2, common.z / 2);
                br = native_region(native_base_pages, native_base_mask, native_base_levels, native_base_border, native_base_axes, common);
                be = br.rect.z - br.rect.x;
            }
        } else if (ne > be) {
            for (int i = 0; i < 16; i++) {
                if (common.x == 0 || ne <= be) break;
                common = ivec3(common.x - 1, common.y / 2, common.z / 2);
                nr = native_region(native_normal_pages, native_normal_mask, native_normal_levels, native_normal_border, native_normal_axes, common);
                ne = nr.rect.z - nr.rect.x;
            }
        }
    }
    vec4 region = native_base_enabled ? br.rect : nr.rect;
    if (common.x < requested.x) {
        int difference = requested.x - common.x;
        vec2 extent = (region.zw - region.xy) / float(1 << difference);
        region.xy += extent * vec2(requested.yz & ivec2((1 << difference) - 1));
        region.zw = region.xy + extent;
    }
    vec2 local = vec2(fract(location.x), 1.0 - fract(location.y));
    vec2 tile_uv = region.xy + local * (region.zw - region.xy);
    tile_uv.y = 1.0 - tile_uv.y;
    vec2 scale = vec2(dimensions) * (region.zw - region.xy);
    base_found = native_base_enabled && br.page.a > 0.0;
    normal_found = native_normal_enabled && nr.page.a > 0.0;
    base = vec4(1.0); normal = vec4(0.5, 0.5, 1.0, 0.5);
    if (base_found) {
        int page = int(br.page.a) - 1;
        base = native_base_sample(page / native_layers_per_pool, vec3(tile_uv, float(page % native_layers_per_pool)), dx * scale, dy * scale);
    }
    if (normal_found) {
        int page = int(nr.page.a) - 1;
        normal = native_normal_sample(page / native_layers_per_pool, vec3(tile_uv, float(page % native_layers_per_pool)), dx * scale, dy * scale);
    }
}

vec3 native_surface_normal(vec3 radial, vec4 pixel) {
    vec2 encoded = pixel.ag * 2.0 - 1.0;
    vec3 tangent = vec3(encoded, sqrt(max(0.0, 1.0 - dot(encoded, encoded))));
    if (native_normal_encoding == 2) {
        tangent = normalize(pixel.rgb * 2.0 - 1.0);
    }
    vec3 east = cross(vec3(0.0, 1.0, 0.0), radial);
    east = length(east) > 1e-5 ? normalize(east) : normalize(cross(vec3(0.0, 0.0, 1.0), radial));
    vec3 south = -cross(radial, east);
    return normalize(east * tangent.x + south * tangent.y + radial * tangent.z);
}
