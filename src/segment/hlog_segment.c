#include "hlog_segment.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#ifdef _WIN32
#include <windows.h>
#include <io.h> /* _chsize_s, _fileno */
#define strncasecmp _strnicmp
#else
#include <unistd.h> /* ftruncate, fileno */
#endif

/* ── CRC32 ───────────────────────────────────────────────────────────────── */

static uint32_t s_crc32_table[256];
static int s_crc32_ready = 0;

static void crc32_init(void)
{
    for (uint32_t i = 0; i < 256u; i++)
    {
        uint32_t c = i;
        for (int j = 0; j < 8; j++)
            c = (c & 1u) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
        s_crc32_table[i] = c;
    }
    s_crc32_ready = 1;
}

static uint32_t crc32_compute(const uint8_t *data, size_t len)
{
    if (!s_crc32_ready)
        crc32_init();
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < len; i++)
        crc = s_crc32_table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}

/* ── Bieżący czas w mikrosekundach ───────────────────────────────────────── */

static int64_t hlog_now_us(void)
{
#ifdef _WIN32
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER ui;
    ui.LowPart = ft.dwLowDateTime;
    ui.HighPart = ft.dwHighDateTime;
    /* FILETIME: 100ns od 1601-01-01 → konwersja do us od 1970-01-01 */
    return (int64_t)((ui.QuadPart - 116444736000000000ULL) / 10u);
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000000LL + (int64_t)(ts.tv_nsec / 1000);
#endif
}

/* ── Struktura segmentu (prywatna) ───────────────────────────────────────── */

struct HlogSegment
{
    FILE *file;
    char path[512];
    HlogSegmentFileHeader header; /* kopia nagłówka w pamięci */

    /* StringTable w pamięci */
    uint16_t str_count;                           /* liczba stringów (łącznie z 0) */
    uint16_t str_offsets[HLOG_STRTABLE_CAPACITY]; /* offset w str_data dla każdego */
    char str_data[HLOG_STR_DATA_SIZE];            /* raw dane stringów */
    uint16_t str_data_used;                       /* bajty użyte w str_data */

    uint32_t file_size; /* aktualny rozmiar pliku */
};

/* ── StringTable: serializacja / deserializacja ──────────────────────────── */

/*
 * Format bloku StringTable (HLOG_STRTABLE_BLOCK bajtów):
 *   [uint16_t count]
 *   [uint16_t offsets[HLOG_STRTABLE_CAPACITY]]   (512 bajtów)
 *   [char data[HLOG_STR_DATA_SIZE]]               (3582 bajtów)
 */

static void strtable_serialize(const HlogSegment *seg, uint8_t out[HLOG_STRTABLE_BLOCK])
{
    memset(out, 0, HLOG_STRTABLE_BLOCK);
    uint8_t *p = out;
    /* count */
    uint16_t cnt = seg->str_count;
    memcpy(p, &cnt, 2);
    p += 2;
    /* offsets */
    memcpy(p, seg->str_offsets, HLOG_STRTABLE_CAPACITY * 2u);
    p += HLOG_STRTABLE_CAPACITY * 2u;
    /* data */
    memcpy(p, seg->str_data, seg->str_data_used);
}

static int strtable_deserialize(HlogSegment *seg, const uint8_t in[HLOG_STRTABLE_BLOCK])
{
    const uint8_t *p = in;
    uint16_t cnt;
    memcpy(&cnt, p, 2);
    p += 2;
    if (cnt > HLOG_STRTABLE_CAPACITY)
        return HLOG_ERR_CORRUPT;
    seg->str_count = cnt;
    memcpy(seg->str_offsets, p, HLOG_STRTABLE_CAPACITY * 2u);
    p += HLOG_STRTABLE_CAPACITY * 2u;
    /* Oblicz ile bajtów danych: max offset + długość ostatniego stringa */
    uint16_t data_used = 0;
    for (uint16_t i = 0; i < cnt; i++)
    {
        uint16_t off = seg->str_offsets[i];
        if (off >= HLOG_STR_DATA_SIZE)
            return HLOG_ERR_CORRUPT;
        const char *s = (const char *)p + off;
        size_t slen = strnlen(s, HLOG_STR_DATA_SIZE - off);
        if (off + slen + 1u > HLOG_STR_DATA_SIZE)
            return HLOG_ERR_CORRUPT;
        uint16_t end = (uint16_t)(off + slen + 1u);
        if (end > data_used)
            data_used = end;
    }
    seg->str_data_used = data_used;
    memcpy(seg->str_data, p, data_used);
    return HLOG_OK;
}

