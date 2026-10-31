# ARIB STD-T98 receiver: third-party notices

These notices accompany the T98 plugin in source snapshots and plugin-inclusive
executable packages. Matching native source and build definitions are included
in the source snapshot. The ARIB PDF is a user-supplied reference and is not redistributed.

The internal dongle transport and managed FEC are compiled from
`../shared/Ambe`. Retain `AMBE.THIRD-PARTY-NOTICES.md` and `licenses/ambe` in
binary distributions as well as the native notices below.

## mbelib-neo

- Source: https://github.com/arancormonk/mbelib-neo
- Pinned commit: eb992d35b4b09959ff16990adf9b160e3a720ba9 (API 2.0.0)
- License: GPL-2.0-or-later. The combined plugin uses GPL-3.0-only.
- Vendored source, include files, build definitions and license documentation are
  unmodified under `Native/vendor/mbelib-neo`. Tests, tools and examples are omitted.
- See the vendored LICENSE, LICENSES, docs/upstream-mbelib-attribution.md and
  src/external/pffft source headers for retained upstream and FFT notices.
- The FEC module builds only the upstream ECC and common channel-decoding
  helpers; no software speech synthesizer is built or linked.
- The independent analysis module retains upstream quantization tables and
  tone-classification helpers for privacy analysis. It does not synthesize PCM.
  Managed channel FEC in `../shared/Ambe/AmbeChannelFec.cs`
  follows the pinned ECC/AMBE channel-decoding conventions and Golay generator.
  These portions remain subject to the above notices and license even when
  audio synthesis is performed by an AMBE dongle.

The upstream project includes a patent notice. An open-source copyright license
is not a patent license; private use alone does not establish that no rights or
other restrictions apply. This project makes no such determination.

## DSD AMBE bitplane schedule

Adapted from https://github.com/szechyjs/dsd/blob/master/include/nxdn_const.h.
The schedule is checked against the supplied STD-T98 interconnection vectors.

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
