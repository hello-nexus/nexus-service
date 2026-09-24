uniform float u_count; // hint_range(2.0, 10.0, 1.0) = 5.0  number of discrete bands
uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  band direction in degrees
uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0  starting hue
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0  colour saturation
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0  brightness

// Spectrum quantised into flat bands - sharp edges, no blending.
void main() {
    float n = max(floor(u_count), 2.0);
    float band = floor(axis01(u_angle) * n) / n;
    fragColor = vec4(hsv2rgb(vec3(fract(band + u_aHue), clamp(u_aSat, 0.0, 1.0), clamp(u_aVal, 0.0, 1.0))), 1.0);
}
