uniform float u_density; // hint_range(1.0, 10.0, 0.5) = 5.0  colour bars across the frame
uniform float u_rotation; // hint_range(0.0, 360.0, 5.0) = 0.0  bar direction in degrees
uniform float u_position; // hint_range(0.05, 1.0, 0.01) = 0.5  split point inside each bar's cell

// Hard-edged colour bars separated by unlit gaps, sliding across the frame.
// Every band is one flat hue with no blend at all, and the space between bands
// is black. Density counts bars here, so it does not match rainbow.frag's
// density, and axis01 turns Rotation the opposite way to that shader.
void main() {
    float dens = max(1.0, u_density);
    // Bar index along the rotated sweep; the whole ladder slides with time.
    float f = axis01(u_rotation) * dens - u_time * u_speed * 0.5;
    float cell = floor(f);
    // Colour up to the split point in each cell, black past it, so the slider
    // trades bar width against gap width the way splitsharp's position does.
    // The upper bound is a real 1.0: frac never reaches it, so the top of the
    // range closes the gaps entirely and the bars butt up against each other.
    float lit = 1.0 - step(clamp(u_position, 0.05, 1.0), f - cell);
    // A fixed hue step per bar, NOT per frame width: scaling it by density
    // would put adjacent bars a full cycle apart at low counts.
    vec3 col = hsv2rgb(vec3(cell * 0.2 + u_hue, 1.0, 1.0)) * lit;
    fragColor = vec4(finalize(col), 1.0);
}
