uniform float u_lights; // hint_range(5.0, 30.0, 1.0) = 16.0  light count
uniform float u_size; // hint_range(0.05, 0.3, 0.01) = 0.15  bokeh size
uniform float u_drift; // hint_range(0.1, 2.0, 0.05) = 1.0  drift speed

// Soft circular blurred lights drifting like nighttime city through a
// rainy window. Each light is a gaussian disc whose centre wobbles
// slowly; many overlapping create a dreamy out-of-focus look.
//
// Every angular rate below is a whole multiple of 1/20, so each sine is back
// at its starting phase when the shared clock wraps at 40 pi: the wrap that
// keeps the clock small is invisible instead of a once-per-loop jump.
const float WRAP = 125.6637;
float rate(float h, float lo, float hi) { return floor(mix(lo, hi, h)) * 0.05; }

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * u_drift * 0.7, WRAP);
    int N = int(clamp(u_lights, 4.0, 40.0));
    float sz = clamp(u_size, 0.03, 0.4);

    // Gradient backdrop - the "rainy window behind city lights" look.
    // Without this the large gaps between lights read as solid black.
    float vignette = 1.0 - length(uv) * 0.35;
    vec3 col = tintedPalette(0.55 + uv.y * 0.12) * (0.14 + vignette * 0.08);
    // Diffuse light haze so every pixel carries some colour from nearby bokeh.
    // perf: diffuse haze is heavily blurred -> fbm3
    float haze = fbm3(uv * 0.8 + 0.8 * vec2(sin(t * 0.2 + 1.0), cos(t * 0.3)));
    col += tintedPalette(0.3 + haze * 0.4) * haze * 0.12;

    for (int i = 0; i < 40; i++) {
        if (i >= N) break;
        float fi = float(i);
        vec2 home = vec2(
            (hash21(vec2(fi, 1.0)) - 0.5) * 3.6,
            (hash21(vec2(fi, 2.0)) - 0.5) * 2.4
        );
        // A wide orbit plus a smaller, faster one, so the path wanders instead
        // of tracing a single ellipse.
        vec2 drift = vec2(
            sin(t * rate(hash21(vec2(fi, 3.0)), 6.0, 17.0) + fi * 1.7),
            cos(t * rate(hash21(vec2(fi, 4.0)), 5.0, 14.0) + fi * 2.3)
        ) * 0.55 + vec2(
            sin(t * rate(hash21(vec2(fi, 7.0)), 10.0, 25.0) + fi * 0.9),
            cos(t * rate(hash21(vec2(fi, 8.0)), 9.0, 23.0) + fi * 3.1)
        ) * 0.22;
        vec2 pos = home + drift;
        float d = length(uv - pos);
        // Wider halo + bright core.
        float breathe = 1.0 + 0.2 * sin(t * rate(hash21(vec2(fi, 9.0)), 4.0, 12.0) + fi * 2.9);
        float glow = 0.85 + 0.15 * sin(t * rate(hash21(vec2(fi, 10.0)), 8.0, 20.0) + fi * 1.3);
        float lightSize = sz * (0.8 + hash21(vec2(fi, 5.0)) * 0.7) * breathe;
        float bokeh = exp(-d * d / (lightSize * lightSize));
        float farHalo = exp(-d * d / (lightSize * lightSize * 4.0)) * 0.35;
        float sparkle = pow(bokeh, 6.0);

        vec3 lightColor = tintedPalette(hash21(vec2(fi, 6.0)) * 0.6 + fi * 0.04);
        col += lightColor * (bokeh * 0.9 + farHalo) * glow;
        col += vec3(1.0, 0.95, 0.9) * sparkle * 0.5;
    }

    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.23; }
