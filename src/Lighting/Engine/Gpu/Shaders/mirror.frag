uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.02
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.6
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  sweep direction in degrees
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 0.6  blend width of the turn

// Symmetric ramp: edge colour in to centre colour and back out again.
void main() {
    float t = abs(axis01(u_angle) * 2.0 - 1.0);
    float w = max(u_softness, 0.001);
    float e = smoothstep(0.5 - w * 0.5, 0.5 + w * 0.5, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
