uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.58
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.88
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  sweep direction in degrees
uniform float u_midpoint; // hint_range(0.0, 1.0, 0.01) = 0.5  where the blend sits along the sweep
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 1.0  0 = hard edge, 1 = full-frame blend

// Two-colour linear blend. Softness collapses the transition toward a hard
// edge, so this covers both a smooth gradient and a soft-edged split.
void main() {
    float t = axis01(u_angle);
    float w = max(u_softness, 0.001) * 0.5;
    float e = smoothstep(u_midpoint - w, u_midpoint + w, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