static int strtable_write(HlogSegment *seg)
{
    uint8_t block[HLOG_STRTABLE_BLOCK];
    strtable_serialize(seg, block);
    if (fseek(seg->file, (long)HLOG_FILE_HEADER_SIZE, SEEK_SET) != 0)
        return HLOG_ERR_IO;
    if (fwrite(block, 1, HLOG_STRTABLE_BLOCK, seg->file) != HLOG_STRTABLE_BLOCK)
        return HLOG_ERR_IO;
    seg->header.strtable_size = seg->str_data_used + HLOG_STR_HEADER_SIZE;
    return HLOG_OK;
}

/*
 * Internuje string — zwraca istniejący ID lub tworzy nowy wpis.
 * NULL i "" → ID 0.
 */
static int strtable_intern(HlogSegment *seg, const char *str, uint16_t *out_id)
{
    if (!str || str[0] == '\0')
    {
        *out_id = 0;
        return HLOG_OK;
    }

    size_t slen = strlen(str);
    if (slen > HLOG_STR_MAX_LEN)
        slen = HLOG_STR_MAX_LEN;

    /* Szukaj istniejącego */
    for (uint16_t i = 0; i < seg->str_count; i++)
    {
        const char *existing = seg->str_data + seg->str_offsets[i];
        if (strncmp(existing, str, slen) == 0 && existing[slen] == '\0')
        {
            *out_id = i;
            return HLOG_OK;
        }
    }

    /* Dodaj nowy */
    if (seg->str_count >= HLOG_STRTABLE_CAPACITY)
        return HLOG_ERR_FULL;
    uint16_t needed = (uint16_t)(slen + 1u);
    if ((uint32_t)seg->str_data_used + needed > HLOG_STR_DATA_SIZE)
        return HLOG_ERR_FULL;

    uint16_t new_id = seg->str_count;
    uint16_t new_off = seg->str_data_used;
    memcpy(seg->str_data + new_off, str, slen);
    seg->str_data[new_off + slen] = '\0';
    seg->str_offsets[new_id] = new_off;
    seg->str_data_used += needed;
    seg->str_count++;

    int rc = strtable_write(seg);
    if (rc != HLOG_OK)
        return rc;

    *out_id = new_id;
    return HLOG_OK;
}

/* ── Zapis nagłówka pliku ────────────────────────────────────────────────── */

static int file_header_write(HlogSegment *seg)
{
    /* Oblicz CRC32 pierwszych 56 bajtów (przed polem header_crc32) */
    seg->header.header_crc32 = crc32_compute((const uint8_t *)&seg->header, 56u);
    if (fseek(seg->file, 0L, SEEK_SET) != 0)
        return HLOG_ERR_IO;
    if (fwrite(&seg->header, 1, sizeof(HlogSegmentFileHeader), seg->file) != sizeof(HlogSegmentFileHeader))
        return HLOG_ERR_IO;
    return HLOG_OK;
}

/* ── Inicjalizacja StringTable z ID=0 = "" ───────────────────────────────── */

static void strtable_init(HlogSegment *seg)
{
    seg->str_count = 1;     /* ID 0 = "" */
    seg->str_data_used = 1; /* null byte */
    memset(seg->str_offsets, 0, sizeof(seg->str_offsets));
    memset(seg->str_data, 0, sizeof(seg->str_data));
    seg->str_offsets[0] = 0;
    seg->str_data[0] = '\0';
}

/* ── hlog_segment_create ─────────────────────────────────────────────────── */

HlogSegment *hlog_segment_create(const char *path, uint64_t seq)
{
    if (!path)
        return NULL;

    HlogSegment *seg = (HlogSegment *)calloc(1, sizeof(HlogSegment));
    if (!seg)
        return NULL;

    strncpy(seg->path, path, sizeof(seg->path) - 1);

    seg->file = fopen(path, "w+b");
    if (!seg->file)
    {
        free(seg);
        return NULL;
    }

    /* Nagłówek */
    seg->header.magic = HLOG_FILE_MAGIC;
    seg->header.version = HLOG_VERSION;
    seg->header.flags = 0;
    seg->header._reserved = 0;
    seg->header.created_at = hlog_now_us();
    seg->header.segment_seq = seq;
    seg->header.record_count = 0;
    seg->header.first_ts = 0;
    seg->header.last_ts = 0;
    seg->header.strtable_offset = HLOG_FILE_HEADER_SIZE;
    seg->header.strtable_size = 0;
    seg->header._padding = 0;

    if (file_header_write(seg) != HLOG_OK)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* StringTable */
    strtable_init(seg);
    if (strtable_write(seg) != HLOG_OK)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* Wymuś rozmiar pliku = HLOG_FIRST_RECORD_OFFSET */
    if (fseek(seg->file, (long)(HLOG_FIRST_RECORD_OFFSET - 1u), SEEK_SET) != 0 ||
        fputc(0, seg->file) == EOF)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    fflush(seg->file);
    seg->file_size = HLOG_FIRST_RECORD_OFFSET;
    return seg;
}

