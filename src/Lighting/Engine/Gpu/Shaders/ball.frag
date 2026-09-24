uniform float u_size; // hint_range(0.03, 0.25, 0.005) = 0.08  ball radius
uniform float u_count; // hint_range(1.0, 24.0, 1.0) = 8.0  ball count
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed;
    float s = clamp(u_size, 0.02, 0.4);
    int count = int(clamp(u_count, 1.0, 24.0));
    vec3 col = vec3(0.0);
    // Each ball drifts horizontally with its own frequency/phase and
    // bounces vertically. abs(sin)^0.55 spreads the dwell at the peak
    // so the arc reads as gravity rather than a pure sine wave. In our
    // uvCentered() space y = +1 is the BOTTOM of the frame, so the
    // floor sits near y = +0.85 and the apex near y = -0.9.
    for (int i = 0; i < 24; i++) {
        if (i >= count) break;
        float fi = float(i);
        float xFreq   = 0.35 + fract(fi * 0.173) * 0.9;
        float xPhase  = fi * 1.37;
        float x       = 1.6 * sin(t * xFreq + xPhase);
        float yFreq   = 0.9  + fract(fi * 0.241) * 1.1;
        float yPhase  = fi * 0.83;
        // hop = 0 on the floor, 1 at the apex.
        float hop     = pow(abs(sin(t * yFreq + yPhase)), 0.55);
        float y       = mix(0.85, -0.9, hop);
        // Squash near the floor (hop close to 0): lower squash value ->
        // ballUv.y expands -> ball reads as shorter & wider. At the
        // apex (hop near 1) squash = 1.0 so the ball stays round.
        float squash  = mix(0.5, 1.0, smoothstep(0.0, 0.2, hop));
        vec2 ballUv   = (uv - vec2(x, y)) / vec2(1.0, squash);
        float d       = length(ballUv);
        float core    = smoothstep(s, s * 0.6, d);
        float glow    = smoothstep(s * 3.0, 0.0, d);
        vec3 tint     = tintedPalette(fi * 0.13 + t * 0.02);
        col += tint * core + tint * glow * 0.35;
    }
    // A subtle ground glow at the bottom so the floor is readable at low ball counts.
    float floorGlow = smoothstep(0.85, 1.05, uv.y) * 0.15;
    col += vec3(floorGlow) * tintedPalette(0.7);
    fragColor = vec4(finalize(col * 1.3), 1.0);
}
