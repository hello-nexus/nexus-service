# dfu-util (linux-x64)

Static musl build of dfu-util 0.11, statically linked against libusb 1.0.27
(udev backend disabled, so there is no libudev dependency). Used by the
firmware flasher; `ldd` reports "not a dynamic executable".

| Input | URL | SHA-256 |
| --- | --- | --- |
| dfu-util-0.11.tar.gz | https://dfu-util.sourceforge.net/releases/dfu-util-0.11.tar.gz | b4b53ba21a82ef7e3d4c47df2952adf5fa494f499b6b0b57c58c5d04ae8ff19e |
| libusb-1.0.27.tar.bz2 | https://github.com/libusb/libusb/releases/download/v1.0.27/libusb-1.0.27.tar.bz2 | ffaa41d741a8a3bee244ac8e54a72ea05bf2879663c098c82fc5757853441575 |

The dfu-util tarball matches the maintainer-published MD5
(31c983543a1fe8f03260ca4d56ad4f43), whose signature verifies against key
C6818737 9B23DE9E FC46 651E 2C80FF56 C6830A0E (Tormod Volden).

Resulting `dfu-util` SHA-256:
021d45e8842b3e56d1b76a1de2edf03f8593089b1f598c3de7b18702327c9aa8

Build, in `alpine:3.20` with `--platform linux/amd64`
(`apk add build-base curl pkgconf linux-headers`):

```sh
cd libusb-1.0.27
./configure --prefix=/work/prefix --disable-shared --enable-static --disable-udev
make && make install
cd ../dfu-util-0.11
PKG_CONFIG_PATH=/work/prefix/lib/pkgconfig ./configure LDFLAGS=-static
make LDFLAGS=-static
strip src/dfu-util
```

COPYING is dfu-util's GPL-2.0; COPYING.libusb is libusb's LGPL-2.1. Rebuild
from the sources above to relink against a modified libusb.
