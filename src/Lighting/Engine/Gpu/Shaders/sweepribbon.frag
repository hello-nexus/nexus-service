uniform float u_ribbons; // hint_range(2.0, 10.0, 1.0) = 4.0  ribbons across the frame
uniform float u_wave;    // hint_range(0.0, 0.5, 0.01) = 0.18  edge wobble

// Green and teal ribbons flowing across the frame, their edges wobbling with
// height so a two-dimensional device shows the curl a plain band would not.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.9;
    float f = (uv.x + sin(uv.y * 6.28318 + t * 2.0) * u_wave) * u_ribbons - t;
    float cell = floor(f);
    float d = f - cell;
    float body = smoothstep(0.0, 0.12, d) * smoothstep(1.0, 0.88, d);
    float hue = (mod(cell, 2.0) < 1.0 ? 0.33 : 0.47) + u_hue;
    vec3 col = hsv2rgb(vec3(hue, 0.9, 1.0)) * body;
    fragColor = vec4(finalize(col), 1.0);
}
