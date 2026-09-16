uniform float u_angle; // hint_range(0.0, 360.0, 5.0) = 0.0  ramp direction in degrees
uniform float u_density; // hint_range(0.2, 4.0, 0.05) = 1.0  hue cycles across the frame
uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0  starting hue
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0  colour saturation
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0  brightness

// Full-spectrum ramp held still: the rainbow, as a fixed painted strip.
void main() {
    float t = axis01(u_angle) * max(u_density, 0.05) + u_aHue;
    fragColor = vec4(hsv2rgb(vec3(fract(t), clamp(u_aSat, 0.0, 1.0), clamp(u_aVal, 0.0, 1.0))), 1.0);
}
