uniform float u_columns; // hint_range(8.0, 60.0, 1.0) = 28.0  column count (density)
uniform float u_fade; // hint_range(1.0, 12.0, 0.5) = 4.0  tail fade exponent
// Three independent streaks fall down every column at their own speed, phase
// and tail length, so the rain stays dense without changing the column grid.
void main() {
    vec2 uv = uv01();
    float cols = max(4.0, u_columns);
    float fade = max(0.5, u_fade);
    float x = floor(uv.x * cols);
    float xLocal = fract(uv.x * cols) - 0.5;
    float lane = smoothstep(0.4, 0.18, abs(xLocal));
    float columnHue = hash21(vec2(x, 91.1));
    // Each column picks its own hue from a hash of the column index, so at
    // hue=0 colorize=0 the whole screen is multi-colour rain (full rainbow).
    // The signature slot wires hue=0.33 colorize=0.75 which collapses every
    // column toward classic Matrix green via finalize().
    vec3 tint = hsv2rgb(vec3(fract(columnHue + u_hue), 1.0, 1.0));
    float intensity = 0.0;
    float head = 0.0;
    for (int k = 0; k < 3; k++) {
        float fk = float(k);
        float colRand = hash21(vec2(x, 13.37 + fk * 17.11));
        float fall = u_time * u_speed * (0.3 + colRand * 0.8);
        float headY = fract(fall + colRand + fk * 0.33);
        float dy = fract(headY - uv.y);
        // Streak k gets its own tail length (fade exponent scaled per streak).
        float f = fade * (0.8 + 0.5 * hash21(vec2(x, 29.7 + fk * 5.3)));
        float s = pow(1.0 - dy, f);
        s *= 0.55 + 0.45 * hash21(vec2(x, floor(uv.y * 32.0 - fall * 32.0) + fk * 41.0));
        // A few streaks per column are absent at a time, never whole columns.
        s *= step(0.12, hash21(vec2(x, 7.91 + fk * 3.7)));
        intensity = max(intensity, s);
        head = max(head, pow(1.0 - dy, f * 6.0));
    }
    vec3 col = tint * intensity * lane;
    col += mix(vec3(1.0), tint, 0.3) * head * lane;
    fragColor = vec4(finalize(col), 1.0);
}
