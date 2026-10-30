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

// A flame color stop: the authored saturated color rotated by the Hue slider
// around the grey axis, or the album-art palette sample when art colors are on.
vec3 flameStop(vec3 c, float artT) {
    vec3 stops = u_palette[0] + u_palette[1] + u_palette[2] + u_palette[3];
    if (u_paletteMix > 0.5 && dot(stops, vec3(1.0)) > 0.0) {
        return artPalette(artT) * 1.4;
    }
    float ca = cos(u_hue * 6.28318);
    float sa = sin(u_hue * 6.28318);
    vec3 k = vec3(0.57735);
    return c * ca + cross(k, c) * sa + k * dot(k, c) * (1.0 - ca);
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

    // Heat ramp: black -> deep red -> orange -> yellow -> pale-yellow tip.
    // Three saturated layers keep red, orange and yellow distinct; only the
    // very hottest pixels reach the near-white tip. Stops stay within a
    // modest range so the tonemap barely has to compress them.
    vec3 col = vec3(0.0);
    col = mix(col, flameStop(vec3(0.62, 0.03, 0.0), 0.97), smoothstep(0.04, 0.30, density));
    col = mix(col, flameStop(vec3(1.00, 0.36, 0.02), 0.04), smoothstep(0.30, 0.62, density));
    col = mix(col, flameStop(vec3(1.10, 0.74, 0.05), 0.12), smoothstep(0.62, 0.95, density));
    col = mix(col, vec3(1.25, 1.05, 0.40),                  smoothstep(1.08, 1.20, density));

    // Always-on coal bed at the very base so the fire feels grounded
    // even where the noise dips. The uv.y^5 falloff keeps the glow from
    // bleeding more than ~25% up the frame.
    float coal = pow(uv.y, 5.0);
    col += flameStop(vec3(1.00, 0.30, 0.02), 0.07) * coal * (0.3 + n * 0.3);

    // Subtle low-frequency horizontal heat shimmer in the bottom strip.
    // Reads as the haze of hot air directly above the fuel bed.
    float shimmer = sin(uv.x * 25.0 + t * 6.0) * pow(uv.y, 4.0) * 0.18;
    col += vec3(shimmer, shimmer * 0.3, 0.0);

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.41; }
