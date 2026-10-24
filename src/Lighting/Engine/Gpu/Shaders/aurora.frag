uniform float u_curtains; // hint_range(1.0, 8.0, 1.0) = 4.0  number of overlapping curtain bands
uniform float u_height; // hint_range(0.2, 1.0, 0.02) = 0.75  how much of the frame the curtains cover
uniform float u_shimmer; // hint_range(0.0, 1.0, 0.02) = 0.4  high-freq flicker intensity
uniform float u_aHue;  // hint_range(0.0, 1.0, 0.01) = 0.67  center hue of the color range
uniform float u_aSpan; // hint_range(0.0, 1.0, 0.01) = 0.33  share of the wheel the curtains step through
uniform float u_aSat;  // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal;  // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bgLevel; // hint_range(0.0, 1.0, 0.01) = 0.6  sky brightness, always tinted
uniform float u_rays; // hint_range(0.0, 1.0, 0.01) = 0.7  how far and how strongly vertical rays stream down

// Hue of the nth curtain counted from the lowest one. The wheel's range runs
// from its first end at the lowest curtain to its far end at the highest, both
// ends included; the full wheel steps evenly and does not repeat the start.
float bandHueAt(float n, float count) {
    float start = u_aHue - u_aSpan * 0.5;
    if (u_aSpan > 0.99) return u_aHue + n / count;
    if (count < 1.5) return start;
    return start + u_aSpan * n / (count - 1.0);
}

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.4, 1000.0);
    int bands = int(clamp(u_curtains, 1.0, 8.0));
    float h = clamp(u_height, 0.1, 1.0);
    float shim = clamp(u_shimmer, 0.0, 1.0);
    float rays = clamp(u_rays, 0.0, 1.0);
    float aspect = u_resolution.x / max(u_resolution.y, 1.0);

    // Sky: a darker palette color nudged toward its neighbor hue, so every
    // preset gets a colored backdrop that is never black. It brightens a little
    // toward the bottom so the lower frame carries color.
    vec3 sky = hsv2rgb(vec3(u_aHue - u_aSpan * 0.5 - 0.04, u_aSat, 1.0)) * 0.4 * mix(0.15, 0.9, clamp(u_bgLevel, 0.0, 1.0));
    vec3 col = sky * (0.75 + 0.5 * uv.y);

    // Pixel-size guard: ray frequency never goes finer than the canvas can resolve.
    float rayFreq = min(9.0, u_resolution.x * 0.12);
    float shimFreq = min(30.0, u_resolution.x * 0.4);
    float span = 0.2 + 0.62 * h;
    float width = 0.05 + 0.03 * h;
    float invUp = 1.0 / (width * 1.7);
    float invDn = 1.0 / (width * 0.5);

    for (int i = 0; i < 8; i++) {
        if (i >= bands) break;
        float fi = float(i);
        float f01 = (bands > 1) ? fi / float(bands - 1) : 0.0;
        // Ripple: layered sine waves warp each curtain like fabric.
        float freq1 = 2.0 + fi * 0.7;
        float freq2 = 5.0 + fi * 1.3;
        float warp = sin(uv.x * freq1 + t * (0.5 + fi * 0.15) + fi * 0.9) * 0.10
                   + sin(uv.x * freq2 - t * (0.3 + fi * 0.1) + fi * 1.7) * 0.05;
        // Curtain hems are spread over the frame height so no region stays empty.
        float cy = 0.14 + f01 * span + warp;
        float dy = uv.y - cy;
        // Bright lower edge: fast falloff below, long soft fade above.
        float s = (dy < 0.0) ? dy * invUp : dy * invDn;
        float band = exp(-(s * s));
        float hem = exp(-(dy * dy) / (width * width * 0.02));

        // Vertical ray pattern along x, drifting slowly.
        float rn = vnoise(vec2(uv.x * rayFreq + fi * 7.3 + warp * 6.0, t * 0.5 + fi * 2.0));
        float ray = smoothstep(0.22, 0.85, rn);

        float flicker = 1.0;
        if (band > 0.004) {
            flicker = 1.0 + shim * (vnoise(vec2(uv.x * shimFreq + fi * 7.0, t * 4.0 + fi)) - 0.5) * 0.9;
        }
        float body = band * flicker * mix(1.0, 0.45 + 1.1 * ray, rays * 0.7);

        // Color comes from the wheel range, not the global tint. Each band takes its own hue from the wheel's range (the lowest band the
        // first end); a gentle gradient inside the band keeps it soft.
        float k = clamp(-dy * invUp / 1.7, 0.0, 1.0);
        float n = float(bands - 1) - fi;
        float stepHue = (u_aSpan > 0.99) ? 1.0 / float(bands) : u_aSpan / max(float(bands), 2.0);
        float bandHue = bandHueAt(n, float(bands)) + stepHue * 0.5 * k;
        vec3 lc = 1.25 * hsv2rgb(vec3(bandHue, u_aSat, u_aVal));

        // Rays stream down from the hem, fading with distance.
        float len = 0.09 + 0.3 * rays;
        float below = smoothstep(-0.01, 0.03, dy);
        float rayI = rays * ray * exp(-max(dy, 0.0) / len) * below;
        // Boldness: lift the ray core so beams stay bright against the sky.
        rayI = min(rayI * 1.35, 1.0);

        // Alpha-over keeps a front band's own hue where bands overlap and lets the
        // colored sky show through the gaps. Rays carry their band's color.
        float a = clamp(body * 0.95 + rayI * 0.95, 0.0, 1.0);
        float hemI = hem * (0.2 + 0.9 * ray) * (0.6 + 0.4 * flicker);
        vec3 lit = lc * (0.75 + 0.5 * body + 0.35 * rayI) + mix(lc, vec3(1.0), 0.25) * hemI * 0.6;
        col = mix(col, lit, clamp(a + hemI * 0.5, 0.0, 1.0));
    }

    // Background star field: one star per grid cell, placed at a hashed point
    // inside it and drawn as a round core whose radius is set in pixels, so a
    // star stays a pinpoint on a panel and still covers a whole pixel on the
    // small canvas the LED path renders into, where a cell is about one pixel.
    // Jitter keeps stars off the cell edges, which is what lets the falloff be
    // read from its own cell alone instead of sampling the neighbours.
    float cellRows = 90.0;
    vec2 cells = vec2(cellRows * aspect, cellRows);
    vec2 sp = uv * cells;
    vec2 cellId = floor(sp);
    float sh = hash21(cellId);
    float bright = pow(sh, 18.0);
    if (bright > 0.002) {
        vec2 jitter = vec2(hash21(cellId + 7.3), hash21(cellId + 19.1)) * 0.6 + 0.2;
        float cellPx = max(u_resolution.y / cellRows, 1.0);
        float d = length(fract(sp) - jitter) * cellPx;
        float core = max(cellPx * 0.16, 0.7);
        float twinkle = 0.75 + 0.25 * sin(t * 6.0 + sh * 90.0);
        col += vec3(bright * exp(-(d * d) / (core * core)) * twinkle * 0.4) * (1.0 - uv.y * 0.6);
    }
    // tonemap, not finalize: the wheel range owns the color, so the global tint is off.
    fragColor = vec4(tonemap(col), 1.0);
}
