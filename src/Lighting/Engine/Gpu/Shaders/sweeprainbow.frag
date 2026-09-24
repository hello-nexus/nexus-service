uniform float u_density; // hint_range(0.5, 4.0, 0.1) = 1.0  hue cycles across the frame

// Simple-mode sweep: the classic spectrum, one full hue cycle travelling along
// the frame's long axis. Every sweep* shader moves on +x only - simple mode
// flips travel with the speed sign, so no shader here reads a direction. Each
// carries its own tempo in the time scale below, since the set is offered with
// no speed control.
void main() {
    float f = uv01().x * u_density - u_time * u_speed * 0.55;
    vec3 col = hsv2rgb(vec3(f + u_hue, 1.0, 1.0));
    fragColor = vec4(finalize(col), 1.0);
}
