#ifndef HOBIARY_HLOG_INDEX_H
#define HOBIARY_HLOG_INDEX_H

/*
 * hlog_index — etap 2
 * ════════════════════════════════════════════════════════════════════════════
 *
 * Pliki indeksów dla segmentów .hlog:
 *
 *   hlog_NNNNNN.hidx_time   — sparse, wpis co HLOG_IDX_STRIDE rekordów
 *                              używany do szybkiego wyszukiwania po czasie
 *
 *   hlog_NNNNNN.hidx_error  — dense, każdy rekord ERROR lub FATAL
 *                              używany do szybkiego filtrowania błędów
 *
 * Układ pliku:
 *
 *   [HlogIndexFileHeader  32B]
 *   [HlogIndexEntry 0     12B]
 *   [HlogIndexEntry 1     12B]
 *   ...
 *
 * HlogIndexEntry jest współdzielony z hlog_segment.h (typedef struct).
 *
 * ════════════════════════════════════════════════════════════════════════════
 */

#include "hlog_segment.h" /* HLOGDB_API, kody błędów, HlogIndexEntry */
#include <stdint.h>

/* ── Stałe ───────────────────────────────────────────────────────────────── */
#define HLOG_IDX_TIME_MAGIC 0x48494454u  /* "HIDT" */
#define HLOG_IDX_ERROR_MAGIC 0x48494445u /* "HIDE" */
#define HLOG_IDX_LEVEL_MAGIC 0x4849444Cu /* "HIDL" — etap 5 */
#define HLOG_IDX_SRC_MAGIC 0x48494453u   /* "HIDS" — etap 5 */
#define HLOG_IDX_VERSION 1u
#define HLOG_IDX_HEADER_SIZE 32u
#define HLOG_IDX_STRIDE 64u /* co ile rekordów wpis do hidx_time */

/* Rozmiary wpisów indeksów rozszerzonych (etap 5) */
#define HLOG_IDX_LEVEL_ENTRY_SIZE 16u
#define HLOG_IDX_SRC_ENTRY_SIZE 16u

/* ── On-disk nagłówek pliku indeksu (32 bajty, packed) ───────────────────── */
#pragma pack(push, 1)

typedef struct
{
    uint32_t magic;   /* HLOG_IDX_TIME_MAGIC lub HLOG_IDX_ERROR_MAGIC */
    uint16_t version; /* HLOG_IDX_VERSION */
    uint8_t _reserved[2];
    uint64_t segment_seq;  /* numer segmentu, do którego należy ten indeks */
    uint64_t entry_count;  /* liczba wpisów */
    uint32_t header_crc32; /* CRC32 bajtów [0..23] */
    uint32_t _padding;
} HlogIndexFileHeader; /* 32 bajty */

/* ── Rozszerzone struktury wpisów (etap 5, packed) ───────────────────────── */

typedef struct
{
    int64_t timestamp; /* 8B */
    uint32_t offset;   /* 4B */
    uint8_t level;     /* 1B */
    uint8_t _pad[3];   /* 3B */
} HlogLevelIndexEntry; /* 16 bajtów */

typedef struct
{
    int64_t timestamp;  /* 8B */
    uint32_t offset;    /* 4B */
    uint16_t source_id; /* 2B */
    uint8_t _pad[2];    /* 2B */
} HlogSrcIndexEntry;    /* 16 bajtów */

#pragma pack(pop)

/* ── Opaque handle ───────────────────────────────────────────────────────── */
typedef struct HlogIndex HlogIndex;

/* ── API ─────────────────────────────────────────────────────────────────── */
#ifdef __cplusplus
extern "C"
{
#endif

    /*
     * Tworzy nowy plik indeksu.
     * magic: HLOG_IDX_TIME_MAGIC lub HLOG_IDX_ERROR_MAGIC.
     * Zwraca NULL przy błędzie.
     */
    HLOGDB_API HlogIndex *hlog_index_create(const char *path, uint32_t magic,
                                            uint64_t seg_seq);

    /*
     * Otwiera istniejący plik indeksu.
     * Weryfikuje magic, version i header_crc32.
     * Zwraca NULL przy błędzie lub niezgodności magic.
     */
    HLOGDB_API HlogIndex *hlog_index_open(const char *path, uint32_t magic);

    /*
     * Dopisuje wpis do indeksu.
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_index_append(HlogIndex *idx, int64_t timestamp, uint32_t offset);

    /*
     * Zamyka plik indeksu i zwalnia pamięć.
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_index_close(HlogIndex *idx);

    /* Zwraca liczbę wpisów. */
    HLOGDB_API uint64_t hlog_index_entry_count(const HlogIndex *idx);

    /*
     * Odczytuje wpis o podanym numerze (0-based).
     * Zwraca HLOG_OK, HLOG_ERR_NOT_FOUND lub kod błędu.
     */
    HLOGDB_API int hlog_index_read_at(HlogIndex *idx, uint64_t pos,
                                      HlogIndexEntry *out_entry);

    /*
     * Zwraca offset w pliku .hlog dla pierwszego wpisu z timestamp >= ts.
     * Binary search — działa tylko dla posortowanego indeksu czasu.
     * out_ts: opcjonalnie, timestamp znalezionego wpisu.
     * Zwraca HLOG_OK, HLOG_ERR_NOT_FOUND lub kod błędu.
     */
    HLOGDB_API int hlog_index_find_ge(HlogIndex *idx, int64_t ts,
                                      uint32_t *out_offset, int64_t *out_ts);

    /*
     * Wypełnia tablicę out_entries wpisami z timestamp w przedziale [from_ts, to_ts].
     * Zwraca liczbę znalezionych wpisów (może być mniejsza niż max_entries jeśli
     * bufor jest za mały) lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_index_find_range(HlogIndex *idx,
                                         int64_t from_ts, int64_t to_ts,
                                         HlogIndexEntry *out_entries,
                                         int max_entries);

    /* ── Etap 5: indeksy level i source ──────────────────────────────────── */

    /*
     * Visitor callbacks dla indeksów rozszerzonych.
     * Zwróć 0 aby kontynuować, !=0 aby przerwać.
     */
    typedef int (*HlogLevelVisitFn)(int64_t ts, uint32_t offset,
                                    uint8_t level, void *ctx);
    typedef int (*HlogSrcVisitFn)(int64_t ts, uint32_t offset,
                                  uint16_t source_id, void *ctx);

    /* Dopisuje wpis do indeksu level (hidx_level). */
    HLOGDB_API int hlog_level_index_append(HlogIndex *idx, int64_t ts,
                                           uint32_t offset, uint8_t level);

    /* Dopisuje wpis do indeksu source (hidx_src). */
    HLOGDB_API int hlog_src_index_append(HlogIndex *idx, int64_t ts,
                                         uint32_t offset, uint16_t source_id);

    /*
     * Skanuje indeks level — wywołuje fn dla każdego wpisu pasującego do level.
     * Zwraca liczbę pasujących wpisów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_level_index_scan(HlogIndex *idx, uint8_t level,
                                         HlogLevelVisitFn fn, void *ctx);

    /*
     * Skanuje indeks source — wywołuje fn dla każdego wpisu pasującego do source_id.
     * Zwraca liczbę pasujących wpisów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_src_index_scan(HlogIndex *idx, uint16_t source_id,
                                       HlogSrcVisitFn fn, void *ctx);

#ifdef __cplusplus
}
#endif

#endif /* HOBIARY_HLOG_INDEX_H */
