uniform float u_density; // hint_range(4.0, 16.0, 1.0) = 9.0  trace density
uniform float u_pulse; // hint_range(0.3, 3.0, 0.05) = 1.0  pulse rate along traces
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  pulse glow

// Animated circuit-board traces with bright pulses traveling along
// them. Each grid cell carries an L-shaped trace whose orientation is
// hashed; pulses are bright dots moving along the traces.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.7, 1000.0);
    float dens = clamp(u_density, 3.0, 20.0);
    float pulseRate = clamp(u_pulse, 0.1, 4.0);
    float glow = clamp(u_glow, 0.1, 3.0);

    // Cell layout.
    vec2 grid = uv * dens;
    vec2 cell = floor(grid);
    vec2 inCell = fract(grid) - 0.5;
    float cellHash = hash21(cell);

    // Each cell picks an L-shape orientation: 4 possibilities.
    int orient = int(cellHash * 4.0);
    // Trace path within the cell: L from one edge to centre to perpendicular edge.
    // We compute distance to two line segments and take the min.
    float dx = abs(inCell.x);
    float dy = abs(inCell.y);

    // Segment 1 (centre to right): horizontal line at y=0, x in [0, 0.5].
    // Segment 2 (centre up): vertical line at x=0, y in [0, 0.5].
    // We fold the cell-local coords based on orient so the L points
    // in different directions.
    vec2 p = inCell;
    if (orient == 1) p = vec2(-p.x, p.y);
    if (orient == 2) p = vec2(p.x, -p.y);
    if (orient == 3) p = vec2(-p.x, -p.y);

    // perf: dropped the unused distToL distance-to-L computation (min/max/abs
    // chain whose result was never read) - pure per-pixel waste.

    // Trace itself: thin line.
    float trace = exp(-abs(p.y) * 35.0) * step(0.0, 0.5 - abs(p.x))
                + exp(-abs(p.x) * 35.0) * step(0.0, 0.5 - abs(p.y));
    trace = clamp(trace, 0.0, 1.0);

    // Pulse traveling along the trace: parametric position 0..1 along
    // each arm. Use cellHash-offset time so all cells aren't synchronised.
    // perf: pulses only live on a lit trace; where trace==0 (most pixels, a
    // coherent background region) both glow exp() are ~0, so skip the heavy pair.
    float pulseGlow = 0.0;
    if (trace > 0.0) {
        float pulsePhase = fract(t * pulseRate * 0.7 + cellHash);
        float invGlow = 20.0 / glow;
        // Pulse along horizontal arm.
        float pulseHX = pulsePhase - 0.5;  // -0.5..0.5
        float pulseDistH = abs(p.x - pulseHX) + abs(p.y) * 4.0;
        // Pulse along vertical arm.
        float pulseVY = (1.0 - pulsePhase) - 0.5;
        float pulseDistV = abs(p.y - pulseVY) + abs(p.x) * 4.0;
        pulseGlow = exp(-pulseDistH * invGlow) + exp(-pulseDistV * invGlow);
    }

    vec3 traceColor = tintedPalette(0.55 + cellHash * 0.2);
    vec3 pulseColor = tintedPalette(0.4 + cellHash * 0.2 + t * 0.1);

    // Board tint varies across the frame so the background itself carries
    // colour instead of being a dim uniform slab.
    // perf: faint 0.18-scaled background tint, no fine detail needed -> fbm3
    float boardFbm = fbm3(uv * 1.4 + vec2(t * 0.2, 0.0));
    vec3 boardTint = tintedPalette(0.35 + boardFbm * 0.3) * 0.18;

    vec3 col = boardTint;
    col += traceColor * trace * 1.1;
    col += pulseColor * pulseGlow * 2.2;
    // Solder-mask glow where traces are dense.
    col += traceColor * trace * trace * 0.6;

    fragColor = vec4(finalize(col), 1.0);
}