/* ── hlog_segment_open ───────────────────────────────────────────────────── */

HlogSegment *hlog_segment_open(const char *path)
{
    if (!path)
        return NULL;

    HlogSegment *seg = (HlogSegment *)calloc(1, sizeof(HlogSegment));
    if (!seg)
        return NULL;

    strncpy(seg->path, path, sizeof(seg->path) - 1);

    seg->file = fopen(path, "r+b");
    if (!seg->file)
    {
        free(seg);
        return NULL;
    }

    /* Odczyt nagłówka */
    if (fread(&seg->header, 1, sizeof(HlogSegmentFileHeader), seg->file) != sizeof(HlogSegmentFileHeader))
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* Weryfikacja magic i version */
    if (seg->header.magic != HLOG_FILE_MAGIC ||
        seg->header.version != HLOG_VERSION)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* Weryfikacja CRC32 nagłówka */
    uint32_t expected = crc32_compute((const uint8_t *)&seg->header, 56u);
    if (expected != seg->header.header_crc32)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* Odczyt StringTable */
    uint8_t block[HLOG_STRTABLE_BLOCK];
    if (fseek(seg->file, (long)HLOG_FILE_HEADER_SIZE, SEEK_SET) != 0 ||
        fread(block, 1, HLOG_STRTABLE_BLOCK, seg->file) != HLOG_STRTABLE_BLOCK)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }
    if (strtable_deserialize(seg, block) != HLOG_OK)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }

    /* Ustal rozmiar pliku */
    if (fseek(seg->file, 0L, SEEK_END) != 0)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }
    long sz = ftell(seg->file);
    if (sz < 0)
    {
        fclose(seg->file);
        free(seg);
        return NULL;
    }
    seg->file_size = (uint32_t)sz;

    return seg;
}

/* ── hlog_segment_append ─────────────────────────────────────────────────── */

