uniform float u_wind; // hint_range(0.3, 3.0, 0.05) = 1.2  wind speed
uniform float u_density; // hint_range(0.3, 2.5, 0.05) = 1.0  particle density
uniform float u_gusts; // hint_range(0.1, 2.0, 0.05) = 0.8  gust frequency

mat2 rot2(float a) { float s = sin(a), c = cos(a); return mat2(c, -s, s, c); }

// Sandstorm = several layers of anisotropic noise advecting along the
// wind direction. Added since the last pass:
//   1. The wind-angle itself oscillates + is deflected by a low-freq
//      swirl field so the streaks follow curving, chaotic paths instead
//      of parallel lines. Gives a much less mechanical look.
//   2. Every layer gets a domain-warp offset so fbm features twist and
//      fold as they advect - real sand doesn't flow as a uniform grid.
//   3. Palette samples are spread across 0.05..0.82 so the shader is
//      genuinely rainbow-capable (template slot 1). The signature's
//      colorize=0.55 still pulls slot 0 to warm sand.
void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * u_wind * 0.5, 1000.0);
    float dens = clamp(u_density, 0.2, 3.0);
    float gusts = clamp(u_gusts, 0.0, 3.0);

    // Wind direction: base tilt toward lower-right, modulated by a slow
    // global oscillation plus a spatial swirl field (large fbm). The
    // per-pixel deflection means streaks curve through the frame rather
    // than all pointing the same way.
    float windTilt = -0.35 + sin(t * 0.25) * 0.12;
    // perf: swirl is a low-freq warp field, top octaves invisible -> fbm3
    float swirl = (fbm3(uv * 1.3 + vec2(t * 0.08, -t * 0.06)) - 0.5) * 0.75;
    float windAngle = windTilt + swirl;
    mat2 R = rot2(-windAngle);
    vec2 w = R * uv;

    // Palette samples spread across the spectrum so template slot 1
    // (rainbow) actually reads as rainbow. Under the default sand
    // signature (colorize ~0.55), finalize() pulls them all back toward
    // a warm monochrome sand look.
    vec3 palA = tintedPalette(0.05);
    vec3 palB = tintedPalette(0.30);
    vec3 palC = tintedPalette(0.55);
    vec3 palD = tintedPalette(0.82);

    // Warm desert sky backdrop using palA..palB so the gradient itself
    // shifts under hue rotation.
    vec3 bgTop = palA * 0.20;
    vec3 bgBot = palB * 0.38;
    vec3 col = mix(bgTop, bgBot, smoothstep(0.0, 1.0, uv.y));
    // perf: backdrop haze tint, smooth low-detail -> fbm3
    col += palC * fbm3(uv * 1.4 + t * 0.08) * 0.10;

    // -------- Layer 1: broad dust curtain ---------------------------
    vec2 p1 = vec2(w.x * 0.8 - t * 0.35, w.y * 1.8);
    // perf: domain-warp eddies + curtain washed out by the warp -> fbm3 (3 calls)
    p1 += vec2(fbm3(p1 * 0.9 + t * 0.15), fbm3(p1 * 1.1 - t * 0.13)) * 0.8;
    float haze = fbm3(p1);
    haze = smoothstep(0.32, 0.7, haze) * dens;
    col += palA * haze * 0.55;

    // -------- Layer 2: mid streaks ----------------------------------
    vec2 p2 = vec2(w.x * 2.2 - t * 1.0, w.y * 18.0);
    // perf: streak warp offsets + body -> fbm3 (3 calls)
    p2 += vec2(fbm3(p2 * 0.35 + t * 0.1), fbm3(p2 * 0.35 + 9.0 - t * 0.12)) * 0.35;
    float mid = fbm3(p2);
    mid = pow(smoothstep(0.42, 0.85, mid), 1.2) * dens;
    col += palB * mid * 1.05;

    // -------- Layer 3: fine grains ----------------------------------
    vec2 p3 = vec2(w.x * 4.0 - t * 2.2, w.y * 55.0);
    // Small lateral jitter per column so grains don't march in straight
    // horizontal lines across the frame.
    // perf: per-column jitter + grain body -> fbm3 (2 calls)
    p3.y += (fbm3(vec2(w.x * 3.0, t * 0.8)) - 0.5) * 1.5;
    float fine = fbm3(p3);
    fine = pow(smoothstep(0.5, 0.92, fine), 1.5) * dens;
    col += palC * fine * 1.25;

    // -------- Layer 4: hot specks -----------------------------------
    vec2 cellSpace = vec2(w.x * 140.0 - t * 70.0, w.y * 180.0);
    // Micro-oscillation on each cell's y so grains wobble mid-flight.
    cellSpace.y += sin(cellSpace.x * 0.3 + t * 2.0) * 0.4;
    vec2 cellId = floor(cellSpace);
    vec2 cellP = fract(cellSpace) - 0.5;
    float cellH = hash21(cellId);
    float pass = step(0.985 - dens * 0.012, cellH);
    float speck = pass
                * exp(-cellP.x * cellP.x * 6.0)
                * exp(-cellP.y * cellP.y * 800.0)
                * (0.7 + 0.3 * hash21(cellId + 17.0));
    // Random palette slot per speck (multiplied by a wider range so
    // rainbow slot actually shows coloured grains). Mixed toward white
    // so the hottest specks look like catch-light highlights.
    float speckHue = hash21(cellId + 53.0);
    vec3 speckCol = mix(tintedPalette(speckHue * 0.85 + 0.05), vec3(1.0, 0.95, 0.82), 0.3);
    col += speckCol * speck * 1.5;

    // -------- Gust: sweeping bright wall ----------------------------
    float gustPhase = fract(t * gusts * 0.2);
    float gustX = mix(-0.4, 1.4, gustPhase);
    float gustAlong = w.x - gustX;
    // Wobble the gust line so it's not perfectly straight.
    gustAlong += sin(t * 1.3 + w.y * 7.0) * 0.04;
    float gustBand = exp(-gustAlong * gustAlong * 70.0);
    float gustEnv = smoothstep(0.0, 0.08, gustPhase) * smoothstep(1.0, 0.88, gustPhase);
    col += palD * gustBand * gustEnv * dens * 1.8;

    fragColor = vec4(finalize(col), 1.0);
}
