uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.33
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_cHue; // hint_range(0.0, 1.0, 0.01) = 0.62
uniform float u_cSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_cVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  sweep direction in degrees
uniform float u_midpoint; // hint_range(0.0, 1.0, 0.01) = 0.5  position of the middle colour along the sweep

// Three-stop blend: A to B to C across the frame.
void main() {
    float t = axis01(u_angle);
    float m = clamp(u_midpoint, 0.02, 0.98);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    vec3 cc = hsv2rgb(vec3(u_cHue, u_cSat, u_cVal));
    vec3 col = t < m
        ? mix(ca, cb, t / m)
        : mix(cb, cc, (t - m) / (1.0 - m));
    fragColor = vec4(col, 1.0);
}
