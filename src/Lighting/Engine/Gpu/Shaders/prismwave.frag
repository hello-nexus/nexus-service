uniform float u_bands; // hint_range(1.0, 16.0, 1.0) = 4.0  spatial frequency of each band stack
uniform float u_sharpness; // hint_range(1.0, 10.0, 0.1) = 7.0  edge falloff exponent (1 = soft, 10 = razor)
uniform float u_thickness; // hint_range(0.15, 0.95, 0.01) = 0.6  how much of each cycle reads as "in band"
uniform float u_drift; // hint_range(0.1, 1.2, 0.02) = 0.65  source motion radius

// Sharp banded color waves emitted from four moving sources, blended
// plasma-style. Each source radiates concentric rings whose v-value gets
// a power curve to crush the band edges (the spiral-shader feel). The
// four wave fields are then combined with a soft-max so peaks survive
// while troughs blend smoothly between sources, giving the
// "plasma made of crystal sheets" look the brief asked for.
//
// u_thickness biases the raw 0..1 ring value before the sharpness power,
// so bands can be made wider (thicker) without softening their edges.
// 0.5 is "neutral" (matches the unbiased sin); higher widens the lit
// portion of each ring; lower thins it to slivers.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.55;
    float bands = clamp(u_bands, 1.0, 16.0);
    float sharp = clamp(u_sharpness, 1.0, 10.0);
    float thick = clamp(u_thickness, 0.05, 0.95);
    float drift = clamp(u_drift, 0.1, 1.2);

    // Four wave centers orbiting at different periods so their interference
    // never repeats in any reasonable session length.
    vec2 c0 = drift * vec2(cos(t * 0.31),       sin(t * 0.27));
    vec2 c1 = drift * vec2(cos(t * 0.19 + 1.7), sin(t * 0.23 + 0.5));
    vec2 c2 = drift * vec2(cos(t * 0.41 - 2.1), sin(t * 0.34 - 1.1));
    vec2 c3 = drift * vec2(cos(t * 0.27 + 3.0), sin(t * 0.36 + 2.4));

    float d0 = length(uv - c0);
    float d1 = length(uv - c1);
    float d2 = length(uv - c2);
    float d3 = length(uv - c3);

    // Each source emits a sin-banded ring field. Phase scrolls outward so
    // bands look like waves leaving the source. Thickness adds an upward
    // bias so a larger fraction of each cycle ends up above 0; the
    // sharpness power then crushes the soft shoulder back down, leaving
    // wider plateaus with the same hard edges.
    float bias = thick - 0.5;
    float w0 = pow(clamp(0.5 + 0.5 * sin(d0 * bands - t * 1.5) + bias, 0.0, 1.0), sharp);
    float w1 = pow(clamp(0.5 + 0.5 * sin(d1 * bands - t * 1.3) + bias, 0.0, 1.0), sharp);
    float w2 = pow(clamp(0.5 + 0.5 * sin(d2 * bands - t * 1.7) + bias, 0.0, 1.0), sharp);
    float w3 = pow(clamp(0.5 + 0.5 * sin(d3 * bands - t * 1.1) + bias, 0.0, 1.0), sharp);

    // perf: soft-max approximated by max(...) blended toward the summed value;
    // drops 5 pow() per pixel for near-identical plateau/overlap feel.
    float vmax = max(max(w0, w1), max(w2, w3));
    float vavg = (w0 + w1 + w2 + w3) * 0.25;
    float v = clamp(mix(vmax, vavg, 0.15), 0.0, 1.0);

    // Each source claims its own slice of the palette so where they overlap
    // the colours mix naturally; the band scroll term keeps hue marching.
    vec3 col0 = tintedPalette(0.00 + d0 * 0.15 + t * 0.05);
    vec3 col1 = tintedPalette(0.25 + d1 * 0.15 + t * 0.07);
    vec3 col2 = tintedPalette(0.50 + d2 * 0.15 + t * 0.06);
    vec3 col3 = tintedPalette(0.75 + d3 * 0.15 + t * 0.04);

    float wsum = w0 + w1 + w2 + w3 + 0.0001;
    vec3 col = (col0 * w0 + col1 * w1 + col2 * w2 + col3 * w3) / wsum;
    // Higher floor on the lit side so the thicker bands read as filled
    // rather than thin contours; troughs still go fully dark.
    col *= 0.05 + 1.10 * v;

    fragColor = vec4(finalize(col * 1.35), 1.0);
}
