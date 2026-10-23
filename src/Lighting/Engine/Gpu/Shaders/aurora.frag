uniform float u_curtains; // hint_range(1.0, 8.0, 1.0) = 4.0  curtain column density
uniform float u_height; // hint_range(0.2, 1.0, 0.02) = 0.55  how far the curtains hang
uniform float u_shimmer; // hint_range(0.0, 1.0, 0.02) = 0.5  ray flicker and hem shimmer
uniform float u_spread; // hint_range(0.0, 1.0, 0.01) = 0.55  hue spread from hem to top of each curtain
uniform float u_bgLevel; // hint_range(0.0, 1.0, 0.01) = 0.6  background brightness, always tinted
uniform float u_jagged; // hint_range(0.0, 1.0, 0.01) = 0.9  soft rays to hard jagged blocks

// Palette color at offset h, rescaled to full value so the hue reads vivid
// before the post-process.
vec3 curtainColor(float h) {
    vec3 c = tintedPalette(h);
    return 2.1 * c / max(max(c.r, max(c.g, c.b)), 0.001);
}

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.25, 1000.0);
    float dens = clamp((u_curtains - 1.0) / 7.0, 0.0, 1.0);
    float hgt = clamp(u_height, 0.0, 1.0);
    float sway = 0.6;
    float shim = clamp(u_shimmer, 0.0, 1.0);
    float soft = 1.0 - clamp(u_jagged, 0.0, 1.0);
    float glow = 0.55;
    float spread = clamp(u_spread, 0.0, 1.0);

    // Pixel-size guard: column counts are capped so a column never spans fewer
    // than about two pixels, and edges are never thinner than a pixel and a half.
    float pxW = max(u_resolution.x, 1.0);
    float pxH = max(u_resolution.y, 1.0);
    float edgeW = max(mix(0.004, 0.22, soft), 1.5 / pxH);

    float cyc = 0.0;
    
    // Background: the palette shifted toward its neighbor hue and darkened, so
    // every preset gets a fitting colored backdrop that is never black.
    vec3 bgc = curtainColor(cyc + 0.06) * 0.476;
    vec3 bg = bgc * mix(0.1, 0.9, clamp(u_bgLevel, 0.0, 1.0));
    vec3 col = bg;

    // Back layer first; each later layer sits in front of the last.
    for (int i = 0; i < 3; i++) {
        float fi = float(i);
        float seed = fi * 23.7;
        float dir = (mod(fi, 2.0) < 0.5) ? 1.0 : -1.0;
        // Drift: the whole layer slides, each layer at its own pace and
        // direction, so columns pass in front of and behind each other.
        float xs = uv.x + sway * (dir * t * (0.05 + fi * 0.03) + 0.06 * sin(t * (0.4 + fi * 0.17) + fi * 2.0));
        float ncols = min(mix(1.4, 5.0, dens) * (1.0 + fi * 0.5), pxW * 0.45);
        float p = xs * ncols;
        // Warp makes columns different widths while staying monotonic.
        p += 0.32 * sin(p * 0.55 + seed);
        float cx = mix(floor(p) + 0.5, p, soft);
        // Fine step columns hang off the coarse ones, giving the stepped edge.
        float nf = min(ncols * 2.2, pxW * 0.6);
        float pf = xs * nf + 0.3 * sin(xs * nf * 0.6 + seed * 1.3);
        float cf = mix(floor(pf) + 0.5, pf, soft);

        float dc = vnoise(vec2(cx * 1.9 + seed, t * 0.22 + fi * 3.0));
        dc = smoothstep(0.12, 0.88, dc);
        float df = vnoise(vec2(cf * 1.3 + seed * 0.7, t * 0.3 + fi));
        float yb = hgt * 1.15 * (0.4 + 0.6 * dc) * (1.0 - fi * 0.06) + (df - 0.5) * 0.16 * hgt;
        yb = clamp(yb, 0.0, 1.0);

        float d = yb - uv.y;
        float inside = smoothstep(-edgeW * 0.25, edgeW, d);
        float g = clamp(uv.y / max(yb, 0.02), 0.0, 1.0);

        // Per-column brightness, plus a little vertical ray texture inside.
        float cb = 0.8 + 0.2 * hash21(vec2(floor(p), seed));
        float rayN = vnoise(vec2(xs * min(ncols * 6.0, pxW * 0.5) + seed, t * 0.3 + uv.y * 0.6));
        float flick = vnoise(vec2(xs * min(ncols * 8.0, pxW * 0.5) + seed, t * 2.4 + fi * 5.0));
        float tex = 0.82 + 0.3 * (rayN - 0.5) + shim * 0.45 * (flick - 0.5);
        float lum = cb * tex * (0.75 + 0.25 * g) * (0.9 + 0.06 * fi);

        vec3 lc = curtainColor(cyc - spread * 0.6 * (1.0 - g) - fi * 0.012 * spread);
        col = mix(col, lc * lum, clamp(inside * 0.96, 0.0, 1.0));

        // Subtle glow along the hem, inside the column.
        float hw = edgeW * 1.2 + 0.012;
        float hem = exp(-(d * d) / (hw * hw)) * smoothstep(-edgeW, 0.0, d);
        col += mix(lc, vec3(1.0), 0.12) * hem * (0.5 + 0.5 * tex) * glow * 0.6;
    }

    fragColor = vec4(finalize(col), 1.0);
}
