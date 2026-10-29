uniform float u_comets; // hint_range(1.0, 8.0, 1.0) = 3.0  comets across the frame
uniform float u_tail;   // hint_range(0.1, 1.0, 0.01) = 0.7  tail length
uniform float u_color;  // hint_range(0.0, 1.0, 0.01) = 0.09  streak colour on the hue wheel, the default is gold
uniform float u_head;   // hint_range(0.0, 1.0, 0.01) = 0.7  how white the leading edge burns

// Shine: coloured streaks with white-hot heads.
void main() {
    float f = uv01().x * u_comets - u_time * u_speed * 1.8;
    float d = f - floor(f);
    // Head sits at the cell's leading edge; the tail decays back from it.
    float tail = exp(-(1.0 - d) * (3.0 / clamp(u_tail, 0.1, 1.0)));
    float head = smoothstep(0.94, 1.0, d);
    vec3 col = hsv2rgb(vec3(u_color + u_hue, 0.95, 1.0)) * tail * 1.3 + vec3(head) * u_head;
    fragColor = vec4(finalize(col), 1.0);
}
