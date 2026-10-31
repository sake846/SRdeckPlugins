# AMBE channel FEC and dongle transport: third-party notices

These sources are compiled into each consuming plugin. The combined plugin
uses GPL-3.0-only. They do not synthesize speech; audio requires an external
AMBE-3000 dongle.

The D-STAR RATEP values and 72-bit channel packet layout were checked against
DroidStar's `serialambe.cpp` (Copyright (C) 2019-2021 Doug McLain,
GPL-3.0-or-later): https://github.com/nostar/DroidStar/blob/main/serialambe.cpp.
The serial transport implementation is shared with the existing AMBE+2 path.

## mbelib-neo channel-decoding conventions

The managed channel FEC and Golay generator are adapted from mbelib-neo,
commit `eb992d35b4b09959ff16990adf9b160e3a720ba9`:
https://github.com/arancormonk/mbelib-neo

Copyright (C) 2025 arancormonk; Copyright (C) 2010 mbelib Author.
License: GPL-2.0-or-later. See `LICENSES/GPL-2.0-or-later.txt` in source or
`licenses/ambe/GPL-2.0-or-later.txt` in a binary distribution.
The managed corresponding source is `AmbeChannelFec.cs`; it does not link
or load mbelib-neo or a native codec DLL.

## DSD AMBE bitplane schedule

Adapted from https://github.com/szechyjs/dsd/blob/master/include/nxdn_const.h.
The schedule is checked against published AMBE channel-bit vectors.

Copyright (C) 2010 DSD Author
GPG Key ID: 0x3F1D7FD0 (74EF 430D F7F2 0A48 FCE6 F630 FAA2 635D 3F1D 7FD0)

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL ISC BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
