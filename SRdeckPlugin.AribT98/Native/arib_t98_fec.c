/* SRdeck channel FEC only, adapted from mbelib-neo (GPL-2.0-or-later).
 * Bitplane schedule adapted from DSD include/nxdn_const.h (ISC):
 * Copyright (C) 2010 DSD Author. Full notice in THIRD-PARTY-NOTICES.md. */
#include <string.h>
#include "ambe_common.h"

/* Golay channel-FEC generator; the upstream internal symbol is not exported. */
static const int golayGenerator[12] = {0x63a,0x31d,0x7b4,0x3da,0x1ed,0x6cc,0x366,0x1b3,0x6e3,0x54b,0x49f,0x475};

#define EXPORT __declspec(dllexport)
/* Input/output bits are individual bytes, MSB first. Bounds are checked at
 * this boundary as well as by the managed caller. */
    static const int w[36] = {0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,2,0,2,0,2,0,2,0,2,0,2,0,2};
    static const int x[36] = {23,10,22,9,21,8,20,7,19,6,18,5,17,4,16,3,15,2,14,1,13,0,12,10,11,9,10,8,9,7,8,6,7,5,6,4};
    static const int y[36] = {0,2,0,2,0,2,0,2,0,3,0,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3};
    static const int z[36] = {5,3,4,2,3,1,2,0,1,13,0,12,22,11,21,10,20,9,19,8,18,7,17,6,16,5,15,4,14,3,13,2,12,1,11,0};
EXPORT int arib_t98_unpack(const unsigned char *bits, int count, unsigned char *data, int size, int *c0) {
    char frame[4][24] = {{0}};
    int i;
    if (!bits || !data || !c0 || count != 72 || size < 49) return -1;
    for (i = 0; i < count; ++i) if (bits[i] > 1) return -2;
    for (i = 0; i < 36; ++i) {
        frame[w[i]][x[i]] = (char)bits[2*i];
        frame[y[i]][z[i]] = (char)bits[2*i+1];
    }
    *c0 = mbe_eccAmbe3600C0_common(frame);
    mbe_demodulateAmbe3600Data_common(frame);
    return *c0 + mbe_eccAmbe3600Data_common(frame, (char *)data);
}

static int golay_codeword_valid(const char *bits) {
    int i, expected = 0, parity = 0;
    for (i = 0; i < 12; ++i) if (bits[22 - i]) expected ^= golayGenerator[i];
    for (i = 10; i >= 0; --i) parity = (parity << 1) | bits[i];
    return parity == expected;
}

EXPORT int arib_t98_unpack_soft(const unsigned char *bits, const unsigned char *reliability,
                               int count, unsigned char *data, int size, int *c0, int *unprotected_minimum,
                               int allow_fast_path) {
    mbe_soft_bit frame[4][24] = {{{0}}};
    char hard[4][24] = {{0}};
    int i, minimum = 255;
    if (!bits || !reliability || !data || !c0 || !unprotected_minimum || count != 72 || size < 49) return -1;
    for (i = 0; i < count; ++i) if (bits[i] > 1) return -2;
    for (i = 0; i < 36; ++i) {
        frame[w[i]][x[i]] = (mbe_soft_bit){bits[2*i], reliability[2*i]};
        frame[y[i]][z[i]] = (mbe_soft_bit){bits[2*i+1], reliability[2*i+1]};
        hard[w[i]][x[i]] = (char)bits[2*i];
        hard[y[i]][z[i]] = (char)bits[2*i+1];
        if (w[i] >= 2 && reliability[2*i] < minimum) minimum = reliability[2*i];
        if (y[i] >= 2 && reliability[2*i+1] < minimum) minimum = reliability[2*i+1];
    }
    *unprotected_minimum = minimum;
    /* A zero-syndrome received codeword has weighted mismatch cost zero for
     * every confidence vector. The vendor's tie rule prefers its hard result.
     * Check BOTH full 23-bit words, not the reported corrected-data count (that
     * count can be zero with corrupt parity). C0 supplies C1's PN sequence. */
    if (allow_fast_path && golay_codeword_valid(hard[0] + 1)) {
        mbe_demodulateAmbe3600Data_common(hard);
        if (golay_codeword_valid(hard[1])) return arib_t98_unpack(bits, count, data, size, c0);
    }
    *c0 = mbe_eccAmbe3600C0Soft_common(frame);
    mbe_demodulateAmbe3600DataSoft_common(frame);
    return *c0 + mbe_eccAmbe3600DataSoft_common(frame, (char *)data);
}
