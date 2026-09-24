uniform float u_petals; // hint_range(3.0, 24.0, 1.0) = 8.0  segment count
uniform float u_wave; // hint_range(0.0, 1.2, 0.05) = 0.8  edge waviness
uniform float u_spin; // hint_range(-3.0, 3.0, 0.05) = 1.0  rotation rate

// Flat poster petals radiating from the centre. The angular index is pushed
// around by a radial sine so each seam ripples outward into an S instead of
// running straight, and only four hues repeat around the wheel - the look is
// screen print, so colour is held flat inside a segment.
void main() {
    vec2 q = uvCentered();
    float t = mod(u_time * u_speed * 0.20, 1000.0);
    float r = length(q);
    float a = atan(q.y, q.x) / 6.28318 + 0.5;

    float petals = clamp(u_petals, 3.0, 24.0);
    float wave = clamp(u_wave, 0.0, 1.2);
    float ripple = sin(r * 5.0 - t * 1.6) * 0.30 * wave
                 + sin(r * 11.0 + t * 1.1) * 0.10 * wave;
    float seg = (a + ripple + t * 0.05 * clamp(u_spin, -3.0, 3.0)) * petals;
    float idx = floor(seg);
    float f = abs(fract(seg) - 0.5) * 2.0;

    // Four hues on rotation, cream every fourth, so the wheel reads as a
    // fixed retro palette rather than a continuous rainbow. Fold by the petal
    // count first: atan's branch cut makes idx jump by exactly u_petals, so
    // mod(idx, 4.0) alone puts a hard colour seam on the -x axis unless the
    // count happens to be a multiple of four.
    float pw = max(floor(petals + 0.5), 1.0);
    float slot = mod(mod(idx, pw) + pw, 4.0);
    vec3 c = tintedPalette(slot * 0.19 + 0.04);
    c = mix(c, vec3(0.97, 0.94, 0.86), step(2.5, slot) * 0.75);
    c *= 1.05;

    float seam = smoothstep(0.008, 0.038, f);
    vec3 col = mix(vec3(0.06, 0.05, 0.07), c, seam);

    // Centre eye: a soft cream oval the petals converge on.
    float eye = smoothstep(0.26, 0.16, r);
    col = mix(col, vec3(0.97, 0.90, 0.84), eye);
    col *= mix(1.0, 0.88, smoothstep(0.5, 1.6, r));

    fragColor = vec4(finalize(col), 1.0);
}
