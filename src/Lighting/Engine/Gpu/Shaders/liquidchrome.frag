uniform float u_flow; // hint_range(0.2, 3.0, 0.05) = 1.0  surface flow rate
uniform float u_thickness; // hint_range(0.3, 2.0, 0.05) = 1.0  surface variation
uniform float u_ripple; // hint_range(0.3, 3.0, 0.05) = 1.0  ripple frequency

// Quilted chrome: a tessellating tile pattern is carved into a flowing
// mercury surface. Each tile has a rounded facet so the light bounces
// off it with a visible specular highlight, and a slow advection drifts
// the tile grid across the frame so the pattern breathes. Reads as a
// repeating ornamental chrome, visually distinct from oilslick (soft
// iridescent film) and from the old smooth-mercury version.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * u_flow * 0.25;
    float thick = clamp(u_thickness, 0.1, 2.5);
    float rip = clamp(u_ripple, 0.2, 4.0);

    // Slow advection: the whole tile grid drifts over time so the
    // repeating pattern moves rather than sitting still.
    vec2 drift = vec2(sin(t * 0.7) * 0.4, cos(t * 0.5) * 0.3);
    vec2 baseUv = uv + drift;

    // Hex-style tessellation. Each tile maps to a cell centre; the
    // distance-from-centre is what we'll use as the tile's height
    // field. Doubling up the grid at an offset gives the honeycomb
    // interlock instead of a plain square grid.
    float cellPitch = 0.35 / max(rip, 0.2);
    vec2 g = baseUv / cellPitch;
    vec2 gA = floor(g);
    vec2 gB = floor(g + vec2(0.5, 0.0));
    vec2 fA = g - gA - 0.5;
    vec2 fB = (g + vec2(0.5, 0.0)) - gB - 0.5;
    // Pick whichever cell centre is closer - that's the tile we're in.
    float dA = dot(fA, fA);
    float dB = dot(fB, fB);
    vec2 cellId = dA < dB ? gA : gB;
    vec2 local = dA < dB ? fA : fB;

    // Per-tile properties: random phase + rotation so no two tiles look
    // identical, but the lattice is still obviously periodic.
    float tileHash = hash21(cellId);
    float tileRot = tileHash * 6.28318;
    vec2 rotL = vec2(
        cos(tileRot) * local.x - sin(tileRot) * local.y,
        sin(tileRot) * local.x + cos(tileRot) * local.y
    );

    // Rounded-facet height field inside the cell: positive bump in the
    // centre, tapers to zero at the edge. Each tile also has a small
    // phase-shifted bump that wobbles, which is what gives the chrome
    // its "flowing" feel without losing the repeat.
    float tileR = length(local);
    float bump = (1.0 - smoothstep(0.25, 0.5, tileR)) * thick;
    float wobble = sin(t * (1.4 + tileHash * 0.8) + tileHash * 6.28) * 0.25;
    // perf: hoist the shared groove term (reused by h and hY unchanged).
    float groove = 0.18 * bump * cos(rotL.x * 14.0 + t * 2.0);
    float h = bump + wobble * bump * 0.6;
    // Add a secondary groove that crosses each tile - this is what
    // makes the pattern read as "cut" rather than "drop".
    h += groove;

    // Normal from finite-differenced height inside the tile.
    float eps = 0.015;
    float hX = (1.0 - smoothstep(0.25, 0.5, length(local + vec2(eps, 0.0))))
             + 0.18 * bump * cos((rotL.x + eps) * 14.0 + t * 2.0);
    float hY = (1.0 - smoothstep(0.25, 0.5, length(local + vec2(0.0, eps))))
             + groove;
    vec3 n = normalize(vec3(h - hX, h - hY, 0.5 * eps));

    // Chrome shading: dark base, soft diffuse from a fixed key light,
    // sharp specular highlights on each facet.
    vec3 lightDir = normalize(vec3(0.35, 0.7, 0.6));
    vec3 viewDir = vec3(0.0, 0.0, 1.0);
    vec3 reflectDir = reflect(-lightDir, n);
    float lambert = max(dot(n, lightDir), 0.0);
    float spec = pow(max(dot(reflectDir, viewDir), 0.0), 28.0);

    // Palette varies per tile so the chrome reads as tinted - pick hue
    // by tile hash so neighbouring tiles are distinctly different.
    vec3 tileTint = tintedPalette(tileHash * 0.6 + 0.2 + t * 0.05);
    vec3 base = tileTint * 0.22;
    vec3 mid = tileTint * 0.55;
    vec3 col = base + mid * lambert;
    col += vec3(1.0, 0.98, 0.95) * spec * 1.6;

    // Grout between tiles: dark crack where the cells meet.
    float grout = smoothstep(0.42, 0.5, tileR);
    col *= (1.0 - grout * 0.7);
    // Faint iridescent rim on the grout so the pattern doesn't go dead.
    col += tintedPalette(0.55 - tileHash * 0.3) * grout * 0.35;

    fragColor = vec4(finalize(col), 1.0);
}
