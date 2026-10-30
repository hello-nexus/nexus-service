uniform float u_freq; // hint_range(2.0, 16.0, 0.5) = 8.0  wave frequency
uniform float u_amp; // hint_range(0.05, 0.4, 0.01) = 0.18  wave amplitude
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Stacked ribbons of color. Layered sine waves with their own phase, rate and
// palette slice are layered back to front; each paints the whole area below its
// crest, brightest at the crest line, so the frame is always filled and the
// waves slide through each other as they travel.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.9;
    float f = max(1.0, u_freq);
    float a = clamp(u_amp, 0.02, 0.45);

    vec3 col = tintedPalette(0.62 + uv.y * 0.25 + t * 0.03) * 0.35;
    for (int i = 0; i < 5; i++) {
        float fi = float(i);
        float k = f * (0.55 + fi * 0.22);
        float dir = (i % 2 == 0) ? 1.0 : -1.0;
        float base = 0.30 + fi * 0.13;
        float h = base + a * sin(uv.x * k - t * (1.6 + fi * 0.5) * dir + fi * 1.7)
                       + a * 0.4 * sin(uv.x * k * 2.1 + t * 1.1 * dir + fi);
        float below = smoothstep(h - 0.01, h + 0.01, uv.y);
        float crest = exp(-abs(uv.y - h) * 38.0);
        float depthFade = 1.0 - clamp((uv.y - h) * 1.7, 0.0, 0.75);
        vec3 tint = tintedPalette(fi * 0.17 + uv.x * 0.22 - t * 0.05);
        col = mix(col, tint * (0.55 + 0.5 * depthFade), below * 0.88);
        col += tint * crest * 0.7 + vec3(crest * crest * 0.35);
    }
    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.31; }
