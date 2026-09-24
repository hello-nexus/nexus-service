uniform float u_stripes; // hint_range(3.0, 16.0, 1.0) = 7.0  diagonal stripe count
uniform float u_flash; // hint_range(0.3, 2.5, 0.05) = 1.2  beat flash intensity
uniform float u_chroma; // hint_range(0.0, 1.0, 0.05) = 0.5  chroma shift amount
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Full-frame colour strobe. Idle: diagonal stripes of shifting hue slide
// across the frame. Audio: bass beats snap a new hue and blow out the
// brightness; mid/high energy drives stripe speed + chromatic fringing.
void main() {
    vec2 uv = uvCentered();
    int stripes = int(clamp(u_stripes, 2.0, 20.0));
    float flashAmt = clamp(u_flash, 0.1, 3.0);
    float chroma = clamp(u_chroma, 0.0, 1.5);
    float t = u_time * u_speed;
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();

    // Stripe coordinate: rotated to a diagonal so stripes read as motion
    // not static bands. Stripe speed accelerates with u_audioMid so the
    // whole frame feels faster during loud sections.
    float angle = 0.35 + u_audioBeat * boost * 0.5;
    vec2 p = vec2(
        uv.x * cos(angle) - uv.y * sin(angle),
        uv.x * sin(angle) + uv.y * cos(angle)
    );
    float stripeSpeed = 0.4 + u_audioMid * 2.4 * boost;
    float s = p.x * float(stripes) + t * stripeSpeed;
    float stripeId = floor(s);
    float within = fract(s);

    // Per-stripe colour: hash hash21 gives an initial palette offset,
    // then every bass beat snaps the whole frame's palette to a new
    // position so the colour explicitly changes on beat.
    float beatPhase = floor(t * 0.9) + u_audioBeat * 30.0 * boost;
    float stripeHue = fract(hash21(vec2(stripeId, 1.7)) +
                            t * 0.08 +
                            beatPhase * 0.17);
    vec3 stripeColor = tintedPalette(stripeHue);

    // Stripe profile: bright band with soft edges.
    float bandCentre = 1.0 - abs(within * 2.0 - 1.0);
    float band = smoothstep(0.2, 0.9, bandCentre);

    // Chromatic aberration per beat: slightly offset R/G/B samples so the
    // stripes have coloured fringes when audio is hot.
    float ca = chroma * (0.02 + u_audioBeat * 0.1 * boost);
    float bandR = smoothstep(0.2, 0.9, 1.0 - abs(fract(s + ca) * 2.0 - 1.0));
    float bandB = smoothstep(0.2, 0.9, 1.0 - abs(fract(s - ca) * 2.0 - 1.0));

    vec3 col = vec3(
        stripeColor.r * bandR,
        stripeColor.g * band,
        stripeColor.b * bandB
    );

    // Global brightness pumping: the whole frame brightens with the RMS
    // level so quiet passages are dim and loud passages are searing.
    float lift = 0.3 + u_audioLevel * 1.2 * boost;
    col *= lift;

    // Beat flash: full-frame white spike on each beat, shaped by
    // u_audioBeat's exponential decay envelope.
    float flashFalloff = exp(-length(uv) * 1.8);
    col += vec3(1.0) * u_audioBeat * flashAmt * boost * 0.5 * flashFalloff;

    // Idle palette wash so the background never goes black.
    col += tintedPalette(0.45 + t * 0.15) * (0.12 - presence * 0.08);

    // Thin horizon line that glows with bass - a simple but eye-catching
    // full-frame bass indicator.
    float horizon = exp(-abs(uv.y) * (20.0 - u_audioBass * 8.0 * boost));
    col += stripeColor * horizon * (0.15 + u_audioBass * 1.2 * boost);

    fragColor = vec4(finalize(col), 1.0);
}
