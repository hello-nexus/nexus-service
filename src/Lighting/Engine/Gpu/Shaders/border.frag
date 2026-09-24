uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.55
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_thickness; // hint_range(0.02, 0.5, 0.01) = 0.18  frame width as a fraction of the short edge
uniform float u_softness; // hint_range(0.0, 1.0, 0.01) = 0.05  0 = hard frame, 1 = faded

// Edge colour framing a centre colour. Reads as an outline on a strip and as a
// border on a matrix.
void main() {
    vec2 p = uv01();
    float d = min(min(p.x, 1.0 - p.x), min(p.y, 1.0 - p.y));
    float w = max(u_softness, 0.001) * 0.25;
    float e = smoothstep(u_thickness - w, u_thickness + w, d);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(cb, ca, e), 1.0);
}
