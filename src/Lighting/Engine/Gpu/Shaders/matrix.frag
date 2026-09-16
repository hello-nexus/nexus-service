uniform float u_columns; // hint_range(8.0, 60.0, 1.0) = 28.0  column count (density)
uniform float u_fade; // hint_range(1.0, 12.0, 0.5) = 4.0  tail fade exponent
void main() {
    vec2 uv = uv01();
    float cols = max(4.0, u_columns);
    float fade = max(0.5, u_fade);
    float x = floor(uv.x * cols);
    float colRand = hash21(vec2(x, 13.37));
    float fall = u_time * u_speed * (0.3 + colRand * 0.8);
    float headY = fract(fall + colRand);
    float dy = fract(headY - uv.y);
    float intensity = pow(1.0 - dy, fade);
    intensity *= 0.55 + 0.45 * hash21(vec2(x, floor(uv.y * 32.0 - fall * 32.0)));
    float columnPresent = step(0.35, hash21(vec2(x, 7.91)));
    intensity *= columnPresent;
    float xLocal = fract(uv.x * cols) - 0.5;
    intensity *= smoothstep(0.4, 0.18, abs(xLocal));
    // Each column picks its own hue from a hash of the column index, so at
    // hue=0 colorize=0 the whole screen is multi-colour rain (full rainbow).
    // The signature slot wires hue=0.33 colorize=0.75 which collapses every
    // column toward classic Matrix green via finalize().
    float columnHue = hash21(vec2(x, 91.1));
    vec3 tint = hsv2rgb(vec3(fract(columnHue + u_hue), 1.0, 1.0));
    vec3 col = tint * intensity;
    col += mix(vec3(1.0), tint, 0.3) * pow(1.0 - dy, fade * 6.0)
         * smoothstep(0.4, 0.18, abs(xLocal)) * columnPresent;
    fragColor = vec4(finalize(col), 1.0);
}