int hlog_segment_append(HlogSegment *seg, const HlogRecordInput *rec)
{
    if (!seg || !rec)
        return HLOG_ERR_ARGS;
    if (seg->header.flags & HLOG_FLAG_SEALED)
        return HLOG_ERR_SEALED;
    if (seg->file_size >= HLOG_MAX_SEGMENT_SIZE)
        return HLOG_ERR_FULL;

    /* Czas */
    int64_t now = hlog_now_us();
    int64_t ingested = rec->ingested_at ? rec->ingested_at : now;
    int64_t ts = rec->timestamp ? rec->timestamp : ingested;

    /* Intern stringów */
    uint16_t source_id = 0, service_id = 0, operation_id = 0, entity_id = 0;
    int rc;
    if ((rc = strtable_intern(seg, rec->source, &source_id)) != HLOG_OK)
        return rc;
    if ((rc = strtable_intern(seg, rec->service, &service_id)) != HLOG_OK)
        return rc;
    if ((rc = strtable_intern(seg, rec->operation, &operation_id)) != HLOG_OK)
        return rc;
    if ((rc = strtable_intern(seg, rec->entity_id, &entity_id)) != HLOG_OK)
        return rc;

    /* Oblicz payload */
    uint32_t plen = rec->payload_len;
    uint16_t flags = rec->flags;
    if (plen > HLOG_MAX_PAYLOAD)
    {
        plen = HLOG_MAX_PAYLOAD;
        flags = (uint16_t)(flags | HLOG_RFLAG_PAYLOAD_TRUNC);
    }

    /* Buduj nagłówek rekordu */
    HlogRecordHeader hdr;
    memset(&hdr, 0, sizeof(hdr));
    hdr.magic = HLOG_RECORD_MAGIC;
    hdr.record_len = (uint32_t)(sizeof(HlogRecordHeader) + plen + sizeof(uint32_t));
    hdr.level = rec->level;
    hdr.flags = flags;
    hdr.payload_len = plen;
    hdr.timestamp = ts;
    hdr.ingested_at = ingested;
    hdr.source_id = source_id;
    hdr.service_id = service_id;
    hdr.operation_id = operation_id;
    hdr.entity_id = entity_id;
    memcpy(hdr.trace_id, rec->trace_id, 16);
    memcpy(hdr.correlation_id, rec->correlation_id, 16);
    hdr.span_id = rec->span_id;

    /* Seek na koniec */
    if (fseek(seg->file, (long)seg->file_size, SEEK_SET) != 0)
        return HLOG_ERR_IO;

    uint32_t record_offset = seg->file_size;

    /* Zapis nagłówka rekordu */
    if (fwrite(&hdr, 1, sizeof(HlogRecordHeader), seg->file) != sizeof(HlogRecordHeader))
        return HLOG_ERR_IO;

    /* Zapis payloadu */
    if (plen > 0 && rec->payload)
    {
        if (fwrite(rec->payload, 1, plen, seg->file) != plen)
            return HLOG_ERR_IO;
    }

    /* Oblicz i zapisz CRC32 (nagłówek + payload) */
    uint32_t crc = crc32_compute((const uint8_t *)&hdr, sizeof(HlogRecordHeader));
    if (plen > 0 && rec->payload)
    {
        /* CRC inkrementalny: kontynuuj od wyniku poprzedniego */
        /* Uproszczenie: re-compute over header+payload razem */
        /* Alokuj tymczasowy bufor do CRC */
        uint8_t *tmp = (uint8_t *)malloc(sizeof(HlogRecordHeader) + plen);
        if (!tmp)
            return HLOG_ERR_IO;
        memcpy(tmp, &hdr, sizeof(HlogRecordHeader));
        memcpy(tmp + sizeof(HlogRecordHeader), rec->payload, plen);
        crc = crc32_compute(tmp, sizeof(HlogRecordHeader) + plen);
        free(tmp);
    }
    if (fwrite(&crc, 1, sizeof(uint32_t), seg->file) != sizeof(uint32_t))
        return HLOG_ERR_IO;

    fflush(seg->file);

    /* Aktualizuj stats w pamięci */
    seg->file_size += hdr.record_len;
    seg->header.record_count++;
    if (seg->header.first_ts == 0)
        seg->header.first_ts = ts;
    seg->header.last_ts = ts;

    /* Zaktualizuj nagłówek pliku */
    (void)record_offset; /* używane przez indeks w etapie 2 */
    return file_header_write(seg);
}

/* ── hlog_segment_read_at ────────────────────────────────────────────────── */

int hlog_segment_read_at(HlogSegment *seg, uint32_t offset,
                         HlogRecordHeader *out_header,
                         uint8_t **out_payload)
{
    if (!seg || !out_header)
        return HLOG_ERR_ARGS;
    if (offset < HLOG_FIRST_RECORD_OFFSET)
        return HLOG_ERR_ARGS;

    if (fseek(seg->file, (long)offset, SEEK_SET) != 0)
        return HLOG_ERR_IO;

    HlogRecordHeader hdr;
    if (fread(&hdr, 1, sizeof(HlogRecordHeader), seg->file) != sizeof(HlogRecordHeader))
        return HLOG_ERR_IO;

    if (hdr.magic != HLOG_RECORD_MAGIC)
        return HLOG_ERR_CORRUPT;

    uint32_t plen = hdr.payload_len;
    uint8_t *payload = NULL;

    if (plen > 0)
    {
        payload = (uint8_t *)malloc(plen);
        if (!payload)
            return HLOG_ERR_IO;
        if (fread(payload, 1, plen, seg->file) != plen)
        {
            free(payload);
            return HLOG_ERR_IO;
        }
    }

    /* Weryfikacja CRC */
    uint32_t stored_crc;
    if (fread(&stored_crc, 1, sizeof(uint32_t), seg->file) != sizeof(uint32_t))
    {
        free(payload);
        return HLOG_ERR_IO;
    }

    uint32_t computed_crc;
    if (plen > 0)
    {
        uint8_t *tmp = (uint8_t *)malloc(sizeof(HlogRecordHeader) + plen);
        if (!tmp)
        {
            free(payload);
            return HLOG_ERR_IO;
        }
        memcpy(tmp, &hdr, sizeof(HlogRecordHeader));
        memcpy(tmp + sizeof(HlogRecordHeader), payload, plen);
        computed_crc = crc32_compute(tmp, sizeof(HlogRecordHeader) + plen);
        free(tmp);
    }
    else
    {
        computed_crc = crc32_compute((const uint8_t *)&hdr, sizeof(HlogRecordHeader));
    }

    if (computed_crc != stored_crc)
    {
        free(payload);
        return HLOG_ERR_CORRUPT;
    }

    *out_header = hdr;
    if (out_payload)
        *out_payload = payload;
    else
        free(payload);

    return HLOG_OK;
}

