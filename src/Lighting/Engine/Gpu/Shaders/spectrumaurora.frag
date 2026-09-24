uniform float u_curtains; // hint_range(2.0, 8.0, 1.0) = 5.0  curtain count
uniform float u_height; // hint_range(0.2, 1.4, 0.05) = 0.75  curtain reach, fraction of the frame
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  bloom spread around each curtain

// Hi-res spectrum, declared here only (see beatbuilder.frag): the 16-band
// u_spectrum is too coarse for a fullscreen curtain, which reads as eight
// visible steps rather than a continuous edge.
uniform float u_spectrum64[64];
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Aurora curtains over a night sky: each curtain hangs from a wandering upper
// edge, brightest along its lower rim, striated by vertical rays. The spectrum
// sets each curtain's local height, so the silhouette follows the music while
// the fbm wander keeps it from reading as a bar chart.

// Continuous read of the 64-band spectrum. x is clamped, never wrapped - a
// wrap puts a hard seam wherever the sample position rolls over.
float spec64At(float x) {
    float f = clamp(x, 0.0, 1.0) * 63.0;
    int i = int(floor(f));
    int j = min(i + 1, 63);
    return mix(u_spectrum64[i], u_spectrum64[j], fract(f));
}

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.4, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();
    int sheets = int(clamp(u_curtains, 2.0, 8.0));
    float reach = clamp(u_height, 0.15, 1.4);
    float glow = clamp(u_glow, 0.2, 3.0);

    // Night sky: a faint cool wash toward the horizon, nowhere near white, so
    // the curtains are the only bright thing in frame.
    vec3 col = tintedPalette(0.58) * 0.035 * smoothstep(0.0, 1.0, uv.y);

    for (int s = 0; s < 8; s++) {
        if (s >= sheets) break;
        float fs = float(s);
        float phase = fs * 2.399;

        // Each curtain owns an evenly spaced slot and sways within it. Letting
        // them travel freely across the frame reads better for a few seconds
        // and then leaves the frame empty whenever the independent rates line
        // up; a gaussian envelope per slot keeps coverage at every phase while
        // still fading at the edges, so nothing seams.
        float slot = (fs + 0.5) / float(sheets);
        float travel = slot + 0.19 * sin(t * (0.055 + fs * 0.013) + phase);
        float width = 0.17 + 0.08 * sin(phase * 1.7);
        float env = exp(-pow((uv.x - travel) / width, 2.0));
        if (env < 0.004) continue;

        float sx = uv.x * 1.7 + fs * 0.37;
        float energy = mix(
            0.34 + 0.20 * sin(t * 0.9 + uv.x * 4.0 + phase),
            spec64At(clamp(uv.x * 0.85 + 0.05, 0.0, 1.0)) * 1.25 + 0.12,
            presence * boost);
        energy *= 1.0 + u_audioLevel * 0.4 * boost;

        // Lower rim wanders per curtain so no two rims align into a band.
        float rim = 0.60 + fs * 0.075
                  + 0.09 * sin(sx * 3.4 + phase)
                  + (fbm3(vec2(sx * 1.5, t * 0.2 + fs * 3.1)) - 0.5) * 0.24;
        float height = clamp((0.26 + energy * 0.55) * reach, 0.08, 1.2);
        float top = rim - height;

        float v = (uv.y - top) / max(height, 1e-3);
        float body = smoothstep(-0.05, 0.5, v) * (1.0 - smoothstep(0.90, 1.02, v));
        body *= pow(clamp(v, 0.0, 1.0), 0.9);

        // Vertical rays: the light-shaft texture that makes it aurora and not
        // fog. Two frequencies so the striation does not read as a screen.
        float rayNoise = fbm3(vec2(sx * 5.0, fs * 7.0));
        float rays = 0.60
                   + 0.28 * sin(sx * 120.0 + rayNoise * 16.0)
                   + 0.12 * sin(sx * 41.0 - t * 0.6 + phase);
        body *= clamp(rays, 0.2, 1.15);
        body *= env;

        // Green at the rim to magenta at the top - the real aurora ramp. The
        // Hue slider walks the whole ramp through tintedPalette.
        vec3 tint = tintedPalette(0.28 + pow(1.0 - clamp(v, 0.0, 1.0), 1.6) * 0.40 + fs * 0.02);
        float amp = (0.95 + energy * 0.85) / (1.0 + fs * 0.45);
        col += tint * body * amp;

        // Hot lower rim, inside the envelope so it cannot draw across frame.
        float rimLine = exp(-pow((uv.y - rim) * (30.0 / glow), 2.0));
        col += mix(tint, vec3(0.8, 1.0, 0.9), 0.45) * rimLine * amp * env * 0.45;
        col += tint * exp(-max(uv.y - rim, 0.0) * (14.0 / glow)) * amp * env * 0.10;
    }

    // Beat lift: the sky brightens briefly on a bass onset.
    col *= 1.0 + u_audioBeat * boost * 0.30;

    // Stars in the dark upper half, twinkling with the high band.
    vec2 grid = uv * vec2(80.0, 45.0);
    vec2 cell = floor(grid);
    float h = hash21(cell);
    if (h > 0.988) {
        vec2 local = fract(grid) - 0.5;
        float star = exp(-dot(local, local) * 140.0);
        float twinkle = 0.55 + 0.45 * sin(t * 4.0 + h * 40.0);
        col += vec3(0.8, 0.85, 1.0) * star * twinkle
               * (0.35 + u_audioHigh * boost * 0.7) * (1.0 - uv.y * 0.7);
    }

    fragColor = vec4(finalize(col), 1.0);
}
