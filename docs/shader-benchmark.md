# Shader FPS Benchmark - Q-series (Q60)

Baseline performance of every non-audio lighting shader rendered as the panel
theme background on the **HYTE Q60** (MediaTek tb8167, Android 11). This is the
pre-optimization baseline; see the "Optimization" section once the heavy shaders
are simplified.

## Method

- Surface: real `PanelBackgroundShader` (nexus-web) rendered as the Q60 panel
  theme background, clock + monitoring widget composited on top - the actual
  shipped path, not an isolated canvas.
- Q60 viewport: 480×854 CSS px (720×1280 physical ÷ 1.5 devicePixelRatio).
  - **Full res** = 480×854 backing store (`maxDevicePixelRatio: 1`).
  - **Half res** = 240×427 backing store (`maxDevicePixelRatio: 0.5`).
- FPS = browser `requestAnimationFrame` render rate of the shader, averaged over
  a ~3s window per resolution. Android's `gfxinfo`/SurfaceFlinger counters were
  unusable - they report the compositor's fixed vsync (~54), not the canvas's
  real update rate, so the meter counts rAF inside the WebGL render loop.
- The panel tops out at **~54 fps** (shader + React + widgets share the frame
  budget); a shader at ~54 is GPU-idle and free.

### Caveats

- The last 12 shaders (`ferrofluid`→`crystaltunnel`, marked `*`) were measured
  after ~8 min of continuous cycling, by which point the panel frame budget had
  sagged to a ~25 fps ceiling. Their **full-res** ranking is valid; their
  **half-res** figures are understated (true half-res is higher).
- `ribbonflow` was not captured (the poller missed the final frame before the
  run signalled done).

## Baseline (heaviest → lightest)

| Shader | Full fps | Half fps | Tier |
|---|---|---|---|
| bursts | 2.0 | 8.7 | brutal |
| watercolor | 3.3 | 14.3 | brutal |
| bubbles* | 3.7 | 10.7 | brutal |
| starfield | 4.0 | 15.7 | brutal |
| lightning | 4.0 | 17.0 | brutal |
| nebula | 4.7 | 18.0 | brutal |
| sandstorm* | 4.7 | 12.7 | brutal |
| mandelbrot* | 5.0 | 17.7 | brutal |
| bokeh* | 5.7 | 14.3 | brutal |
| plasmaglobe | 6.3 | 25.0 | heavy |
| starpath | 8.7 | 28.3 | heavy |
| silkwave* | 9.7 | 19.7 | heavy |
| fire | 10.7 | 37.7 | heavy |
| domainwarp | 10.7 | 38.3 | heavy |
| jellyfish | 11.0 | 37.0 | heavy |
| flowfield | 12.0 | 42.0 | heavy |
| oilslick | 13.0 | 44.7 | heavy |
| circuit* | 16.0 | 23.0 | heavy |
| inkbloom | 16.3 | 51.3 | heavy |
| ferrofluid* | 17.0 | 23.7 | heavy |
| lavafissure | 17.0 | 51.3 | heavy |
| dotmatrix* | 17.0 | 23.7 | heavy |
| ball | 18.3 | 54.7 | medium (old shader, needs re-measurement on the Q60) |
| meteor | 19.3 | 53.7 | medium |
| voronoi | 19.3 | 53.3 | medium |
| prismwave* | 19.3 | 24.3 | medium |
| galaxy | 19.7 | 55.0 | medium |
| liquidchrome* | 20.0 | 24.3 | medium |
| crystaltunnel* | 20.3 | 24.7 | medium |
| hextunnel* | 21.0 | 25.3 | medium |
| caustics | 24.0 | 55.3 | medium |
| aurora | 24.7 | 54.3 | medium |
| sacredgeometry | 26.0 | 53.7 | medium |
| kaleidoscope | 30.7 | 55.0 | light-ish |
| lavalamp | 35.7 | 53.7 | light-ish |
| interference | 42.7 | 53.3 | light-ish |
| neonrain | 44.0 | 55.0 | light-ish |
| cosmicdust | 47.3 | 52.0 | light-ish |
| simplered | 48.3 | 53.3 | light |
| spiral | 52.3 | 54.0 | free |
| simpleyellow | 53.0 | 54.3 | free |
| rainbow | 53.7 | 54.7 | free |
| gradientwave | 53.7 | 54.3 | free |
| ripple | 53.7 | 55.0 | free |
| simplegreen | 54.0 | 53.3 | free |
| simplecyan | 54.0 | 52.3 | free |
| simpleblue | 54.0 | 55.3 | free |
| matrix | 54.0 | 54.3 | free (old shader, needs re-measurement on the Q60) |
| simpleorange | 54.0 | 54.7 | free |
| wave | 54.3 | 54.0 | free |
| radar | 54.3 | 54.7 | free |
| wormhole | 54.3 | 54.3 | free |
| tessellation | 54.3 | 53.7 | free |
| simpleviolet | 54.7 | 54.7 | free |
| simplepink | 54.7 | 54.3 | free |
| plasma | 54.7 | 53.3 | free |
| neongrid | 54.7 | 54.7 | free |
| pulse | 55.0 | 54.3 | free |
| chromaspiral | 55.3 | 54.7 | free |
| ribbonflow | - | - | not captured |

## Reading it

Almost every heavy shader is **fill-rate / per-pixel bound**: half-res roughly
triples or quadruples the rate (watercolor 3→14, fire 11→38, aurora 25→54),
which means the cost is per-pixel work. Simplification levers, in order of
payoff: fewer fbm/noise octaves, fewer raymarch/loop iterations, cheaper
transcendentals (`pow`/`exp`/`length`/`atan`), and smaller kernels.

Worst offenders to simplify first:
**bursts, watercolor, starfield, lightning, nebula, mandelbrot, sandstorm,
bokeh, bubbles, plasmaglobe, starpath, silkwave, fire, domainwarp, jellyfish.**

Shaders pinned at ~54 (solids, plasma, rainbow, matrix, wave, wormhole, …) are
GPU-idle and need no work.
