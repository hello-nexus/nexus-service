uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_size; // hint_range(2.0, 10.0, 1.0) = 4.0  cells across the frame

// Two-colour checkerboard.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01();
    p.x *= ar;
    vec2 cell = floor(p * max(u_size, 1.0));
    float odd = mod(cell.x + cell.y, 2.0);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(odd < 0.5 ? ca : cb, 1.0);
}
