#version 330 core
out vec4 fragColor;
uniform vec2  u_resolution;
uniform float u_time;
uniform float u_speed;      // hint_range(-2.0, 2.0, 0.02) = 1.0
uniform float u_hue;        // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_colorize;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_saturation; // hint_range(0.0, 4.0, 0.01) = 1.0
uniform float u_contrast;   // hint_range(0.0, 4.0, 0.01) = 1.0
const float PI = 3.14159265;
const float S_SPEED = 1.0;
const float S_HUE = 0.0;
const float S_SAT = 1.0;
const float S_CON = 1.0;
uniform float u_fill_level; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_fill_level_m; // hint_range(-1.0, 1.0, 0.01) = 1.0
uniform float u_fill_level_s; // sensor {"category":"quick","sensor":"summary/gpu-usage","min":0,"max":100}
uniform float u_fill_angle; // hint_range(0.0, 360.0, 1.0) = 0.0
uniform float u_fill_flip; // hint_range(0.0, 1.0, 1.0) = 0.0
uniform float u_fill_span; // hint_range(90.0, 360.0, 1.0) = 240.0
uniform float u_fill_radius; // hint_range(0.2, 1.2, 0.01) = 0.65
uniform float u_fill_width; // hint_range(0.1, 1.2, 0.01) = 0.4
uniform float u_fill_track; // hint_range(0.0, 0.5, 0.01) = 0.1
uniform float u_col_low; // hint_range(0.0, 0.95, 0.01) = 0.55
uniform float u_col_shade; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_col_c1r; // hint_range(0.0, 1.0, 0.001) = 0.1
uniform float u_col_c1g; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_col_c1b; // hint_range(0.0, 1.0, 0.001) = 0.4
uniform float u_col_c2r; // hint_range(0.0, 1.0, 0.001) = 0.6
uniform float u_col_c2g; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_col_c2b; // hint_range(0.0, 1.0, 0.001) = 0.2
uniform float u_col_c3r; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_col_c3g; // hint_range(0.0, 1.0, 0.001) = 0.85
uniform float u_col_c3b; // hint_range(0.0, 1.0, 0.001) = 0.1
uniform float u_col_c4r; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_col_c4g; // hint_range(0.0, 1.0, 0.001) = 0.15
uniform float u_col_c4b; // hint_range(0.0, 1.0, 0.001) = 0.1
vec2 uvCentered() {
  vec2 u = gl_FragCoord.xy / u_resolution;
  u.y = 1.0 - u.y;
  u = u * 2.0 - 1.0;
  u.x *= u_resolution.x / u_resolution.y;
  return u;
}
vec3 hsv2rgb(vec3 c) {
  vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
  return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}
vec3 finalize(vec3 col, float hue) {
  float luma = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
  vec3 blended = mix(col, luma * hsv2rgb(vec3(hue, 1.0, 1.0)) * 1.4, clamp(u_colorize, 0.0, 1.0));
  float bl = dot(max(blended, 0.0), vec3(0.2126, 0.7152, 0.0722));
  blended = mix(vec3(bl), blended, clamp(u_saturation * S_SAT, 0.0, 4.0));
  blended = (blended - 0.5) * clamp(u_contrast * S_CON, 0.0, 4.0) + 0.5;
  return max(blended, 0.0);
}
void main() {
  vec2 p0 = uvCentered();
  vec2 p = p0;
  float t = mod(u_time, 3600.0) * u_speed * S_SPEED;
  float hue = u_hue + S_HUE;
  float v = 0.0;
  vec3 col = vec3(0.0);
  { float f = 0.0;
    float da = mod(degrees(atan(p.x, -p.y)) - (u_fill_angle) + 180.0, 360.0) - 180.0;
    float g = (da + (u_fill_span) * 0.5) / (u_fill_span);
    if ((u_fill_flip) > 0.5) g = 1.0 - g;
    float r = length(p); float lo = (u_fill_radius) - (u_fill_width) * 0.5; float hi = (u_fill_radius) + (u_fill_width) * 0.5;
    float band = smoothstep(lo - 0.03, lo, r) * (1.0 - smoothstep(hi, hi + 0.03, r));
    float inArc = smoothstep(0.0, 0.02, g) * (1.0 - smoothstep(0.98, 1.0, g));
    float lit = 1.0 - smoothstep((clamp(u_fill_level + u_fill_level_m * 1.0 * u_fill_level_s, 0.0, 1.0)) * 1.03 - 0.03, (clamp(u_fill_level + u_fill_level_m * 1.0 * u_fill_level_s, 0.0, 1.0)) * 1.03, g);
    f = band * inArc * mix((u_fill_track), 0.55 + 0.45 * clamp(g, 0.0, 1.0), lit);
    v = f; }
  {
    float k = clamp((v - (u_col_low)) / (1.0 - (u_col_low)), 0.0, 1.0);
    vec3 g = mix((vec3(u_col_c1r, u_col_c1g, u_col_c1b)), (vec3(u_col_c2r, u_col_c2g, u_col_c2b)), smoothstep(0.0, 0.3333, k));
    g = mix(g, (vec3(u_col_c3r, u_col_c3g, u_col_c3b)), smoothstep(0.3333, 0.6667, k));
    g = mix(g, (vec3(u_col_c4r, u_col_c4g, u_col_c4b)), smoothstep(0.6667, 1.0, k));
    col = g * mix(1.0, v, (u_col_shade));
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}