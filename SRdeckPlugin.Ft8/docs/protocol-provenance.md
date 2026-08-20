# FT8, FT4, and JT65 protocol implementation provenance

The FT8 plugin implements the published FT8 and FT4 protocols directly and
also provides a JT65A receiver. It has no runtime, package, source, or
derived-code dependency on `ft8_lib`.

## Normative protocol sources

* Franke, Somerville, and Taylor, *The FT4 and FT8 Communication Protocols*,
  QEX, July/August 2020: https://wsjt.sourceforge.io/FT4_FT8_QEX.pdf
* The accompanying `ft4_ft8_protocols.tgz` reference package distributed by
  ARRL: https://www.arrl.org/files/file/QEX%20Binaries/2020/ft4_ft8_protocols.tgz
* Joe Taylor, *The JT65 Communications Protocol*, QEX,
  September/October 2005: https://wsjt.sourceforge.io/JT65.pdf
* WSJT-X 3.0.1 source distribution, specifically `gen65.f90`,
  `interleave63.f90`, `graycode65.f90`, and the Phil Karn Reed-Solomon codec:
  https://sourceforge.net/projects/wsjt/files/wsjtx-3.0.1/

The article states that its protocol description and the accompanying
reference resources are public domain. The protocol package defines the
CRC-14, source encoding, and the `generator.dat` / `parity.dat` LDPC matrices.

## External band list

FT8, FT4, and JT65 dial-frequency profiles are loaded at application startup
from `SRdeckPlugin.Ft8.bands.json` beside `SRdeckPlugin.Ft8.dll`. The file uses
`schemaVersion: 1`; each item in `bands` contains `id`, `band`,
`dialFrequencyHz`, `region`, and `mode`. IDs must be unique, frequencies must
be positive, and the list must contain the default `ft8-band-20m` entry. FT8
profile IDs use the `ft8-` prefix; legacy saved `band-*` IDs are migrated when
settings are loaded. Restart the application after editing the file.

The distributed JSON is also embedded as a recovery copy. If the external
file is missing or invalid, the plugin uses that copy and writes the
`ft8.bands.fallback` warning to the plugin log.

The default frequencies use pages 7-8 of the JARL KANHAM2026 presentation
*デジタルモードFT8の活用術* as the Japanese operating reference:
https://jarl.gr.jp/kanham2026/wp-content/uploads/2026/07/d42e8c8215d8e864fa5fd33a7fda67db.pdf
Profiles not covered by that table, such as 60 m, 4 m, and 1.25 m, are retained
as complementary WSJT-X profiles. The JT9 column is not exposed until the
plugin has a JT9 decoder.

## Implementation choices

* CRC-14 uses the published `0x6757` polynomial with an initial value of zero.
* The `(174,91)` LDPC parity-check matrix is reconstructed as zero-based row
  lists from the published `parity.dat` column triples. The systematic encoder
  derives its generator form by GF(2) elimination at startup.
* The receiver uses an independently implemented FFT waterfall synchronizer,
  Gray soft demapper, sum-product LDPC decoder, fine synchronization, and
  successive interference cancellation.
* FT8 has 8-GFSK at 6.25 baud with three `3,1,4,0,6,5,2` Costas sequences.
* FT4 reuses the same 77-bit source encoding, CRC-14, and LDPC(174,91) code.
  It uses 4-GFSK at 20.833 baud and the four published 4x4 Costas arrays.
* JT65A uses 126 symbols at 11025/4096 baud. Its 63 data symbols are protected
  by RS(63,12), transposed through the published 7x9 interleaver, Gray-coded,
  and placed in the zero positions of the published 126-bit sync sequence.
* The JT65 Reed-Solomon implementation is a C# adaptation of Phil Karn's
  GPL-licensed codec distributed with WSJT-X. Attribution is retained in
  `SRdeckPlugin.Ft8/THIRD-PARTY-NOTICES.md`.

For reproducibility, the SHA-256 values of the downloaded public reference
data used during this migration were:

* `parity.dat`: `A9A1E1CD67D3C83B078C16D2AD83807CE6F5A6AAF84839A2CF5875353909085E`
* `generator.dat`: `A803E478E99441043A50819470CBB3866CD72C28B63989792941211BF37A6DC5`
