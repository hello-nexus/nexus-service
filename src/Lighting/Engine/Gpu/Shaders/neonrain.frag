uniform float u_density; // hint_range(12.0, 80.0, 1.0) = 28.0  rain column density
uniform float u_length; // hint_range(0.03, 0.5, 0.01) = 0.3  streak length

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.45, 1000.0);
    float cols = max(8.0, u_density);
    float len = clamp(u_length, 0.02, 0.6);

    vec3 col = vec3(0.01, 0.005, 0.02);
    // A drop travels past both frame edges before it wraps, so it never pops in or out; the
    // rate is scaled to keep its on-screen speed.
    float span = 1.0 + len * 1.8 + 0.3;
    // One pixel in column units: the edge softness that keeps thin streaks from shimmering.
    float aa = cols / u_resolution.x;
    // Rain streaks.
    float x = floor(uv.x * cols);
    for (int layer = 0; layer < 3; layer++) {
        float fl = float(layer);
        float xOff = fl * 0.33;
        float xi = floor((uv.x + xOff) * cols);
        float colRand = hash21(vec2(xi, 17.0 + fl * 43.0));
        float fall = t * (1.5 + colRand * 2.5 + fl * 0.8);
        float head = fract(fall / span + colRand) * span - 0.15;
        float dy = uv.y - head;
        // Streak: single falloff from the head trailing upward.
        float up = -dy;
        float streak = step(0.0, up) * (1.0 - smoothstep(0.0, len * 1.6, up));
        // Neon tip glow.
        float tip = exp(-dy * dy * 190.0) * 1.1;
        float xLocal = fract((uv.x + xOff) * cols) - 0.5;
        float xMask = 1.0 - smoothstep(0.17, 0.3 + aa, abs(xLocal));
        vec3 tint = tintedPalette(colRand * 0.6 + fl * 0.1);
        float depth = 1.0 - fl * 0.22;
        col += tint * (streak * 0.75 + tip) * xMask * depth;
    }
    fragColor = vec4(finalize(col), 1.0);
}
