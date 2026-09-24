uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.5
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_count; // hint_range(1.0, 10.0, 1.0) = 4.0  wedge pairs around the centre
uniform float u_offset; // hint_range(0.0, 1.0, 0.01) = 0.0  rotation of the wedges

// Pie wedges alternating between two colours - sharp radial spokes.
void main() {
    float t = fract((sweep01() + u_offset) * max(floor(u_count), 1.0));
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(t < 0.5 ? ca : cb, 1.0);
}
