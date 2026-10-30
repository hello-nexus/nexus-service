uniform float u_density; // hint_range(0.2, 4.0, 0.05) = 1.0  how many band repeats across the frame
uniform float u_rotation; // hint_range(0.0, 360.0, 5.0) = 0.0  band direction in degrees
uniform float u_aspect; // hint_range(0.0, 1.0, 1.0) = 0.0  on stretches the bands with the frame's aspect ratio
uniform float u_tempo; // hint_range(0.05, 1.0, 0.01) = 0.55  how fast the bands slide at 1x speed

// Painted-strip spectrum: parallel hue bands sliding across the frame.
// The rotation slider rotates the band direction; 0 = vertical bands
// scrolling horizontally, 90 = horizontal bands scrolling vertically.
void main() {
    vec2 uv = uv01() * 2.0 - 1.0;
    uv.x *= mix(1.0, u_resolution.x / u_resolution.y, step(0.5, u_aspect));
    float dens = max(0.001, u_density);
    float a = u_rotation * 0.01745329;
    float pos = uv.x * cos(a) - uv.y * sin(a);
    float h = (pos * 0.5 + 0.5) * dens - u_time * u_speed * u_tempo + u_hue;
    vec3 col = hsv2rgb(vec3(h, 1.0, 1.0));
    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.90; }
