uniform float u_colors; // hint_range(1.0, 12.0, 1.0) = 6.0  colours stepped through, one per breath
uniform float u_depth;  // hint_range(0.0, 1.0, 0.01) = 1.0  how far each breath fades toward black
uniform float u_spread; // hint_range(0.0, 1.0, 0.01) = 0.0  hue spread across the frame

// Classic RGB breathing: the whole frame fades in and out, and each breath
// takes the next colour around the wheel. The colour changes at the darkest
// point of the breath, so at full depth the step is never visible. One colour
// is a single-hue breath whose colour is the Hue slider.
void main() {
    // t counts breaths. The half-breath offset puts t=0 on a peak, so a frozen
    // frame (Static mode, speed 0) is lit, not black.
    float t = u_time * u_speed * 0.25 + 0.5;
    float breath = floor(t);
    // exp(-cos) holds longer at the dark end than a plain sine, the curve
    // hardware breathing modes use; normalised to 0..1, 0 at each breath edge.
    float b = (exp(-cos(fract(t) * 6.28318)) - 0.36788) / 2.35040;
    float level = mix(1.0 - u_depth, 1.0, b);
    float hue = breath / max(u_colors, 1.0) + u_hue + uv01().x * u_spread;
    vec3 col = hsv2rgb(vec3(hue, 1.0, level));
    fragColor = vec4(finalize(col), 1.0);
}
