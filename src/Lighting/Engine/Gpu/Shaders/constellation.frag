uniform float u_points; // hint_range(3.0, 10.0, 1.0) = 6.0  grid cells across the frame
uniform float u_reach; // hint_range(0.6, 1.5, 0.05) = 1.4  longest link that still draws
uniform float u_dots; // hint_range(0.2, 2.0, 0.05) = 1.3  node size

// One drifting node per grid cell, each wandering on its own small ellipse.
// A pixel gathers the 3x3 cells around it and draws the twelve lattice edges
// that can cross its own cell, so a link is whole wherever it runs instead of
// breaking where it bulges out of the two cells it joins. 3x3 is the whole
// cost - no global particle list.
//
// The backdrop is an inverse-distance blend of those same nine node colours,
// so the field behind the mesh is always a soft gradient of whatever the
// nearby nodes are lit with rather than flat black.
vec2 nodeAt(vec2 cell, float t) {
    float a = hash21(cell);
    float b = hash21(cell + 31.7);
    float ph = a * 6.28318;
    // Drift is capped at 0.30 so a node stays within 1.13 of every pixel in
    // its own cell while any node outside the 3x3 gather stays beyond 1.20.
    // That gap is what lets the backdrop blend below use a single cutoff that
    // is both always covered and never clipped.
    return cell + 0.5 + 0.30 * vec2(cos(t * (0.5 + b) + ph),
                                    sin(t * (0.42 + a) + ph * 1.7));
}

// One link. Near-binary cutoff: a link draws at full strength or not at all,
// rather than thinning out over a wide distance ramp.
vec3 link(vec2 p, vec2 a, vec2 b, vec3 ca, vec3 cb, float reach, float lw);

float segDist(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a;
    vec2 ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-5), 0.0, 1.0);
    return length(pa - ba * h);
}

vec3 link(vec2 p, vec2 a, vec2 b, vec3 ca, vec3 cb, float reach, float lw) {
    float fade = smoothstep(reach, reach * 0.88, length(b - a));
    float d = segDist(p, a, b);
    return mix(ca, cb, 0.5) * smoothstep(lw, lw * 0.25, d) * fade * 1.25;
}

void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 uv = uv01();
    float cells = clamp(u_points, 3.0, 10.0);
    vec2 p = vec2(uv.x * ar, uv.y) * cells;
    vec2 base = floor(p);
    float t = mod(u_time * u_speed * 0.20, 1000.0);

    // One screen pixel in grid units. Sizes take a pixel-scaled term clamped
    // between a grid-scaled floor and ceiling: the floor keeps the mesh
    // proportional on a 4K panel, the pixel term keeps it visible on a 32x8
    // LED grid, and the ceiling stops that same term from swelling every node
    // past its cell there and washing the whole frame white.
    float px = cells / max(u_resolution.y, 1.0);
    float dotSize = clamp(u_dots, 0.2, 2.0);
    float lw = clamp(px * 3.0, 0.045, 0.16);
    float rd = clamp(px * 6.0, 0.110, 0.26) * dotSize;
    float reach = clamp(u_reach, 0.6, 1.5);

    vec2 nodes[9];
    vec3 cols[9];
    for (int i = 0; i < 9; i++) {
        vec2 cell = base + vec2(float(i % 3) - 1.0, float(i / 3) - 1.0);
        nodes[i] = nodeAt(cell, t);
        cols[i] = tintedPalette(hash21(cell) * 0.85 + t * 0.05);
    }

    vec3 col = vec3(0.0);
    vec3 bg = vec3(0.0);
    float bgw = 0.0;

    for (int i = 0; i < 9; i++) {
        float nd = length(p - nodes[i]);
        // Cutoff sits in the 1.13-1.20 gap the drift cap opens: every pixel
        // always has at least its own cell's node inside it (no black pocket)
        // and no node outside the gather ever reaches it (no cell seams).
        float w = smoothstep(1.18, 0.15, nd) / (0.45 + nd * nd * 2.0);
        bg += cols[i] * w;
        bgw += w;

        // Filled disc with a halo around it, not a gaussian smudge.
        float disc = smoothstep(rd, rd * 0.72, nd);
        float halo = exp(-(nd * nd) / (rd * rd * 2.2));
        col += cols[i] * (disc * 2.4 + halo * 0.60);
    }

    // The lattice edges that can pass through this cell, by index into the 3x3
    // gather (index = (dy+1)*3 + (dx+1)). A node wanders up to 0.30 per axis,
    // so an edge between two adjacent cells can bulge into the next one over;
    // these twelve are every edge that reaches this cell. Written out rather
    // than looped over an index table so both arrays stay constant-indexed and
    // no driver has to spill them into indexable temporaries.
    col += link(p, nodes[3], nodes[4], cols[3], cols[4], reach, lw);
    col += link(p, nodes[4], nodes[5], cols[4], cols[5], reach, lw);
    col += link(p, nodes[1], nodes[4], cols[1], cols[4], reach, lw);
    col += link(p, nodes[4], nodes[7], cols[4], cols[7], reach, lw);
    col += link(p, nodes[0], nodes[4], cols[0], cols[4], reach, lw);
    col += link(p, nodes[3], nodes[7], cols[3], cols[7], reach, lw);
    col += link(p, nodes[1], nodes[5], cols[1], cols[5], reach, lw);
    col += link(p, nodes[4], nodes[8], cols[4], cols[8], reach, lw);
    col += link(p, nodes[6], nodes[4], cols[6], cols[4], reach, lw);
    col += link(p, nodes[3], nodes[1], cols[3], cols[1], reach, lw);
    col += link(p, nodes[7], nodes[5], cols[7], cols[5], reach, lw);
    col += link(p, nodes[4], nodes[2], cols[4], cols[2], reach, lw);

    col += bg / max(bgw, 1e-4) * 0.34;
    fragColor = vec4(finalize(col), 1.0);
}
