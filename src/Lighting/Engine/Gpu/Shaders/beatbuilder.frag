uniform float u_intensity; // hint_range(0.0, 1.0, 0.01) = 1.0

// Beat Builder extends the shared audio uniforms with a hi-res spectrum, a
// short per-band history, and decaying peak-holds. These are declared only
// here (not the prelude) so only this effect pays their uniform-vector cost;
// ShaderEffect.cs / useShaderRenderer.ts upload them when the locations exist.
//   u_spectrum64 : current frame, 64 log bands, 0..1
//   u_specHist   : 16 bands x 16 frames, vec4-packed; float index = frame*16+band,
//                  frame 0 = current, frame 15 = oldest retained
//   u_*Peak      : peak-hold of bass/mid/high, rises instant, decays slow
uniform float u_spectrum64[64];
uniform vec4  u_specHist[64];
uniform float u_bassPeak;
uniform float u_midPeak;
uniform float u_highPeak;

// ---- Center spectrum ----
uniform float u_centerStyle; // hint_range(0.0, 3.0, 1.0) = 2.0  0 Bars, 1 Smooth, 2 Dots, 3 Radial
uniform float u_barCount; // hint_range(8.0, 96.0, 1.0) = 48.0  bar / cell density across the half-width
uniform float u_barWidth; // hint_range(0.1, 1.0, 0.02) = 0.7  0..1 fill ratio inside each bar/cell
uniform float u_centerGain; // hint_range(0.3, 3.0, 0.05) = 1.2  spectrum amplitude
uniform float u_centerFloor; // hint_range(0.0, 0.3, 0.01) = 0.04  noise gate
uniform float u_centerSize; // hint_range(0.3, 1.1, 0.02) = 0.8  vertical half-height of the center region
// ---- Top band meters ----
uniform float u_topMeters; // hint_range(0.0, 1.0, 1.0) = 1.0  0/1
uniform float u_topHeight; // hint_range(0.04, 0.25, 0.005) = 0.085  top strip height fraction; also sizes the corner squares
// ---- Fan corner fills (top-right) ----
uniform float u_cornerFills; // hint_range(0.0, 1.0, 1.0) = 1.0  0/1: square radial gauge + square level fill, anchored top-right
// ---- Bottom bars (count + width track the center bars) ----
uniform float u_bottomBars; // hint_range(0.0, 1.0, 1.0) = 1.0  0/1
uniform float u_bottomScale; // hint_range(0.02, 0.2, 0.005) = 0.07  strip height fraction
// ---- Color & style ----
uniform float u_colorMode; // hint_range(0.0, 1.0, 1.0) = 1.0  0 Solid, 1 Rainbow
uniform float u_beatColor; // hint_range(0.0, 1.0, 0.02) = 0.0  hue jump on beat
uniform float u_bgLevel; // hint_range(0.0, 0.2, 0.01) = 0.0  background brightness floor
uniform float u_flash; // hint_range(0.0, 1.5, 0.05) = 0.0  full-frame beat strobe amount (0 = off)
uniform float u_beatPulse; // hint_range(0.0, 1.0, 0.02) = 0.3  scale-on-beat amount
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

float boost() { return clamp(u_audioBoost, 0.0, 2.0); }
float band64(int i) { return u_spectrum64[clamp(i, 0, 63)]; }

// 16x16 history packed as vec4[64]; frame 0 = current.
float histAt(int band, int frame) {
    int fi = frame * 16 + band;
    return u_specHist[fi >> 2][fi & 3];
}

// fpos 0..1 -> interpolated 64-band magnitude.
float spec(float fpos) {
    float x = clamp(fpos, 0.0, 1.0) * 63.0;
    int i = int(floor(x));
    return mix(band64(i), band64(min(i + 1, 63)), fract(x));
}

// The one magnitude sampler every region uses: gated, gain-scaled audio,
// blended against an idle travelling wave by audio presence so the frame is
// alive before audio arrives and locks to the music once it does.
float magAt(float pos) {
    float bz = boost();
    float gain = u_centerGain * (1.0 + u_audioLevel * 0.4 * bz);
    float audio = max(spec(pos) - u_centerFloor, 0.0) * gain;
    float t = u_time * u_speed;
    float idle = 0.16 + 0.13 * sin(t * 1.7 + pos * 17.0) * cos(t * 0.8 + pos * 6.0);
    return clamp(mix(max(idle, 0.0), audio, clamp(audioPresence() * bz, 0.0, 1.0)), 0.0, 1.5);
}

