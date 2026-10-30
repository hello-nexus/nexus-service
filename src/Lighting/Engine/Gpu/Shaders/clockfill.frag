uniform float u_aHue;   // hint_range(0.0, 1.0, 0.01) = 0.5
uniform float u_aSpan;  // hint_range(0.0, 1.0, 0.01) = 0.0  share of the wheel the first color steps through
uniform float u_aSat;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_bSpan;  // hint_range(0.0, 1.0, 0.01) = 0.0  share of the wheel the second color steps through
uniform float u_bSat;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_softness; // hint_range(0.0, 0.5, 0.01) = 0.12  width of the fill's leading edge
uniform float u_arms;     // hint_range(1.0, 6.0, 1.0) = 1.0  hands sweeping at once, evenly spaced

// Hue of a color's nth turn: even steps through its range, both ends
// included, wrapping back to the start. The full wheel steps on from the hue.
float turnHue(float hue, float span, float n) {
    if (span > 0.99) return hue + n / 6.0;
    if (span <= 0.0) return hue;
    float count = max(1.0, floor(span * 6.0 + 0.5)) + 1.0;
    // n + 0.5 keeps floor off an exact multiple, where a GPU's x * rcp(y)
    // division can land just below the integer.
    float k = n - count * floor((n + 0.5) / count);
    return hue - span * 0.5 + span * k / (count - 1.0);
}

// The color that fills on pass n: the first color on even passes, the
// second on odd ones, each stepping through its range once per appearance.
vec3 passColor(float n) {
    float m = floor(n * 0.5 + 0.25);
    bool first = n - 2.0 * floor(n * 0.5 + 0.25) < 0.5;
    return first
        ? hsv2rgb(vec3(turnHue(u_aHue, u_aSpan, m), u_aSat, u_aVal))
        : hsv2rgb(vec3(turnHue(u_bHue, u_bSpan, m), u_bSat, u_bVal));
}

// A fill sweeps around like a clock hand from 12 o'clock: each pass paints the next
// color over the last. A negative speed runs time backwards, so the fill unwinds.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01() - 0.5;
    p.x *= ar;
    // Clockwise turn from 12 o'clock, 0..1 (uv01 grows downward), repeated once per hand.
    float arms = max(1.0, floor(u_arms + 0.5));
    float x = fract(fract(atan(p.x, -p.y) / 6.2831853 + 1.0) * arms);
    // The half-pass offset puts speed 0 mid-fill, so a frozen frame (Static
    // mode) shows both colors.
    float ph = u_time * u_speed * 0.25 + 0.5;
    float n = floor(ph);
    // A zero-width edge would make smoothstep undefined (edge0 == edge1).
    float w = max(u_softness, 0.001);
    // The edge runs past a full turn (x = 1) by its own width, so a pass has
    // fully covered the frame before the next color takes over.
    float e = fract(ph) * (1.0 + w);
    float s = 1.0 - smoothstep(e - w, e, x);
    // tonemap, not finalize: the slots own the color, so the global tint is off.
    fragColor = vec4(gainOut(tonemap(mix(passColor(n - 1.0), passColor(n), s))), 1.0);
}

float outGain() { return 1.90; }
