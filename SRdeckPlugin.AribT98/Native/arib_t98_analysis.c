/* Standalone AMBE parameter analysis (GPL-2.0-or-later).
 * Uses pinned mbelib-neo quantization tables and tone helpers; no PCM synthesis.
 * See THIRD-PARTY-NOTICES.md for attribution and license notices. */
#include <string.h>
#include <math.h>
#include <windows.h>
#include "ambe3600x2450_const.h"
#include "mbe_tone.h"
#define EXPORT __declspec(dllexport)

typedef struct {
    int b0, b1, b2, b3, b4, b5, b6, b7, b8;
} ambe_indices;

static int read_index(const unsigned char *bits, const int *positions, int count) {
    int value = 0, i;
    for (i = 0; i < count; ++i) value = (value << 1) | bits[positions[i]];
    return value;
}

static ambe_indices extract_indices(const unsigned char *bits) {
    static const int b0[] = {0,1,2,3,37,38,39};
    static const int b1[] = {4,5,6,7,35};
    static const int b2[] = {8,9,10,11,36};
    static const int b3[] = {12,13,14,15,16,17,18,19,40};
    static const int b4[] = {20,21,22,23,41,42,43};
    static const int b5[] = {24,25,26,27,44};
    static const int b6[] = {28,29,30,45};
    static const int b7[] = {31,32,33,46};
    static const int b8[] = {34,47,48};
    ambe_indices result;
    result.b0 = read_index(bits, b0, 7);
    result.b1 = read_index(bits, b1, 5);
    result.b2 = read_index(bits, b2, 5);
    result.b3 = read_index(bits, b3, 9);
    result.b4 = read_index(bits, b4, 7);
    result.b5 = read_index(bits, b5, 5);
    result.b6 = read_index(bits, b6, 4);
    result.b7 = read_index(bits, b7, 4);
    result.b8 = read_index(bits, b8, 3);
    return result;
}

static double vector_distance(const float *left, const float *right, int count) {
    double sum = 0.0;
    int i;
    for (i = 0; i < count; ++i) {
        double difference = (double)left[i] - right[i];
        sum += difference * difference;
    }
    return sum;
}

/* Same classification as ambe2450_setup_frame_model / tone-ID validation in
 * the pinned mbelib-neo decoder. Analysis has already rejected errors > 3.
 * 0=speech, 1=silence, 2=verified tone, 3=erasure. No synthesis/state mutation. */
static int classify_parameters(const unsigned char *bits, int b0) {
    int i, tone_prefix = 1, tail = 0, high = 0, low = 0, id = 0;
    for (i = 0; i < 6; ++i) tone_prefix &= bits[i];
    for (i = 45; i < 49; ++i) tail |= bits[i];
    for (i = 0; i < 4; ++i) {
        high = (high << 1) | bits[12 + i];
        low = (low << 1) | bits[20 + i];
    }
    if (tone_prefix && (tail == 0 || high == low)) {
        for (i = 12; i < 20; ++i) id = (id << 1) | bits[i];
        return mbe_tone_id_is_valid(id) ? 2 : 3;
    }
    if (b0 == 124 || b0 == 125) return 1;
    return b0 < 120 ? 0 : 3;
}

EXPORT int arib_t98_classify_parameters(const unsigned char *bits, int count) {
    int i;
    if (!bits || count != 49) return -1;
    for (i = 0; i < count; ++i) if (bits[i] > 1) return -2;
    return classify_parameters(bits, extract_indices(bits).b0);
}

static INIT_ONCE privacy_tables_once = INIT_ONCE_STATIC_INIT;
static unsigned char privacy_masks[32767][4][49];
static double privacy_log_pitch[120];

/* Immutable after initialization, including when separate analyses start
 * concurrently. The PN masks occupy about 6.1 MiB for all non-zero seeds. */
static BOOL CALLBACK initialize_privacy_tables(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    int key, slot, i;
    (void)once; (void)parameter; (void)context;
    for (i = 0; i < 120; ++i) privacy_log_pitch[i] = log((double)AmbeW0table[i]);
    for (key = 1; key <= 32767; ++key) {
        int state = key;
        for (slot = 0; slot < 4; ++slot) {
            for (i = 0; i < 49; ++i) {
                privacy_masks[key - 1][slot][i] = (unsigned char)(state & 1);
                state = (state >> 1) | (((state ^ (state >> 1)) & 1) << 14);
            }
        }
    }
    return TRUE;
}

