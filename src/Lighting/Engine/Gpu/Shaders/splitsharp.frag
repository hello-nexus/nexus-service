uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.62
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  split direction in degrees
uniform float u_position; // hint_range(0.0, 1.0, 0.01) = 0.5  where the edge sits along the sweep

// Hard two-tone split, no blend at all.
void main() {
    float t = axis01(u_angle);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(t < u_position ? ca : cb, 1.0);
}
