uniform float u_wavelength; // hint_range(0.05, 1.0, 0.01) = 0.22  crest spacing (smaller = denser pattern)
uniform float u_sources; // hint_range(2.0, 8.0, 1.0) = 5.0  source count

// Physical wave interference: sum N circular cos() waves from drifting
// source points. Map the wave intensity (|total|) to brightness so
// cancellation reads as black and constructive peaks pop as bright
// bands - the classic ripple-tank look. The phase still drives palette
// position so each band is a different hue.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 1.1;
    float wl = max(0.04, u_wavelength);
    int N = int(clamp(u_sources, 2.0, 8.0));

    float total = 0.0;
    for (int i = 0; i < 8; i++) {
        if (i >= N) break;
        float idx = float(i) / float(N);
        float ang = idx * 6.28318530718 + t * 0.15;
        vec2 src = vec2(cos(ang), sin(ang)) *
                   (0.65 + 0.22 * sin(t * 0.4 + idx * 3.0));
        float d = distance(uv, src);
        total += cos(d / wl - t);
    }

    // |total|/N sits in [0,1]. The pow() gamma lifts mid-range so most
    // of the frame reads as visible interference instead of muddy mid-gray.
    float intensity = pow(abs(total) / float(N), 0.7);
    // Palette position uses both intensity AND phase so bright bands
    // cycle through colours as they shift across the frame.
    vec3 col = tintedPalette(intensity * 0.5 + total * 0.13 + t * 0.05);
    col *= 0.18 + 1.7 * intensity;

    // Bright white spike on extreme constructive interference.
    float spike = smoothstep(0.85, 1.0, intensity);
    col += vec3(0.85, 0.9, 1.0) * spike * 0.6;

    fragColor = vec4(finalize(col), 1.0);
}
