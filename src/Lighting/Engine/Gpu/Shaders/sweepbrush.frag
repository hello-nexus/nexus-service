uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  brush lanes side by side, each with its own brush
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  how ragged and soft the brush edge is
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  length of the bright wet band behind the brush, as a share of the frame; the rest dries darker
uniform float u_rotation; // hint_range(0.0, 360.0, 5.0) = 0.0  brush direction in degrees
uniform float u_wash;     // hint_range(0.0, 1.0, 0.05) = 0.5  watercolor look: pooled edge, bleed, paper grain

// Paint-over: in each lane a brush keeps dragging a new coat across the old
// one. Coats take their hue from the color wheel range (hue is its middle,
// 1 - colorize its width, as in ball.frag), and the edge blends in hue space.
const bool WHEEL_RANGE = true;
// How far each new coat moves along the wheel range.
const float STEP = 0.2;

bool artActive() {
    vec3 stops = u_palette[0] + u_palette[1] + u_palette[2] + u_palette[3];
    return u_paletteMix > 0.5 && dot(stops, vec3(1.0)) > 0.0;
}

// Hue of coat index c inside the wheel range; a partial range ping-pongs so
// consecutive coats never jump across it.
float coatHue(float c) {
    float span = 1.0 - clamp(u_colorize, 0.0, 1.0);
    float k = span > 0.999 ? fract(c) : 1.0 - abs(2.0 * fract(c * 0.5) - 1.0);
    return u_hue + (k - 0.5) * span;
}

vec3 coat(float cOld, float cNew, float painted, float v) {
    if (artActive()) {
        return mix(artPalette(cOld), artPalette(cNew), painted) * v;
    }
    if (!WHEEL_RANGE) {
        return tintedPalette(mix(cOld, cNew, painted)) * v;
    }
    float hOld = coatHue(cOld);
    float d = coatHue(cNew) - hOld;
    d -= floor(d + 0.5);
    return hsv2rgb(vec3(hOld + d * painted, 1.0, v));
}

// Phase for sin(): fract keeps the argument inside one period, so there is no jump at
// the wrap. t * k still rounds at a large clock, so the phase steps coarsely there.
float cyc(float t, float k) {
    return fract(t * k * 0.1591549) * 6.2831853;
}

vec3 laneColor(float lane, float s, float q, float t, float wash) {
    float feather = clamp(u_feather, 0.02, 0.4);
    float fill = clamp(u_fill, 0.2, 1.0);
    float ph = t * 0.2 + lane * 0.17 + 0.08 * hash21(vec2(lane, 1.3));
    float pass = floor(ph);
    // The edge starts and ends off-frame, so a new pass continues the old coat without a jump.
    float reach = feather * 1.4 + 0.15;
    float hx = -reach + (1.0 + 2.0 * reach) * (ph - pass);
    // Bounded seed; it only changes while the edge is off-frame at rollover.
    float rn = vnoise(vec2(q * 6.0, mod(pass, 64.0) * 3.0 + lane * 7.0));
    float rag = feather * 2.0 * (rn - 0.5);
    float bend = 0.05 * sin(q * 5.0 + cyc(t, 0.6) + lane * 2.0);
    // Distance behind the edge along the travel axis; negative is still old coat.
    float e = hx + rag + bend - s;
    float soft = 0.02 + 0.4 * feather + wash * 0.04 * rn;
    float painted = smoothstep(-soft, 0.01, e);
    // Wetness ignores the per-pass rag so the old coat continues the last pass exactly.
    float eW = hx + bend - s;
    float vNew = mix(0.5, 1.0, 1.0 - smoothstep(0.55 * fill, fill, eW));
    float vOld = mix(0.5, 1.0, 1.0 - smoothstep(0.55 * fill, fill, eW + 1.0 + 2.0 * reach));
    float v = mix(vOld, vNew, painted);
    // Watercolor: pigment pools in a line behind the wet edge, and the old hue bleeds into it.
    // Faded out before the pass rolls over, so nothing snaps when the edge restarts.
    float settle = 1.0 - smoothstep(0.85, 1.0, ph - pass);
    float nearEdge = exp(-max(e, 0.0) * 7.0) * painted * settle;
    v *= 1.0 - 0.4 * wash * exp(-max(e, 0.0) * 20.0) * painted * settle;
    float hueMix = painted * (1.0 - 0.45 * wash * rn * nearEdge);
    float cNew = (pass + lane * 0.5) * STEP;
    return coat(cNew - STEP, cNew, hueMix, v);
}

// finalize() without its colorize blend: the wheel range alone decides hue.
vec3 finish(vec3 col) {
    float bl = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
    col = mix(vec3(bl), col, clamp(u_saturation, 0.0, 4.0));
    col = (col - 0.5) * clamp(u_contrast, 0.0, 4.0) + 0.5;
    return gainOut(tonemap(max(col, 0.0)));
}

void main() {
    vec2 uv = uv01();
    // A negative speed mirrors the travel axis, so the brush paints the other way instead of unpainting.
    float dir = u_speed < 0.0 ? -1.0 : 1.0;
    float t = u_time * abs(u_speed) * 0.7;
    float wash = clamp(u_wash, 0.0, 1.0);
    float s = axis01(u_rotation);
    if (dir < 0.0) {
        s = 1.0 - s;
    }
    float q = axis01(u_rotation + 90.0);
    float lanes = floor(max(1.0, u_strokes) + 0.5);
    // Lane centres sit on q = 0.5, so a strip through the middle stays inside one lane.
    float lq = (q - 0.5) * lanes + 0.5 + 0.1 * sin(s * 4.0 + cyc(t, 0.5));
    float lane = floor(lq);
    float fy = lq - lane;
    vec3 col = laneColor(lane, s, q, t, wash);
    float a = u_rotation * 0.01745329;
    float px = mix(u_resolution.y, u_resolution.x, abs(sin(a)));
    float aa = 1.5 * lanes / px;
    float wUp = 1.0 - smoothstep(0.0, aa, fy);
    float wDn = smoothstep(1.0 - aa, 1.0, fy);
    if (wUp > 0.0) col = mix(col, laneColor(lane - 1.0, s, q, t, wash), 0.5 * wUp);
    if (wDn > 0.0) col = mix(col, laneColor(lane + 1.0, s, q, t, wash), 0.5 * wDn);
    if (wash > 0.001) {
        // Paper grain: fixed to the frame, low frequency so LED sampling keeps it smooth.
        float ar = u_resolution.x / u_resolution.y;
        col *= 1.0 - wash * 0.22 * vnoise(vec2(uv.x * ar, uv.y) * 3.0);
    }
    bool ranged = WHEEL_RANGE && !artActive();
    fragColor = vec4(ranged ? finish(col) : finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.90; }
