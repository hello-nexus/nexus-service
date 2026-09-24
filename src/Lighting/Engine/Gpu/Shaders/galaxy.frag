uniform float u_arms; // hint_range(1.0, 6.0, 1.0) = 4.0  spiral arm count
uniform float u_dust; // hint_range(0.0, 1.5, 0.05) = 0.8  nebulosity amount
uniform float u_rotation; // hint_range(0.05, 2.0, 0.05) = 0.5  spin rate multiplier
uniform float u_stars; // hint_range(0.0, 2.5, 0.05) = 1.2  star density

// Spiral galaxy: log-spiral arm density + dust nebulosity + individually
// rendered star points with sub-cell jitter, soft gaussian cores, cross
// spikes, and per-star twinkle. The old implementation lit whole grid
// cells white, which read as pixelated squares at canvas resolution; we
// now sample a 3x3 neighbourhood per pixel so the stars are sub-pixel
// points regardless of output size.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.1, 1000.0);
    float arms = clamp(u_arms, 1.0, 8.0);
    float dust = clamp(u_dust, 0.0, 2.0);
    float spin = clamp(u_rotation, 0.0, 3.0);
    float starAmt = clamp(u_stars, 0.0, 3.0);

    float r = length(uv) + 0.001;
    float a = atan(uv.y, uv.x);

    // Log-spiral arm pattern.
    float spiralArg = arms * a + log(r) * 5.0 + t * spin * 4.0;
    float armBand = 0.5 + 0.5 * sin(spiralArg);
    armBand = pow(armBand, 2.5);

    // Disc fade: bright core, fades into the background at the edges.
    float disc = exp(-r * 1.4);
    float armBright = armBand * disc;

    // Dust nebulosity: fbm folded onto the arm pattern so colour pools
    // follow the spirals instead of being uniform noise.
    // perf: fbm3 (3 octaves) - blended in at low weight, so the dropped top
    // octaves are invisible.
    float dustNoise = fbm3(uv * 2.4 + vec2(t * 0.3, -t * 0.2));
    vec3 armColor = tintedPalette(0.55 + r * 0.15 + dustNoise * 0.2);
    vec3 dustColor = tintedPalette(0.78 + dustNoise * 0.15) * dust;

    vec3 col = armColor * armBright * 1.4;
    col += dustColor * armBand * disc * 0.7;

    // --- Stars ---------------------------------------------------------
    // Cell grid at ~16 stars-per-unit-uv. Each cell randomly spawns a
    // sub-cell-jittered point. The 3x3 neighbour loop means points near
    // cell boundaries render correctly in neighbouring pixels too, so
    // the stars never clip to their cell.
    float starDensity = 16.0;
    vec2 starUV = uv * starDensity;
    vec2 cellFloor = floor(starUV);
    vec2 cellFract = fract(starUV);
    float stars = 0.0;
    float threshold = 0.94 - starAmt * 0.04;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            vec2 n = vec2(float(i), float(j));
            vec2 cellId = cellFloor + n;
            float sh = hash21(cellId);
            if (sh <= threshold) continue;
            vec2 jit = vec2(hash21(cellId + 13.0), hash21(cellId + 29.0)) - 0.5;
            vec2 cellP = cellFract - 0.5 - n - jit * 0.7;
            float d = length(cellP);
            // Soft core + faint horizontal/vertical spikes so each star reads
            // as a crisp bright pinpoint rather than a square.
            float core = exp(-d * d * 80.0);
            // perf: fold each spike's two exp() into one (exp(a)*exp(b)=exp(a+b)) - 4 exp -> 2.
            float ax = abs(cellP.x), ay = abs(cellP.y);
            float spikeH = exp(-(ax * 8.0 + ay * 120.0));
            float spikeV = exp(-(ay * 8.0 + ax * 120.0));
            // Brightness scaled by how far above threshold this cell hashed,
            // so most stars are dim and a few are bright (real sky feel).
            float bright = (sh - threshold) / max(0.01, 1.0 - threshold);
            bright = pow(bright, 0.8);
            float twinkle = 0.6 + 0.4 * sin(t * 2.5 + sh * 37.0);
            stars += (core + (spikeH + spikeV) * 0.15) * bright * twinkle;
        }
    }
    // Stars are brighter inside the galactic disc, present but fainter in
    // the surrounding sky so we get a proper star-field spread instead of
    // a hard edge.
    float starMask = mix(0.25, 1.3, smoothstep(1.8, 0.2, r));
    col += vec3(1.0, 0.95, 0.85) * stars * starMask * starAmt;

    // Dim background tint so corners aren't pure black.
    col += tintedPalette(0.72) * 0.04;

    fragColor = vec4(finalize(col), 1.0);
}
