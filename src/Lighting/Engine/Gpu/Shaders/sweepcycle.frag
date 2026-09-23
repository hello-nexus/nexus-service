// Classic colour cycle: the whole frame is one colour, drifting round the
// wheel. Nothing travels, so the speed sign only reverses the hue order.
void main() {
    vec3 col = hsv2rgb(vec3(u_time * u_speed * 0.08 + u_hue, 1.0, 1.0));
    fragColor = vec4(finalize(col), 1.0);
}
