uniform float u_pitch; // hint_range(1.0, 6.0, 0.1) = 3.0  helix twist rate
uniform float u_strands; // hint_range(2.0, 4.0, 1.0) = 2.0  strand count
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  strand glow

// Intertwined helical strands spiralling into the vanishing point, with
// rung bands between turns. Depth = 1/r; each strand is a thin bright
// spiral cos(a - z*pitch + offset). Screen-space.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.6;
    float pitch = clamp(u_pitch, 1.0, 6.0);
    float strands = clamp(u_strands, 2.0, 4.0);
    float glow = clamp(u_glow, 0.1, 3.0);

    float r = length(uv) + 1e-3;
    float a = atan(uv.y, uv.x);
    float z = 1.0 / r - t;

    vec3 col = vec3(0.0);
    for (int i = 0; i < 4; i++) {
        if (float(i) >= strands) break;
        float off = 6.28318530 * float(i) / strands;
        float s = cos(a - z * pitch + off);
        float line = pow(max(s, 0.0), 40.0);
        col += tintedPalette(z * 0.05 + float(i) / strands + t * 0.03) * line;
    }
    float rung = pow(0.5 + 0.5 * sin(z * pitch), 30.0);
    col += tintedPalette(z * 0.05 + 0.5) * rung * 0.5;

    col *= glow * smoothstep(0.0, 1.3, r);
    col += vec3(0.95, 0.95, 1.0) * (0.45 / (1.0 + r * r * 24.0));
    fragColor = vec4(finalize(col), 1.0);
}
