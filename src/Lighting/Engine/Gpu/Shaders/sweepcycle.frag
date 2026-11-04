uniform float u_span; // hint_range(0.05, 1.0, 0.01) = 1.0  share of the wheel the colour covers; below full it swings back and forth
uniform float u_dip;  // hint_range(0.0, 1.0, 0.01) = 0.0  how far the frame dims between colour changes

// Classic colour cycle: the whole frame is one colour, drifting round the
// wheel. Nothing travels, so the speed sign only reverses the hue order.
void main() {
    float ph = u_time * u_speed * 0.24;
    // A partial span swings across its arc instead of wrapping, so the ends meet without a jump.
    float h = u_span > 0.99 ? ph : (0.5 - 0.5 * cos(ph * 6.28318)) * u_span;
    float v = 1.0 - u_dip * (0.5 - 0.5 * cos(ph * 25.13274));
    vec3 col = hsv2rgb(vec3(h + u_hue, 1.0, v));
    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.90; }
