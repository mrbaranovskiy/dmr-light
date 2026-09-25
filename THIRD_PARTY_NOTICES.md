# Reference provenance and third-party notices

The receiver is implemented in managed C#. Runtime signalling code follows the supplied ETSI TS 102 361-1 V2.1.1 (2012-04), with conventional LC semantics from TS 102 361-2 V2.2.1 (2013-07). The generator extracts the numeric parity matrices from tables B.11-B.17; the idle-vector test uses Annex D. The supplied standards retain their original copyrights.

AMBE mapping and Golay generator constants in `src/Dmr/Vocoder/Ambe.cs` are adapted from these pinned ISC-licensed sources:

- [szechyjs/dsd, dmr_const.h](https://github.com/szechyjs/dsd/blob/59423fa46be8b41ef0bd2f3d2b45590600be29f0/include/dmr_const.h), commit `59423fa46be8b41ef0bd2f3d2b45590600be29f0`.
- [szechyjs/mbelib, ecc_const.h](https://github.com/szechyjs/mbelib/blob/9a04ed5c78176a9965f3d43f7aa1b1f5330e771f/ecc_const.h), and the channel-decoding sequence in `ambe3600x2450.c`, commit `9a04ed5c78176a9965f3d43f7aa1b1f5330e771f`.

The optional fixture-regeneration tool downloads and compiles the native upstream decoder. WAV export can load a user-supplied native mbelib 1.3.0 library at runtime. No native library is linked or shipped with the receiver. The frozen vectors record channel-decoding comparisons; they do not imply that encrypted traffic is intelligible.

## ISC notices

Copyright (C) 2010 DSD Author

GPG Key ID: 0x3F1D7FD0 (74EF 430D F7F2 0A48 FCE6 F630 FAA2 635D 3F1D 7FD0)

Copyright (C) 2010 mbelib Author

GPG Key ID: 0xEA5EFE2C (9E7A 5527 9CDC EBF7 BF1B D772 4F98 E863 EA5E FE2C)

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL ISC BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
