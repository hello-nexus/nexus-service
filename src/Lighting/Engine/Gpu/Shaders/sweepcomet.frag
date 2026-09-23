uniform float u_comets; // hint_range(1.0, 8.0, 1.0) = 3.0  comets across the frame
uniform float u_tail;   // hint_range(0.1, 1.0, 0.01) = 0.7  tail length

// Gold shine: amber streaks with white-hot heads, the fastest tile in the set.
void main() {
    float f = uv01().x * u_comets - u_time * u_speed * 1.8;
    float d = f - floor(f);
    // Head sits at the cell's leading edge; the tail decays back from it.
    float tail = exp(-(1.0 - d) * (3.0 / clamp(u_tail, 0.1, 1.0)));
    float head = smoothstep(0.94, 1.0, d);
    vec3 col = hsv2rgb(vec3(0.09 + u_hue, 0.95, 1.0)) * tail * 1.3 + vec3(head) * 0.7;
    fragColor = vec4(finalize(col), 1.0);
}
