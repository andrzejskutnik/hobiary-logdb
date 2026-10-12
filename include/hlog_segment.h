#ifndef HOBIARY_HLOG_SEGMENT_H
#define HOBIARY_HLOG_SEGMENT_H

/*
 * hlog_segment — etap 1
 * ════════════════════════════════════════════════════════════════════════════
 *
 * Zapis i odczyt pojedynczego pliku segmentu (.hlog).
 *
 * Układ pliku:
 *
 *   [HlogSegmentFileHeader  64B]      offset 0
 *   [StringTable block    4096B]      offset 64
 *   [LogRecord 0]                     offset 4160  (HLOG_FIRST_RECORD_OFFSET)
 *   [LogRecord 1]
 *   ...
 *
 * LogRecord = [HlogRecordHeader 80B] + [payload_len B] + [uint32_t crc32]
 *
 * ════════════════════════════════════════════════════════════════════════════
 */

#include <stdint.h>
#include <stddef.h>

/* ── Export macro ────────────────────────────────────────────────────────── */
#ifdef _WIN32
#ifdef HOBIARYLOGDB_EXPORTS
#define HLOGDB_API __declspec(dllexport)
#else
#define HLOGDB_API __declspec(dllimport)
#endif
#else
#define HLOGDB_API
#endif

/* ── Kody błędów ─────────────────────────────────────────────────────────── */
#define HLOG_OK 0
#define HLOG_ERR_IO (-1)
#define HLOG_ERR_ARGS (-2)
#define HLOG_ERR_CORRUPT (-3)
#define HLOG_ERR_FULL (-4)      /* StringTable lub segment pełny */
#define HLOG_ERR_SEALED (-5)    /* próba zapisu do zamkniętego segmentu */
#define HLOG_ERR_NOT_FOUND (-6) /* brak wyników / wpisu */

/* ── Stałe on-disk ───────────────────────────────────────────────────────── */
#define HLOG_FILE_MAGIC 0x484C4F47u /* "HLOG" */
#define HLOG_RECORD_MAGIC 0xA5u
#define HLOG_VERSION 1u

/* Flagi nagłówka pliku */
#define HLOG_FLAG_COMPRESSED (1u << 0)
#define HLOG_FLAG_ENCRYPTED (1u << 1)
#define HLOG_FLAG_SEALED (1u << 2)

/* Flagi rekordu */
#define HLOG_RFLAG_HAS_TRACE (1u << 0)
#define HLOG_RFLAG_HAS_SPAN (1u << 1)
#define HLOG_RFLAG_HAS_ENTITY (1u << 2)
#define HLOG_RFLAG_PAYLOAD_JSON (1u << 3)
#define HLOG_RFLAG_PAYLOAD_TRUNC (1u << 4) /* payload przycięty do HLOG_MAX_PAYLOAD */

/* Limity */
#define HLOG_STRTABLE_BLOCK 4096u                                       /* bajty zarezerwowane na StringTable */
#define HLOG_STRTABLE_CAPACITY 256u                                     /* max unikalnych stringów per segment */
#define HLOG_STR_HEADER_SIZE (2u + 2u * HLOG_STRTABLE_CAPACITY)         /* 514 */
#define HLOG_STR_DATA_SIZE (HLOG_STRTABLE_BLOCK - HLOG_STR_HEADER_SIZE) /* 3582 */
#define HLOG_MAX_PAYLOAD 65536u                                         /* 64 KB */
#define HLOG_MAX_SEGMENT_SIZE (64u * 1024u * 1024u)                     /* 64 MB */
#define HLOG_FILE_HEADER_SIZE 64u
#define HLOG_FIRST_RECORD_OFFSET (HLOG_FILE_HEADER_SIZE + HLOG_STRTABLE_BLOCK) /* 4160 */
#define HLOG_STR_MAX_LEN 255u

/* ── Poziomy logowania ───────────────────────────────────────────────────── */
typedef enum
{
    HLOG_LEVEL_TRACE = 0,
    HLOG_LEVEL_DEBUG = 1,
    HLOG_LEVEL_INFO = 2,
    HLOG_LEVEL_WARN = 3,
    HLOG_LEVEL_ERROR = 4,
    HLOG_LEVEL_FATAL = 5
} HlogLevel;