/* ── hlog_free ───────────────────────────────────────────────────────────── */

void hlog_free(void *ptr)
{
    free(ptr);
}

/* ── hlog_segment_close ──────────────────────────────────────────────────── */

int hlog_segment_close(HlogSegment *seg, int seal)
{
    if (!seg)
        return HLOG_ERR_ARGS;
    int rc = HLOG_OK;
    if (seal)
    {
        seg->header.flags = (uint8_t)(seg->header.flags | HLOG_FLAG_SEALED);
        rc = file_header_write(seg);
        if (rc == HLOG_OK)
            fflush(seg->file);
    }
    fclose(seg->file);
    free(seg);
    return rc;
}

/* ── Gettery ─────────────────────────────────────────────────────────────── */

uint64_t hlog_segment_record_count(const HlogSegment *seg)
{
    return seg ? seg->header.record_count : 0u;
}

int64_t hlog_segment_first_ts(const HlogSegment *seg)
{
    return seg ? seg->header.first_ts : 0;
}

int64_t hlog_segment_last_ts(const HlogSegment *seg)
{
    return seg ? seg->header.last_ts : 0;
}

uint32_t hlog_segment_file_size(const HlogSegment *seg)
{
    return seg ? seg->file_size : 0u;
}

const char *hlog_segment_str(const HlogSegment *seg, uint16_t id)
{
    if (!seg || id >= seg->str_count)
        return "";
    return seg->str_data + seg->str_offsets[id];
}

int hlog_segment_is_sealed(const HlogSegment *seg)
{
    return seg ? ((seg->header.flags & HLOG_FLAG_SEALED) != 0) : 0;
}

/* ── hlog_segment_scan_valid (etap 6) ────────────────────────────────────── */

int hlog_segment_scan_valid(HlogSegment *seg,
                            uint32_t *out_end_offset,
                            int64_t *out_last_ts)
{
    if (!seg)
        return HLOG_ERR_ARGS;

    /* Minimum valid record_len = sizeof(header) + sizeof(crc32) = 80 + 4 = 84 */
    const uint32_t MIN_RECORD_LEN =
        (uint32_t)(sizeof(HlogRecordHeader) + sizeof(uint32_t));

    uint32_t cur = HLOG_FIRST_RECORD_OFFSET;
    int count = 0;
    int64_t last_ts = 0;

    for (;;)
    {
        HlogRecordHeader hdr;
        int rc = hlog_segment_read_at(seg, cur, &hdr, NULL);
        if (rc != HLOG_OK)
            break; /* EOF lub uszkodzony rekord */

        /* Sanity check: record_len musi być co najmniej MIN_RECORD_LEN */
        if (hdr.record_len < MIN_RECORD_LEN)
            break;

        count++;
        last_ts = hdr.timestamp;
        cur += hdr.record_len;
    }

    if (out_end_offset)
        *out_end_offset = cur;
    if (out_last_ts)
        *out_last_ts = last_ts;
    return count;
}

/* ── hlog_segment_truncate (etap 6) ──────────────────────────────────────── */

int hlog_segment_truncate(HlogSegment *seg,
                          uint32_t valid_end_offset,
                          uint64_t valid_count,
                          int64_t last_valid_ts)
{
    if (!seg)
        return HLOG_ERR_ARGS;
    if (valid_end_offset < HLOG_FIRST_RECORD_OFFSET)
        return HLOG_ERR_ARGS;

#ifdef _WIN32
    if (_chsize_s(_fileno(seg->file), (long long)valid_end_offset) != 0)
        return HLOG_ERR_IO;
#else
    if (ftruncate(fileno(seg->file), (off_t)valid_end_offset) != 0)
        return HLOG_ERR_IO;
#endif

    /* Zaktualizuj stan w pamięci */
    seg->header.record_count = valid_count;
    seg->header.last_ts = last_valid_ts;
    seg->file_size = valid_end_offset;

    int rc = file_header_write(seg);
    if (rc == HLOG_OK)
        fflush(seg->file);
    return rc;
}
