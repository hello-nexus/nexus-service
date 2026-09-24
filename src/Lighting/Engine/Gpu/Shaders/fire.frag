uniform float u_turbulence; // hint_range(1.0, 3.0, 0.05) = 1.6  flame turbulence

// Anisotropic flame field: tall not square, scrolls upward over time,
// sampled at a low-freq domain-warped coordinate so tongues lick
// organically instead of marching in vertical stripes. Two extra octaves
// give the high-frequency detail that makes individual flame tips read
// as flame and not blob.
float flame(vec2 uv, float t, float turb) {
    vec2 q = vec2(uv.x * 2.5 * turb, uv.y * 1.6 * turb + t * 1.5);
    // perf: warp offsets are low-freq, top octaves washed out -> fbm3 (3 oct)
    vec2 w = vec2(
        fbm3(q * 0.55 + vec2(0.0,  t * 0.20)) - 0.5,
        fbm3(q * 0.55 + vec2(7.3, -t * 0.25)) - 0.5
    ) * 0.65;
    // perf: drop the 5x detail octave; base fbm + one 2.2x detail keeps tongues
    float n = fbm(q + w);
    n += fbm3((q + w) * 2.2 + vec2(0.0, t * 0.6)) * 0.50;
    return n / 1.40;
}

void main() {
    // uv01() returns top-left origin: y=0 at the TOP, y=1 at the BOTTOM
    // of the rendered frame. The fire's base sits at uv.y=1 and tongues
    // climb toward uv.y=0.
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 1.0, 1000.0);
    // Min raised to 1.0 so even the bottom of the slider is visibly
    // turbulent - the old 0.3 floor read as near-laminar.
    float turb = clamp(u_turbulence, 1.0, 4.0);

    float n = flame(uv, t, turb);

    // Density mask: full fire at the base (uv.y=1), tapers up. Subtract
    // the height-from-base ((1 - uv.y) is "how far from the bottom") from
    // the noise so only the strongest noise spikes survive higher up.
    float density = n * 1.8 - (1.0 - uv.y) * 1.6 + 0.55;
    density = clamp(density, 0.0, 1.2);

    // Heat ramp: black -> dim red -> orange -> yellow -> white core.
    // Sampling tintedPalette across 0.97..0.13 walks the warm half of the
    // rainbow without ever crossing into green/blue, so under any user
    // hue offset the fire still reads fire-shaped.
    vec3 col = vec3(0.0);
    col = mix(col, tintedPalette(0.97) * 0.7,  smoothstep(0.04, 0.22, density));
    col = mix(col, tintedPalette(0.04) * 1.3,  smoothstep(0.22, 0.50, density));
    col = mix(col, tintedPalette(0.12) * 1.8,  smoothstep(0.50, 0.78, density));
    // White-hot core: deliberately overrange so the soft-knee tonemap in
    // finalize() rolls it off into a believable bloom rather than clipping.
    col = mix(col, vec3(1.5, 1.4, 1.05),       smoothstep(0.78, 1.05, density));

    // Always-on coal bed at the very base so the fire feels grounded
    // even where the noise dips. The uv.y^5 falloff keeps the glow from
    // bleeding more than ~25% up the frame.
    float coal = pow(uv.y, 5.0);
    col += tintedPalette(0.07) * coal * (0.7 + n * 0.6);

    // Subtle low-frequency horizontal heat shimmer in the bottom strip.
    // Reads as the haze of hot air directly above the fuel bed.
    float shimmer = sin(uv.x * 25.0 + t * 6.0) * pow(uv.y, 4.0) * 0.18;
    col += vec3(shimmer, shimmer * 0.3, 0.0);

    fragColor = vec4(finalize(col), 1.0);
}
