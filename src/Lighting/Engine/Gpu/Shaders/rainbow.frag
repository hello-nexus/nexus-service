uniform float u_density; // hint_range(0.2, 3.0, 0.05) = 1.0  how many band repeats across the frame
uniform float u_rotation; // hint_range(0.0, 360.0, 5.0) = 0.0  band direction in degrees

// Painted-strip spectrum: parallel hue bands sliding across the frame.
// The rotation slider rotates the band direction; 0 = vertical bands
// scrolling horizontally, 90 = horizontal bands scrolling vertically.
void main() {
    vec2 uv = uvCentered();
    float dens = max(0.001, u_density);
    // Convert degrees to radians and rotate the sample axis.
    float a = u_rotation * 0.01745329;
    float pos = uv.x * cos(a) - uv.y * sin(a);
    // Map pos into roughly [0,1] for 16:9 aspect so dens=1 still gives one
    // full hue cycle across the frame regardless of rotation.
    float h = (pos * 0.5 + 0.5) * dens - u_time * u_speed * 0.15 + u_hue;
    vec3 col = hsv2rgb(vec3(h, 1.0, 1.0));
    fragColor = vec4(finalize(col), 1.0);
}
