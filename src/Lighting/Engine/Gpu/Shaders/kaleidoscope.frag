uniform float u_sides; // hint_range(3.0, 12.0, 1.0) = 8.0  fold count
uniform float u_spin; // hint_range(-2.0, 2.0, 0.05) = 0.4  rotation rate
uniform float u_inner; // hint_range(0.3, 3.0, 0.05) = 1.2  inner pattern scale

// N-fold radial kaleidoscope. Polar-fold the coordinate into one wedge,
// mirror across its center line, then sample a flowing noise pattern so
// the mirror image remains organic. Hue varies with angle through the
// central motif so rotations read as rainbow bloom.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.35, 1000.0);
    int N = int(clamp(u_sides, 3.0, 12.0));
    float sides = float(N);
    float spin = u_spin;
    float inner = max(0.3, u_inner);

    float r = length(uv);
    float a = atan(uv.y, uv.x) + t * spin;
    float sector = 6.28318530718 / sides;
    // Fold into sector, then mirror across its centerline.
    float f = mod(a, sector);
    f = abs(f - sector * 0.5);
    vec2 p = vec2(cos(f), sin(f)) * r;

    // Inner motif: drifting fbm + concentric ripples.
    vec2 s = p * inner * 3.2 + vec2(t * 0.3, -t * 0.2);
    float motif = fbm(s) + 0.25 * sin(length(s) * 7.0 - t * 1.5);
    float hue = motif * 0.45 + 0.1 * sin(r * 5.0 - t);
    vec3 col = tintedPalette(hue);

    // Radial falloff: dark hole near the dead center, faded edges.
    col *= smoothstep(0.0, 0.08, r);
    col *= 1.0 - smoothstep(0.85, 1.35, r) * 0.55;
    // Brighten the mirror seam so the facet edges catch light.
    col += tintedPalette(hue + 0.3) * pow(max(1.0 - f / (sector * 0.5), 0.0), 6.0) * 0.35;

    fragColor = vec4(finalize(col * 1.2), 1.0);
}
