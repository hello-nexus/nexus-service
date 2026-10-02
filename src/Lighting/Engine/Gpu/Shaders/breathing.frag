uniform float u_aHue;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSpan;  // hint_range(0.0, 1.0, 0.01) = 1.0  share of the wheel the first colour steps through
uniform float u_aSat;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_bSpan;  // hint_range(0.0, 1.0, 0.01) = 0.0  share of the wheel the second colour steps through
uniform float u_bSat;   // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_sharpness; // hint_range(0.0, 1.0, 0.01) = 0.0  0 = smooth crossfade, 1 = quick switch between long holds

// Hue of a colour's nth turn: even steps through its range, both ends
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

// Two colours take turns: the first peaks mid-breath, the second at each
// breath's edge. Each turn's colour is fixed from the moment it starts fading
// in until it has faded out, so a range never smears mid-fade. A black second
// colour is classic breathing.
void main() {
    // t counts breaths. The half-breath offset puts t=0 on a peak of the first
    // colour, so a frozen frame (Static mode, speed 0) shows it.
    float t = u_time * u_speed * 0.25 + 0.5;
    float n = floor(t);
    float f = fract(t);
    float edge = f < 0.5 ? n : n + 1.0;
    vec3 a = hsv2rgb(vec3(turnHue(u_aHue, u_aSpan, n), u_aSat, u_aVal));
    vec3 b = hsv2rgb(vec3(turnHue(u_bHue, u_bSpan, edge), u_bSat, u_bVal));
    // exp(-cos) holds longer at the dark end, the curve hardware breathing modes
    // use; it applies as far as the second colour is darker than the first, and
    // two equally bright colours get an even cosine crossfade.
    float hold = (exp(-cos(f * 6.28318)) - 0.36788) / 2.35040;
    float even = 0.5 - 0.5 * cos(f * 6.28318);
    // Clamped: hold dips a hair below 0 at the edges, and pow() is undefined there.
    float w = clamp(mix(even, hold, clamp(u_aVal - u_bVal, 0.0, 1.0)), 0.0, 1.0);
    // Sharpness steepens the crossfade around its midpoint, lengthening both holds.
    float k = exp2(3.0 * clamp(u_sharpness, 0.0, 1.0));
    float wk = pow(w, k);
    w = wk / (wk + pow(1.0 - w, k));
    // tonemap, not finalize: the slots own the colour, so the global tint is off.
    fragColor = vec4(tonemap(mix(b, a, w)), 1.0);
}
