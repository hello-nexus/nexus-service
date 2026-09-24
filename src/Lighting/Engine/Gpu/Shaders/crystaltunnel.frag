uniform float u_facets; // hint_range(3.0, 16.0, 1.0) = 8.0  angular facet count
uniform float u_depth; // hint_range(0.4, 4.0, 0.05) = 1.0  section frequency along tunnel axis
uniform float u_refract; // hint_range(0.0, 3.0, 0.05) = 1.0  chroma streak intensity

// Forward-facing faceted tunnel. Polar map -> facet x section grid; each
// cell picks a hashed palette slot, edges glow on facet+section borders,
// and a sin streak across each facet reads as light refracting through
// the crystal wall. Distinct from hextunnel (hex grid wrapped on a tube)
// and wormhole (smooth radial bands) by the hard polygonal geometry.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.45;
    float facets = clamp(u_facets, 3.0, 16.0);
    float depth = clamp(u_depth, 0.4, 4.0);
    float refr = clamp(u_refract, 0.0, 3.0);

    float r = length(uv) + 0.001;
    float a = atan(uv.y, uv.x);

    // Forward depth = 1/r so the centre is far, scrolling pulls cells in.
    float z = (1.0 / r) * depth - t;
    float section = floor(z);
    float zLocal = fract(z);

    // Angular cell
    float fa = (a + 3.14159265) / 6.28318530 * facets;
    float facet = floor(fa);
    float aLocal = fract(fa);

    // Per-cell palette slot with a small depth gradient so colour bands
    // sweep along the tunnel as you fly.
    float palT = hash21(vec2(facet, section)) + section * 0.04 + t * 0.05;
    vec3 cellColor = tintedPalette(palT);

    // Edge glow: sharper than hextunnel's hex-distance to read as "cut crystal".
    float facetEdge = pow(1.0 - abs(aLocal - 0.5) * 2.0, 14.0);
    float sectionEdge = pow(1.0 - abs(zLocal - 0.5) * 2.0, 10.0);
    float edge = facetEdge * 0.7 + sectionEdge * 0.5;

    // Refraction streak across each facet face. The phase carries time so
    // light "moves" within each section.
    // perf: single streak layer (dropped the half-offset second sin) - keeps the
    // moving band, loses a barely-visible interference ripple.
    float streak = 0.5 + 0.5 * sin(aLocal * 6.28318 * (1.0 + refr) + t * 1.4 + section * 0.8);
    streak = pow(streak, 2.5);

    // Compose: dim filled cell + bright streaks + edge highlight on top.
    vec3 col = cellColor * 0.25;
    col += cellColor * streak * 0.9;
    col += tintedPalette(palT + 0.15) * edge * (1.4 + refr * 0.4);

    // Depth fog so the throat doesn't read as flat-coloured noise.
    float fog = smoothstep(0.0, 1.6, r);
    col *= fog;

    // Bright vanishing-point core.
    float core = exp(-r * 5.0);
    col += vec3(1.0, 0.95, 0.85) * core * 0.45;

    // Faint ambient so corners don't crush.
    col += tintedPalette(t * 0.08) * 0.04;

    fragColor = vec4(finalize(col), 1.0);
}