vec3 hueRGB(float h) { return hsv2rgb(vec3(fract(h), 1.0, 1.0)); }

// colorT 0..1: frequency / screen position. Solid (0) paints one wheel hue;
// Rainbow (1) sweeps the full wheel across colorT. The wheel hue rotates the
// whole palette and Colorize pulls it toward mono, so the colour wheel alone
// defines the desired range - no separate spread/second-hue controls.
vec3 colorFor(float colorT) {
    int mode = int(u_colorMode + 0.5);
    float base = u_hue + u_beatColor * u_audioBeat * 0.35;
    if (mode >= 1) {
        return hueRGB(base + clamp(colorT, 0.0, 1.0));
    }
    return hueRGB(base);
}

// Center spectrum luminance for the folded coords. Writes colorT via out param.
float centerField(vec2 f, float aspect, out float colorT) {
    int style = int(u_centerStyle + 0.5);
    float fx = clamp(abs(f.x) / aspect, 0.0, 1.0);   // 0 center -> 1 edge
    float fy = f.y;                                   // 0 center -> +/-1
    colorT = fx;

    if (style == 1) {
        // Smooth silhouette kept as a centered band, not a full-height fill.
        float halfH = min(magAt(fx) * u_centerSize * 0.5, 0.8);
        return smoothstep(halfH + 0.02, halfH - 0.02, abs(fy));
    }

    if (style >= 2) {
        float cols = clamp(u_barCount, 12.0, 96.0);
        float rows = max(floor(cols / aspect), 6.0);
        vec2 g = vec2(fx, abs(fy));
        vec2 cell = floor(vec2(g.x * cols, g.y * rows));
        vec2 cc = (cell + 0.5) / vec2(cols, rows);     // cell-center, 0..1
        vec2 fr = fract(vec2(g.x * cols, g.y * rows)) * 2.0 - 1.0;
        float dotMask = step(max(abs(fr.x), abs(fr.y)), clamp(u_barWidth, 0.1, 1.0));

        if (style == 3) {
            // Radial fill: draw each band as a ring/outline AT its magnitude
            // height instead of a solid fill, so the quad-mirror folds it into
            // the concentric diamond "eye" lobes of the video's radial style.
            // Trailing rings sampled from the history buffer (now, ~4, ~8, ~12
            // frames back) nest into concentric diamonds as the music moves.
            float fpos = cc.x;
            colorT = fpos;
            float m = magAt(fpos);
            float ringW = (0.05 + 0.10 * (1.0 - m)) / rows * 6.0;   // thinner when loud
            int hb = int(clamp(fpos, 0.0, 0.999) * 16.0);
            float acc = 0.0;
            for (int k = 0; k < 4; k++) {
                float hm = clamp(histAt(hb, k * 4) * u_centerSize, 0.0, 1.0);
                acc = max(acc, step(abs(cc.y - hm), ringW * (1.0 + float(k) * 0.25))
                               * (1.0 - float(k) * 0.18));
            }
            float spark = step(hash21(cell + 3.7), 0.55 + 0.45 * m);
            return acc * dotMask * spark * (0.7 + 0.5 * m);
        }
        // Dots: vertical spectrum silhouette as cells, lacier where quieter.
        float fpos = cc.x;
        colorT = fpos;
        float m = magAt(fpos);
        float lit = step(cc.y, m * u_centerSize);
        float spark = step(hash21(cell + 3.7), 0.3 + 0.7 * m);
        return lit * dotMask * spark;
    }

    // Bars: quantized vertical bars from the midline.
    float bars = clamp(u_barCount, 8.0, 96.0);
    float bi = floor(fx * bars);
    float pos = (bi + 0.5) / bars;
    colorT = pos;
    float halfH = magAt(pos) * u_centerSize;
    float within = fract(fx * bars) * 2.0 - 1.0;
    float wmask = step(abs(within), clamp(u_barWidth, 0.1, 1.0));
    float body = step(abs(fy), halfH);
    float grad = mix(1.15, 0.55, abs(fy) / max(halfH, 0.001));
    return clamp(body * wmask * grad, 0.0, 1.4);
}

