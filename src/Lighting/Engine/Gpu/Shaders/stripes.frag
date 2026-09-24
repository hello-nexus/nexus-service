uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.58
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_count; // hint_range(1.0, 10.0, 1.0) = 3.0  stripe pairs across the frame
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  stripe direction in degrees
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 0.02  0 = hard bands, 1 = sine blend
uniform float u_balance; // hint_range(0.05, 0.95, 0.01) = 0.5  width split between the two colours

// Alternating bands. Softness turns the hard band edge into a blend, so one
// shader covers sharp stripes and a repeating gradient.
void main() {
    float t = fract(axis01(u_angle) * max(u_count, 0.5));
    float w = max(u_softness, 0.001) * 0.5;
    float b = clamp(u_balance, 0.05, 0.95);
    float e = smoothstep(b - w, b + w, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
