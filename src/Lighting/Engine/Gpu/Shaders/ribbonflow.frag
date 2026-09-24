uniform float u_ribbons; // hint_range(2.0, 14.0, 1.0) = 7.0  ribbon count
uniform float u_turbulence; // hint_range(0.1, 3.5, 0.05) = 1.2  ribbon wobble
uniform float u_glow; // hint_range(0.2, 3.0, 0.05) = 1.0  bloom intensity

// Lateral counterpart to the fire shader: ribbons stream horizontally,
// each at its own scroll speed and wobble phase. Where fire is anisotropic
// vertical with a hot base, this is a fan of horizontal streamers with
// per-band hue, glow, and turbulence. Reads as warpdrive / aurora-banner
// rather than a flame.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.7;
    float ribbons = clamp(u_ribbons, 2.0, 14.0);
    float turb = clamp(u_turbulence, 0.1, 3.5);
    float glow = clamp(u_glow, 0.2, 3.0);

    vec3 col = vec3(0.0);

    for (int i = 0; i < 14; i++) {
        float fi = float(i);
        if (fi >= ribbons) break;

        // Even vertical slot per ribbon, then wobbled by fbm so they snake.
        float yPos = (fi + 0.5) / ribbons;

        // Per-ribbon scroll speed varies by hash; alternate every other
        // ribbon to scroll backward so the field looks woven.
        float dir = mod(fi, 2.0) < 0.5 ? 1.0 : -1.0;
        float vScroll = (0.6 + hash21(vec2(fi, 17.0)) * 0.9) * dir;
        float xShift = uv.x + t * vScroll * 0.18;

        // Vertical wobble: a 3-octave fbm gives the gentle snake, a single
        // value-noise tap the faint edge ripple. Both were 5-octave fbm; at
        // these small amplitudes the dropped octaves don't read, and this loop's
        // per-ribbon noise is the shader's dominant cost on the q-series GPU.
        float wobble = (fbm3(vec2(xShift * 1.6, fi * 3.13 + t * 0.25)) - 0.5) * 0.16 * turb;
        wobble += (vnoise(vec2(xShift * 5.0, fi * 7.7 - t * 0.4)) - 0.5) * 0.04 * turb;
        float yLine = yPos + wobble;

        // Soft Gaussian thickness profile, modulated by a sin so the
        // ribbon "breathes" along its length.
        float thickness = 0.025 + 0.018 * (0.5 + 0.5 * sin(xShift * 5.5 + fi * 1.7));
        float dy = uv.y - yLine;
        float ribbon = exp(-dy * dy / (thickness * thickness));

        // Hue rolls along ribbon length and across ribbons so neighbours
        // never share the same colour at the same x.
        vec3 c = tintedPalette(fi / ribbons + xShift * 0.45 + t * 0.05);

        // Outer glow: wider Gaussian, dimmer, additive.
        float halo = exp(-dy * dy / (thickness * thickness * 6.0)) * 0.35 * glow;

        col += c * ribbon;
        col += c * halo;
    }

    // Subtle warm baseline so corners aren't dead-black between ribbons.
    col += tintedPalette(t * 0.1) * 0.04;

    fragColor = vec4(finalize(col * 1.1), 1.0);
}