/*
 * Score every non-zero PN(15,14) seed without synthesizing PCM. A correct
 * standard-privacy seed tends to make adjacent AMBE pitch, gain, spectral and
 * voicing parameters substantially smoother than wrong seeds. slot_markers
 * use bits 0..1 for the PN slot and bit 7 to start a new contiguous run.
 * Lower output values are better; the managed layer ranks the six independent
 * measures and applies a deliberately conservative separation threshold.
 */
EXPORT int arib_t98_score_privacy(const unsigned char *blocks, const unsigned char *slot_markers,
                                  int block_count, int first_key, int *invalid_b0, double *pitch, double *gain,
                                  double *spectrum, double *voicing, double *raw_hamming, int *speech_transitions,
                                  int score_count) {
    unsigned char plain[49], previous_plain[49];
    int key, slot, i, j;
    if (!blocks || !slot_markers || !invalid_b0 || !pitch || !gain || !spectrum ||
        !voicing || !raw_hamming || !speech_transitions || block_count < 2 || first_key < 1 || score_count < 1 ||
        first_key + score_count > 32768) return -1;
    for (i = 0; i < block_count; ++i) {
        if ((slot_markers[i] & 0x7c) != 0) return -2;
        for (j = 0; j < 49; ++j) if (blocks[i * 49 + j] > 1) return -3;
    }
    if (!InitOnceExecuteOnce(&privacy_tables_once, initialize_privacy_tables, NULL, NULL)) return -4;

    for (key = first_key; key < first_key + score_count; ++key) {
        const unsigned char (*masks)[49] = privacy_masks[key - 1];
        int invalid = 0, transitions = 0;
        int have_previous = 0;
        ambe_indices previous = {0};
        double previous_gamma = 0.0;
        double pitch_sum = 0.0, gain_sum = 0.0, spectrum_sum = 0.0;
        double voicing_sum = 0.0, raw_sum = 0.0;

        for (i = 0; i < block_count; ++i) {
            unsigned char marker = slot_markers[i];
            ambe_indices current;
            int kind;
            double gamma;
            if ((marker & 0x80) != 0) {
                have_previous = 0;
                previous_gamma = 0.0;
            }
            slot = marker & 3;
            for (j = 0; j < 49; ++j) plain[j] = blocks[i * 49 + j] ^ masks[slot][j];
            current = extract_indices(plain);
            kind = classify_parameters(plain, current.b0);
            if (kind == 3) ++invalid;
            /* Pitch/codebook distances describe speech only. Neither valid
             * silence nor tone is an invalid pitch, and do not bridge them. */
            if (kind != 0) {
                have_previous = 0;
                previous_gamma = 0.0;
                continue;
            }
            gamma = AmbeDg[current.b2] + 0.5 * previous_gamma;

            if (have_previous) {
                double difference;
                ++transitions;
                difference = privacy_log_pitch[current.b0] - privacy_log_pitch[previous.b0];
                pitch_sum += difference * difference;

                difference = gamma - previous_gamma;
                gain_sum += difference * difference < 16.0 ? difference * difference : 16.0;
                spectrum_sum += vector_distance(AmbePRBA24[current.b3], AmbePRBA24[previous.b3], 3);
                spectrum_sum += vector_distance(AmbePRBA58[current.b4], AmbePRBA58[previous.b4], 4);
                spectrum_sum += 0.35 * vector_distance(AmbeHOCb5[current.b5], AmbeHOCb5[previous.b5], 4);
                spectrum_sum += 0.35 * vector_distance(AmbeHOCb6[current.b6], AmbeHOCb6[previous.b6], 4);
                spectrum_sum += 0.35 * vector_distance(AmbeHOCb7[current.b7], AmbeHOCb7[previous.b7], 4);
                spectrum_sum += 0.35 * vector_distance(AmbeHOCb8[current.b8], AmbeHOCb8[previous.b8], 4);
                for (j = 0; j < 8; ++j)
                    if (AmbeVuv[current.b1][j] != AmbeVuv[previous.b1][j]) voicing_sum += 1.0;
                for (j = 0; j < 49; ++j) if (plain[j] != previous_plain[j]) raw_sum += 1.0;
            }

            memcpy(previous_plain, plain, 49);
            previous = current;
            previous_gamma = gamma;
            have_previous = 1;
        }

        i = key - first_key;
        invalid_b0[i] = invalid;
        speech_transitions[i] = transitions;
        if (transitions > 0) {
            pitch[i] = pitch_sum / transitions;
            gain[i] = gain_sum / transitions;
            spectrum[i] = spectrum_sum / transitions;
            voicing[i] = voicing_sum / transitions;
            raw_hamming[i] = raw_sum / transitions;
        } else {
            pitch[i] = gain[i] = spectrum[i] = voicing[i] = raw_hamming[i] = 1.0e30;
        }
    }
    return score_count;
}