void main() {
    vec2 uv = uv01();
    float aspect = u_resolution.x / u_resolution.y;
    float bz = boost();

    // Beat pump: gently zoom the center coords on each onset.
    vec2 c = uvCentered();
    c /= 1.0 + u_beatPulse * u_audioBeat * 0.18 * bz;

    vec2 f = vec2(abs(c.x), abs(c.y));   // 4-way mirror (quad), matching the video

    float colorT;
    float lum = centerField(f, aspect, colorT);
    vec3 col = colorFor(colorT) * lum;

    // ---- Bottom bars ----
    if (u_bottomBars > 0.5) {
        float Bm = clamp(u_bottomScale, 0.02, 0.25);
        if (uv.y > 1.0 - Bm) {
            float mfx = abs(uv.x * 2.0 - 1.0);            // mirror around center
            float bars = clamp(u_barCount, 8.0, 96.0);   // count tracks the center bars
            float bi = floor(mfx * bars);
            float pos = (bi + 0.5) / bars;
            float mag = clamp(magAt(pos), 0.0, 1.0);
            float within = fract(mfx * bars) * 2.0 - 1.0;
            float wmask = step(abs(within), clamp(u_barWidth, 0.1, 1.0));   // width tracks too
            // Full-height bars within the strip; magnitude drives brightness.
            col = colorFor(pos) * wmask * (0.12 + 0.95 * mag);
        }
    }

    // ---- Top strip: band meters (left) + fan corner fills (top-right) ----
    // The corner reserves a strip-height square for a clockwise radial gauge
    // (fan RGB) plus a vertical level fill to its left; the band meters shrink
    // to the remaining width so the two never overlap.
    float Tm = clamp(u_topHeight, 0.04, 0.25);
    if (uv.y < Tm && (u_topMeters > 0.5 || u_cornerFills > 0.5)) {
        // Corner squares: side = strip height in pixels, so sq (in uv.x) = Tm/aspect.
        // The radial gauge anchors the corner, the level square sits to its left,
        // and the band meters shrink to the remaining width.
        float sq = Tm / aspect;
        float cg = 0.012;                         // gap between elements
        float cornerW = (u_cornerFills > 0.5) ? (2.0 * sq + 2.0 * cg) : 0.0;
        float metersR = max(1.0 - cornerW, 0.0);  // band meters span [0, metersR]

        if (u_cornerFills > 0.5 && uv.x >= metersR) {
            col = vec3(0.0);                       // reserve the corner: clear behind
            float vx0 = metersR + cg;             // vertical square spans [vx0, vx0+sq]
            if (uv.x >= vx0 && uv.x < vx0 + sq) {
                float up = (Tm - uv.y) / Tm;       // 0 at strip bottom -> 1 at top
                col = colorFor(up) * step(up, clamp(u_audioLevel * bz, 0.0, 1.0));
            } else if (uv.x >= 1.0 - sq) {
                // Radial fill bounded by a square: a clockwise wedge from the top
                // that lights the whole square at full level. The angle uses
                // aspect-corrected offsets so the sweep stays circular.
                vec2 rc = vec2(1.0 - sq * 0.5, Tm * 0.5);
                vec2 rd = vec2((uv.x - rc.x) * aspect, uv.y - rc.y);
                float ta = fract(atan(rd.x, -rd.y) / 6.2831853);  // clockwise from top
                float lit = step(ta, clamp(u_audioLevel * bz, 0.0, 1.0));
                col = colorFor(ta) * (0.12 + 0.88 * lit);          // dim track + bright fill
            }
            // gaps between the squares stay black (reserved corner)
        } else if (u_topMeters > 0.5 && uv.x < metersR) {
            float mx = uv.x / max(metersR, 0.001);
            float seg = floor(mx * 3.0);
            float cur = seg < 0.5 ? u_audioBass : (seg < 1.5 ? u_audioMid : u_audioHigh);
            float pk  = seg < 0.5 ? u_bassPeak  : (seg < 1.5 ? u_midPeak  : u_highPeak);
            float within = fract(mx * 3.0);
            float ghost = step(within, pk) * 0.35;
            float bright = step(within, cur * bz);
            float gap = step(0.02, within) * step(within, 0.98);
            col = colorFor(seg / 3.0) * max(ghost, bright) * gap;
        }
    }

    // Background floor + master intensity.
    col += colorFor(0.5) * u_bgLevel * (0.4 + 0.6 * u_audioLevel * bz);

    // Beat flash: full-frame strobe in the palette colour on each onset.
    col += colorFor(0.5) * (u_flash * u_audioBeat * 0.9 * bz);

    col *= clamp(u_intensity, 0.0, 1.0);

    fragColor = vec4(finalize(col), 1.0);
}
