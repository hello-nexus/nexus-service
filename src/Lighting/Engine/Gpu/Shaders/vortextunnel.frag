uniform float u_twist; // hint_range(0.0, 2.0, 0.05) = 0.8  spiral twist
uniform float u_churn; // hint_range(0.2, 2.0, 0.05) = 1.0  wall flow turbulence
uniform float u_depth; // hint_range(0.4, 3.0, 0.05) = 1.2  recede rate

// Liquid tunnel: smooth flowing tube walls. Depth = 1/r. The wall is
// driven by integer-harmonic angular sines (seamless around the tube, no
// radial seam) modulating depth bands, tinted iridescent. Analytic, so
// softer and cheaper than the geometric hex/crystal tunnels.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    float twist = clamp(u_twist, 0.0, 2.5);
    float churn = clamp(u_churn, 0.1, 2.5);
    float depth = clamp(u_depth, 0.3, 3.0);

    float r = length(uv) + 1e-3;
    float a = atan(uv.y, uv.x);
    float z = (1.0 / r) * depth - t;
    a += twist * z * 0.15;

    float churnPhase = sin(a * 3.0 + z * 1.2 + t) + 0.5 * sin(a * 5.0 - z * 0.7 + t * 0.8);
    float wall = 0.5 + 0.5 * sin(z * 3.5 + churnPhase * churn);

    vec3 col = tintedPalette(z * 0.08 + churnPhase * 0.08 + t * 0.04);
    col *= 0.2 + 0.9 * wall;
    col *= smoothstep(0.0, 1.4, r);
    col += vec3(0.9, 0.95, 1.0) * (0.5 / (1.0 + r * r * 20.0));
    fragColor = vec4(finalize(col), 1.0);
}