/* ── On-disk struktury (packed) ──────────────────────────────────────────── */
#pragma pack(push, 1)

/*
 * Nagłówek pliku segmentu — 64 bajty.
 * header_crc32 = CRC32 bajtów [0..55].
 */
typedef struct
{
    uint32_t magic;   /* HLOG_FILE_MAGIC */
    uint16_t version; /* HLOG_VERSION */
    uint8_t flags;    /* HLOG_FLAG_* */
    uint8_t _reserved;
    int64_t created_at;       /* us od Unix epoch — kiedy segment powstał */
    uint64_t segment_seq;     /* numer porządkowy (1, 2, ...) */
    uint64_t record_count;    /* liczba rekordów */
    int64_t first_ts;         /* timestamp pierwszego rekordu (0 = brak) */
    int64_t last_ts;          /* timestamp ostatniego rekordu (0 = brak) */
    uint32_t strtable_offset; /* zawsze = HLOG_FILE_HEADER_SIZE = 64 */
    uint32_t strtable_size;   /* bajty używane w bloku StringTable */
    uint32_t header_crc32;    /* CRC32 bajtów [0..55] */
    uint32_t _padding;
} HlogSegmentFileHeader; /* 64 bajty */

/*
 * Nagłówek rekordu logu — 80 bajtów.
 * record_len = sizeof(HlogRecordHeader) + payload_len + sizeof(uint32_t)
 * CRC32 obejmuje bytes nagłówka [0..79] + payload — zapisany zaraz po payloadzie.
 */
typedef struct
{
    uint8_t magic;       /* HLOG_RECORD_MAGIC = 0xA5 */
    uint32_t record_len; /* sizeof(HlogRecordHeader) + payload_len + 4 */
    uint8_t level;       /* HlogLevel */
    uint16_t flags;      /* HLOG_RFLAG_* */
    uint32_t payload_len;
    int64_t timestamp;          /* us — kiedy zdarzenie nastąpiło */
    int64_t ingested_at;        /* us — kiedy trafiło do HobiaryLogDB */
    uint16_t source_id;         /* → StringTable */
    uint16_t service_id;        /* → StringTable */
    uint16_t operation_id;      /* → StringTable */
    uint16_t entity_id;         /* → StringTable */
    uint8_t trace_id[16];       /* binary UUID, zeros = brak */
    uint8_t correlation_id[16]; /* binary UUID, zeros = brak */
    uint64_t span_id;           /* 0 = brak */
    uint32_t _padding;
} HlogRecordHeader; /* 80 bajtów */

/* Wpis indeksu — używany przez hlog_index (etap 2) */
typedef struct
{
    int64_t timestamp; /* us */
    uint32_t offset;   /* offset w pliku .hlog */
} HlogIndexEntry;      /* 12 bajtów */

#pragma pack(pop)

/* ── Input struct dla zapisu (nie packed, dla wygody klientów) ───────────── */
typedef struct
{
    uint8_t level;         /* HlogLevel */
    uint16_t flags;        /* HLOG_RFLAG_* (np. HLOG_RFLAG_PAYLOAD_JSON) */
    int64_t timestamp;     /* us; 0 = użyj ingested_at */
    int64_t ingested_at;   /* us; 0 = ustaw automatycznie */
    const char *source;    /* np. "dcc-central"; NULL = "" */
    const char *service;   /* np. "InfrastrukturaService"; NULL = "" */
    const char *operation; /* np. "ReserveTracks"; NULL = "" */
    const char *entity_id; /* np. "train:abc-123"; NULL = "" */
    uint8_t trace_id[16];  /* zeros = brak */
    uint8_t correlation_id[16];
    uint64_t span_id;       /* 0 = brak */
    const uint8_t *payload; /* NULL = brak */
    uint32_t payload_len;
} HlogRecordInput;

/* ── Opaque handle ───────────────────────────────────────────────────────── */
typedef struct HlogSegment HlogSegment;

