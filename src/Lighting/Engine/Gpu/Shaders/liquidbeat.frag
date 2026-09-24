uniform float u_blobs; // hint_range(2.0, 9.0, 1.0) = 5.0  metaball count
uniform float u_viscosity; // hint_range(0.2, 2.5, 0.05) = 1.0  surface tension - low melts, high beads
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  inner bloom through the surface
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// A metaball fluid: overlapping blobs that merge and split like a lava lamp
// under glass. Each blob is bound to a frequency band, so a busy mix pulls the
// surface into a different silhouette every beat. Idle: the blobs still orbit
// and breathe, so the frame never sits still.
//
// Metaball field: sum of r^2 / d^2 per blob, thresholded at 1. The merge
// between two nearby blobs falls out of the summation; the analytic gradient
// alongside it gives a surface normal for the rim and specular.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.5, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float presence = audioPresence();
    int count = int(clamp(u_blobs, 2.0, 9.0));
    float tension = clamp(u_viscosity, 0.15, 2.5);
    float glow = clamp(u_glow, 0.2, 3.0);

    float field = 0.0;
    vec2 grad = vec2(0.0);
    float hueAcc = 0.0;
    float wAcc = 0.0;

    for (int i = 0; i < 9; i++) {
        if (i >= count) break;
        float fi = float(i);
        float phase = fi * 2.399;  // golden-angle spacing, no two blobs in step

        // Lissajous orbit; the two rates are incommensurate so the pattern does
        // not visibly loop.
        vec2 c = vec2(
            sin(t * (0.43 + fi * 0.07) + phase) * (0.78 + 0.14 * sin(t * 0.21 + fi)),
            cos(t * (0.37 + fi * 0.09) + phase * 1.3) * (0.56 + 0.12 * cos(t * 0.27 + fi))
        );

        // One band per blob, low blobs on bass. The idle radius breathes so the
        // surface moves with no audio at all.
        int bandIdx = int(clamp(fi * (16.0 / float(count)), 0.0, 15.0));
        float idleR = 0.15 + 0.035 * sin(t * 1.3 + phase);
        float audioR = 0.09 + u_spectrum[bandIdx] * 0.17 + u_audioBass * 0.07;
        float r = mix(idleR, audioR, presence * boost);
        r *= 1.0 + u_audioBeat * boost * 0.18;

        vec2 d = uv - c;
        float dd = max(dot(d, d), 2e-3);
        float contrib = (r * r) / dd;
        field += contrib;
        grad += -2.0 * (r * r) * d / (dd * dd);

        hueAcc += contrib * (fi / float(count));
        wAcc += contrib;
    }

    // Surface: threshold at 1, transition width set by tension.
    float edgeW = 0.30 / tension;
    float surface = smoothstep(1.0 - edgeW, 1.0 + edgeW * 0.5, field);
    float depth = smoothstep(1.0, 2.4, field);

    // Surface normal from the analytic gradient - the rim and the highlight
    // both track the real shape, including where two blobs are mid-merge.
    vec3 n = normalize(vec3(grad * 0.06, 1.0));
    vec3 lightDir = normalize(vec3(-0.45, -0.6, 0.65));
    float diffuse = clamp(dot(n, lightDir) * 0.5 + 0.5, 0.0, 1.0);
    float spec = pow(clamp(dot(reflect(-lightDir, n), vec3(0.0, 0.0, 1.0)), 0.0, 1.0), 42.0);

    float hueT = wAcc > 0.0 ? hueAcc / wAcc : 0.0;
    // Iridescence from the slope MAGNITUDE, not its direction: the gradient
    // rotates through every angle around a blob centre, so a direction-keyed
    // hue paints a pinwheel with a singularity at the middle.
    vec3 body = tintedPalette(hueT * 0.5 + (1.0 - n.z) * 0.16 + t * 0.04);
    // Lighter body, not a second palette entry: a contrasting hue at the
    // blob centres reads as a hole punched through the fluid.
    vec3 core = mix(body, vec3(1.0), 0.35);

    vec3 col = body * surface * (0.35 + diffuse * 0.55);
    col += core * depth * (0.25 + u_audioLevel * 0.45 * boost) * glow * 0.6;
    col += vec3(1.0) * spec * surface * 0.30;

    // Rim: bright where the field crosses the threshold, which is what makes
    // the blobs read as liquid rather than as flat discs.
    float rim = exp(-pow((field - 1.0) * (4.0 * tension), 2.0));
    col += mix(body, vec3(1.0), 0.5) * rim * (0.45 + u_audioHigh * boost * 0.6);

    // Caustic shimmer inside the body, so a large merged blob is not flat.
    float caustic = fbm(uv * 3.2 + vec2(t * 0.3, -t * 0.22));
    col += body * caustic * depth * 0.22;

    // Background: a slow palette wash lifted by mids, with a vignette.
    float bg = fbm3(uv * 1.1 - vec2(t * 0.12, t * 0.09));
    col += tintedPalette(bg * 0.5 + t * 0.03)
           * (0.035 + u_audioMid * 0.16 * boost)
           * (1.0 - surface) * (1.0 - smoothstep(0.4, 1.5, length(uv)) * 0.7);

    fragColor = vec4(finalize(col), 1.0);
}
