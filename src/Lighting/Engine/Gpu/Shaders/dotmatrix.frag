uniform float u_density; // hint_range(8.0, 30.0, 1.0) = 18.0  dot rows across the frame's height
uniform float u_scrollRate; // hint_range(0.2, 3.0, 0.05) = 1.0  how fast comets and the wash move
uniform float u_complexity; // hint_range(0.0, 1.0, 0.05) = 0.5  how busy: rows with comets, twinkles

// An LED scoreboard: a dim palette wash under comets racing along their rows
// (each row its own direction and speed) and dots that flash and fade. Every
// dot lights at one of a few discrete levels, with a soft halo, like real LEDs.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 uv = uv01();
    float dens = clamp(u_density, 4.0, 40.0);
    float busy = clamp(u_complexity, 0.0, 1.0);
    float t = mod(u_time * u_speed * u_scrollRate, 1000.0);

    // Square dots: rows across the height, as many columns as the width holds.
    vec2 g = vec2(uv.x * ar, uv.y) * dens;
    vec2 cell = floor(g);
    vec2 f = fract(g) - 0.5;
    float cols = ceil(ar * dens);

    // Comets: busier settings light more rows and stretch the tails.
    float r = hash21(vec2(cell.y, 7.13));
    float lit = step(hash21(vec2(cell.y, 3.71)), 0.3 + busy * 0.7);
    float span = cols + 16.0;
    float head = mod(t * (5.0 + r * 9.0) + r * span, span) - 8.0;
    float x = hash21(vec2(cell.y, 5.29)) < 0.5 ? cell.x : cols - 1.0 - cell.x;
    float behind = head - x;
    float comet = behind >= 0.0 ? exp(-behind / (2.0 + busy * 6.0)) * lit : 0.0;

    // Twinkles: a share of the dots flash on their own beat and fade.
    float tw = hash21(cell + 11.3);
    float tp = fract(t * 0.3 + tw * 9.0);
    float twinkle = step(tw, 0.04 + busy * 0.2) * exp(-tp * 7.0);

    // The wash keeps the board alive between comets.
    float wash = 0.5 + 0.5 * sin(cell.x * 0.35 - t * 0.8 + sin(cell.y * 0.3 + t * 0.45) * 2.0);
    float level = max(0.03 + wash * 0.1, max(comet, twinkle));
    level = floor(level * 6.0 + 0.5) / 6.0;

    // A comet's head and a twinkle's flash run hotter, toward white.
    vec3 pal = tintedPalette(fract(cell.x / max(cols, 1.0) * 0.6 + cell.y / dens * 0.25 + t * 0.03));
    vec3 tone = mix(pal, vec3(1.0), smoothstep(0.75, 1.0, level) * 0.45);

    float d = length(f);
    float core = smoothstep(0.42, 0.3, d);
    float halo = exp(-d * 5.0) * 0.35 * level;
    vec3 col = tone * level * (core + halo) * (1.0 + level * 2.5);
    col += vec3(0.012, 0.013, 0.025) * (1.0 - core);

    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.13; }