/* ── API ─────────────────────────────────────────────────────────────────── */
#ifdef __cplusplus
extern "C"
{
#endif

    /*
     * Tworzy nowy plik segmentu pod podaną ścieżką z podanym numerem seq.
     * Zwraca NULL przy błędzie.
     */
    HLOGDB_API HlogSegment *hlog_segment_create(const char *path, uint64_t seq);

    /*
     * Otwiera istniejący plik segmentu.
     * Weryfikuje magic, version i header_crc32.
     * Zwraca NULL przy błędzie lub uszkodzeniu.
     */
    HLOGDB_API HlogSegment *hlog_segment_open(const char *path);

    /*
     * Dopisuje rekord do segmentu.
     * Jeśli rec->ingested_at == 0, ustawia bieżący czas.
     * Jeśli rec->timestamp == 0, ustawia = ingested_at.
     * Jeśli payload_len > HLOG_MAX_PAYLOAD, przycina i ustawia HLOG_RFLAG_PAYLOAD_TRUNC.
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_segment_append(HlogSegment *seg, const HlogRecordInput *rec);

    /*
     * Odczytuje rekord z podanego offsetu w pliku.
     * Weryfikuje CRC32.
     * out_payload: jeśli != NULL, alokuje bufor i wypełnia — caller zwalnia przez hlog_free().
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_segment_read_at(HlogSegment *seg, uint32_t offset,
                                        HlogRecordHeader *out_header,
                                        uint8_t **out_payload);

    /*
     * Zwalnia pamięć alokowaną przez hlog_segment_read_at (payload).
     */
    HLOGDB_API void hlog_free(void *ptr);

    /*
     * Zamyka segment i zwalnia pamięć.
     * Jeśli seal != 0, ustawia flagę HLOG_FLAG_SEALED i aktualizuje header_crc32.
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_segment_close(HlogSegment *seg, int seal);

    /* Zwraca liczbę rekordów w segmencie. */
    HLOGDB_API uint64_t hlog_segment_record_count(const HlogSegment *seg);

    /* Zwraca timestamp pierwszego rekordu (us). 0 = brak rekordów. */
    HLOGDB_API int64_t hlog_segment_first_ts(const HlogSegment *seg);

    /* Zwraca timestamp ostatniego rekordu (us). 0 = brak rekordów. */
    HLOGDB_API int64_t hlog_segment_last_ts(const HlogSegment *seg);

    /* Zwraca aktualny rozmiar pliku segmentu w bajtach. */
    HLOGDB_API uint32_t hlog_segment_file_size(const HlogSegment *seg);

    /*
     * Zwraca string dla danego ID z StringTable.
     * Wskaźnik jest ważny do momentu zamknięcia segmentu — nie zwalniać.
     * Zwraca "" dla ID=0 lub nieznanych ID.
     */
    HLOGDB_API const char *hlog_segment_str(const HlogSegment *seg, uint16_t id);

    /* Czy segment jest oznaczony jako sealed? */
    HLOGDB_API int hlog_segment_is_sealed(const HlogSegment *seg);

    /* ── Etap 6: crash recovery ──────────────────────────────────────────── */

    /*
     * Skanuje rekordy od początku do pierwszego uszkodzonego lub EOF.
     * Zwraca liczbę poprawnych (CRC OK) rekordów.
     * out_end_offset: offset bajtowy za ostatnim poprawnym rekordem (= HLOG_FIRST_RECORD_OFFSET
     *                 jeśli segment jest pusty).
     * out_last_ts:    timestamp ostatniego poprawnego rekordu (0 = brak rekordów).
     */
    HLOGDB_API int hlog_segment_scan_valid(HlogSegment *seg,
                                           uint32_t *out_end_offset,
                                           int64_t *out_last_ts);

    /*
     * Obcina plik segmentu do valid_end_offset i aktualizuje nagłówek:
     *   record_count = valid_count, last_ts = last_valid_ts, file_size = valid_end_offset.
     * Używane po hlog_segment_scan_valid gdy valid_end_offset < file_size.
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_segment_truncate(HlogSegment *seg,
                                         uint32_t valid_end_offset,
                                         uint64_t valid_count,
                                         int64_t last_valid_ts);

#ifdef __cplusplus
}
#endif

#endif /* HOBIARY_HLOG_SEGMENT_H */
