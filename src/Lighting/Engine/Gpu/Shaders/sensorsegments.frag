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
const float S_COLORIZE = 0.0;
uniform float u_fill_level; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_fill_level_m; // hint_range(-1.0, 1.0, 0.01) = 1.0
uniform float u_fill_level_s; // sensor {"category":"quick","sensor":"summary/memory-usage","min":0,"max":100}
uniform float u_fill_angle; // hint_range(0.0, 360.0, 1.0) = 0.0
uniform float u_fill_flip; // hint_range(0.0, 1.0, 1.0) = 0.0
uniform float u_fill_count; // hint_range(3.0, 40.0, 1.0) = 10.0
uniform float u_fill_size; // hint_range(0.2, 1.0, 0.01) = 0.8
uniform float u_col_low; // hint_range(0.0, 0.95, 0.01) = 0.5
uniform float u_col_shade; // hint_range(0.0, 1.0, 0.01) = 1.0
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
  float bl = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
  vec3 blended = mix(vec3(bl), col, clamp(u_saturation * S_SAT, 0.0, 4.0));
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
    float ar = u_resolution.x / u_resolution.y; float ra = radians((u_fill_angle)); vec2 dir = vec2(sin(ra), -cos(ra));
    float x = dot(p, dir) / (2.0 * (abs(dir.x) * ar + abs(dir.y))) + 0.5;
    if ((u_fill_flip) > 0.5) x = 1.0 - x;
    float n = floor((u_fill_count) + 0.5); float xc = clamp(x, 0.0, 0.9999); float seg = floor(xc * n); float cell = fract(xc * n);
    float lit = clamp((clamp(u_fill_level + u_fill_level_m * 1.0 * u_fill_level_s, 0.0, 1.0)) * n - seg, 0.0, 1.0);
    f = lit * (1.0 - smoothstep((u_fill_size) * 0.5 - 0.04, (u_fill_size) * 0.5, abs(cell - 0.5))) * (0.45 + 0.55 * (seg + 1.0) / n);
    v = f; }
  {
    float k = clamp((v - (u_col_low)) / (1.0 - (u_col_low)), 0.0, 1.0);
    float span = 1.0 - clamp(u_colorize + S_COLORIZE, 0.0, 1.0);
    col = hsv2rgb(vec3(hue + (0.5 - k) * span, 1.0, 1.0)) * mix(1.0, v, (u_col_shade));
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}