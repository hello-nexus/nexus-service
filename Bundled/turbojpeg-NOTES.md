# libjpeg-turbo (TurboJPEG 3 API)

Used by `src/Rendering/TurboJpeg.cs` for every server-rendered device JPEG. Every
caller falls back to ImageSharp when the library is absent, so a missing copy costs
speed, not function.

| | |
| --- | --- |
| Version | **3.2.0** (build 20260630), both platforms |
| Upstream | https://github.com/libjpeg-turbo/libjpeg-turbo/releases/tag/3.2.0 |
| License | BSD-3-Clause + IJG - see `turbojpeg-LICENSE.md` and `turbojpeg-README.ijg` |

## win-x64/turbojpeg/turbojpeg.dll

From the official `libjpeg-turbo-3.2.0-vc64.exe` installer payload.
sha256 `3fbbfc901b08dc376adbb3d7edd46415a6dbc599abc5ae256a7c30384b61b22a`

**Unsigned by upstream.** It enters the Windows installer payload, and this workspace
has failed Smart App Control validation on unsigned DLLs before - confirm the installer
signs it before shipping a release that carries it.

## osx-arm64/turbojpeg/libturbojpeg.dylib

From the official `libjpeg-turbo-3.2.0.dmg` -> `libjpeg-turbo.pkg` ->
`opt/libjpeg-turbo/lib/libturbojpeg.0.5.0.dylib`. Universal (x86_64 + arm64) as shipped.

Two local changes, both required:
1. `install_name_tool -id "@loader_path/libturbojpeg.dylib"` - upstream's `LC_ID_DYLIB`
   points at its own install prefix.
2. `codesign --force -s -` to restore the signature that step 1 invalidates.
   `Bundled/macos/sign-notarize.sh` re-signs it with the Developer ID at package time.

sha256 `9ec31f880327f09b7d0300e761ce662db611fe2b82030328bfc24272449889fa`
(after both steps; it will not match the upstream payload byte for byte)

## Updating

Take both binaries from the same upstream release. Re-run the two macOS steps above,
update the versions and hashes here, and re-run `Bundled_turbojpeg_is_the_active_encoder`
plus the channel-order tests in `BgraJpegEncoderTests` - a wrong TJPF_* constant is
otherwise silent.
