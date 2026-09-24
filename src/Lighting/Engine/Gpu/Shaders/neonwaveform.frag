uniform float u_amplitude; // hint_range(0.1, 0.9, 0.05) = 0.35  ribbon reach from the centre line
uniform float u_thickness; // hint_range(0.01, 0.2, 0.005) = 0.03  edge line weight
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  bloom spread around the edges

// Hi-res spectrum, declared here only (see beatbuilder.frag): a 16-band read
// gives eight visible steps per side across a fullscreen ribbon.
uniform float u_spectrum64[64];
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// A mirrored waveform around the centre line: two bright neon edges with a
// translucent fill between them. Bass sits at the centre and treble runs out
// to both edges, so the shape reads symmetric. Idle: a travelling swell.
// Audio: the envelope is the spectrum, smoothed across neighbouring bands.

// Five-tap read: one loud band should widen the ribbon smoothly, not spike a
// single column into a spike the eye reads as an artefact.
float spec64Smooth(float x) {
    float f = clamp(x, 0.0, 1.0) * 63.0;
    float acc = 0.0;
    float wsum = 0.0;
    for (int k = -2; k <= 2; k++) {
        float w = 1.0 - abs(float(k)) * 0.28;
        int i = int(clamp(floor(f) + float(k), 0.0, 63.0));
        acc += u_spectrum64[i] * w;
        wsum += w;
    }
    return acc / wsum;
}

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.6, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();
    float amp = clamp(u_amplitude, 0.05, 1.0);
    float weight = clamp(u_thickness, 0.005, 0.25);
    float glow = clamp(u_glow, 0.2, 3.0);

    float axis = 0.5;
    // Normalize the vertical axis to the SHORT side: uv.y is a fraction of the
    // frame height, so on a portrait panel an untouched reach draws a ribbon a
    // fraction as thick as the same value gives in landscape.
    float vScale = min(u_resolution.x / u_resolution.y, 1.0);
    // Mirror about the frame centre: 0 at the middle, 1 at either edge.
    float mirrored = abs(uv.x - 0.5) * 2.0;

    float band = spec64Smooth(pow(mirrored, 0.85));
    float idle = 0.16 + 0.11 * sin(t * 1.5 - mirrored * 4.5)
                      + 0.05 * sin(t * 2.6 + mirrored * 9.0);
    float env = mix(idle, band * 0.95 + 0.04, presence * boost);
    env *= 1.0 + u_audioLevel * 0.5 * boost;
    // Fine ripple so the edge reads as a waveform rather than a smooth hump;
    // highs sharpen it.
    env *= 1.0 + (0.06 + u_audioHigh * boost * 0.18)
                 * sin(mirrored * 58.0 - t * 2.2);
    // Taper so the ribbon closes toward the frame edges instead of running off.
    env *= 1.0 - pow(mirrored, 2.6) * 0.7;

    float reach = max(env * amp, 0.004);
    float d = abs(uv.y - axis) * vScale;

    vec3 tint = tintedPalette(mirrored * 0.35 + t * 0.05);
    vec3 hot = mix(tint, vec3(1.0), 0.55);

    // Translucent fill with a vertical gradient - densest at the centre line.
    float fill = 1.0 - smoothstep(reach * 0.75, reach, d);
    vec3 col = tint * fill * (0.22 + 0.38 * (1.0 - d / max(reach, 1e-3)));

    // The two neon edges. Line weight is independent of reach, so a quiet
    // passage still draws a crisp ribbon instead of a smear.
    float edge = exp(-pow((d - reach) / max(weight * 0.5, 1e-3), 2.0));
    col += hot * edge * 1.15;
    // Bloom outside the edges.
    col += tint * exp(-max(d - reach, 0.0) * (11.0 / glow)) * 0.30;

    // Centre line: always present, so silence reads as a taut neon thread.
    col += hot * exp(-pow(d * 260.0, 2.0)) * 0.5;

    // Beat: a bright crest travelling out along the ribbon from the centre.
    float sweepPos = 1.0 - u_audioBeat;
    float crest = exp(-pow((mirrored - sweepPos) * 7.0, 2.0));
    col += vec3(1.0) * crest * u_audioBeat * boost * 0.5 * (fill + edge);

    // Floor wash, pumped by bass, so the frame is not pure black below.
    col += tintedPalette(uv.x * 0.3 + t * 0.04)
           * (0.02 + u_audioBass * 0.09 * boost)
           * smoothstep(0.45, 1.0, uv.y);

    fragColor = vec4(finalize(col), 1.0);
}
