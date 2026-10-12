#include "hlog_index.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

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

/* ── Struktura indeksu (prywatna) ────────────────────────────────────────── */

struct HlogIndex
{
    FILE *file;
    HlogIndexFileHeader header;
};

/* ── Zapis nagłówka ──────────────────────────────────────────────────────── */

static int index_header_write(HlogIndex *idx)
{
    idx->header.header_crc32 = crc32_compute((const uint8_t *)&idx->header, 24u);
    if (fseek(idx->file, 0L, SEEK_SET) != 0)
        return HLOG_ERR_IO;
    if (fwrite(&idx->header, 1, sizeof(HlogIndexFileHeader), idx->file) != sizeof(HlogIndexFileHeader))
        return HLOG_ERR_IO;
    return HLOG_OK;
}

/* ── Odczyt wpisu z podanej pozycji (0-based) ────────────────────────────── */

static int index_read_entry_at(HlogIndex *idx, uint64_t pos,
                               HlogIndexEntry *out)
{
    long offset = (long)(HLOG_IDX_HEADER_SIZE + pos * sizeof(HlogIndexEntry));
    if (fseek(idx->file, offset, SEEK_SET) != 0)
        return HLOG_ERR_IO;
    if (fread(out, 1, sizeof(HlogIndexEntry), idx->file) != sizeof(HlogIndexEntry))
        return HLOG_ERR_IO;
    return HLOG_OK;
}

/* ── hlog_index_create ───────────────────────────────────────────────────── */

HlogIndex *hlog_index_create(const char *path, uint32_t magic, uint64_t seg_seq)
{
    if (!path)
        return NULL;
    if (magic != HLOG_IDX_TIME_MAGIC && magic != HLOG_IDX_ERROR_MAGIC &&
        magic != HLOG_IDX_LEVEL_MAGIC && magic != HLOG_IDX_SRC_MAGIC)
        return NULL;

    HlogIndex *idx = (HlogIndex *)calloc(1, sizeof(HlogIndex));
    if (!idx)
        return NULL;

    idx->file = fopen(path, "w+b");
    if (!idx->file)
    {
        free(idx);
        return NULL;
    }

    idx->header.magic = magic;
    idx->header.version = HLOG_IDX_VERSION;
    idx->header.segment_seq = seg_seq;
    idx->header.entry_count = 0;
    idx->header._padding = 0;
    memset(idx->header._reserved, 0, sizeof(idx->header._reserved));

    if (index_header_write(idx) != HLOG_OK)
    {
        fclose(idx->file);
        free(idx);
        return NULL;
    }
    fflush(idx->file);
    return idx;
}

/* ── hlog_index_open ─────────────────────────────────────────────────────── */

HlogIndex *hlog_index_open(const char *path, uint32_t magic)
{
    if (!path)
        return NULL;

    HlogIndex *idx = (HlogIndex *)calloc(1, sizeof(HlogIndex));
    if (!idx)
        return NULL;

    idx->file = fopen(path, "r+b");
    if (!idx->file)
    {
        free(idx);
        return NULL;
    }

    if (fread(&idx->header, 1, sizeof(HlogIndexFileHeader), idx->file) != sizeof(HlogIndexFileHeader))
    {
        fclose(idx->file);
        free(idx);
        return NULL;
    }

    if (idx->header.version != HLOG_IDX_VERSION ||
        idx->header.magic != magic)
    {
        fclose(idx->file);
        free(idx);
        return NULL;
    }

    uint32_t expected = crc32_compute((const uint8_t *)&idx->header, 24u);
    if (expected != idx->header.header_crc32)
    {
        fclose(idx->file);
        free(idx);
        return NULL;
    }

    return idx;
}

/* ── hlog_index_append ───────────────────────────────────────────────────── */

int hlog_index_append(HlogIndex *idx, int64_t timestamp, uint32_t offset)
{
    if (!idx)
        return HLOG_ERR_ARGS;

    HlogIndexEntry entry;
    entry.timestamp = timestamp;
    entry.offset = offset;

    if (fseek(idx->file, 0L, SEEK_END) != 0)
        return HLOG_ERR_IO;
    if (fwrite(&entry, 1, sizeof(HlogIndexEntry), idx->file) != sizeof(HlogIndexEntry))
        return HLOG_ERR_IO;

    idx->header.entry_count++;
    int rc = index_header_write(idx);
    if (rc == HLOG_OK)
        fflush(idx->file);
    return rc;
}

/* ── hlog_index_close ────────────────────────────────────────────────────── */

int hlog_index_close(HlogIndex *idx)
{
    if (!idx)
        return HLOG_ERR_ARGS;
    fclose(idx->file);
    free(idx);
    return HLOG_OK;
}

/* ── hlog_index_entry_count ──────────────────────────────────────────────── */

uint64_t hlog_index_entry_count(const HlogIndex *idx)
{
    return idx ? idx->header.entry_count : 0u;
}

/* ── hlog_index_read_at ──────────────────────────────────────────────────── */

int hlog_index_read_at(HlogIndex *idx, uint64_t pos, HlogIndexEntry *out_entry)
{
    if (!idx || !out_entry)
        return HLOG_ERR_ARGS;
    if (pos >= idx->header.entry_count)
        return HLOG_ERR_NOT_FOUND;
    return index_read_entry_at(idx, pos, out_entry);
}

/* ── hlog_index_find_ge — binary search ─────────────────────────────────── */

int hlog_index_find_ge(HlogIndex *idx, int64_t ts,
                       uint32_t *out_offset, int64_t *out_ts)
{
    if (!idx || !out_offset)
        return HLOG_ERR_ARGS;

    uint64_t count = idx->header.entry_count;
    if (count == 0)
        return HLOG_ERR_NOT_FOUND;

    int64_t lo = 0;
    int64_t hi = (int64_t)count - 1;
    int64_t result = -1;

    while (lo <= hi)
    {
        int64_t mid = lo + (hi - lo) / 2;
        HlogIndexEntry entry;
        if (index_read_entry_at(idx, (uint64_t)mid, &entry) != HLOG_OK)
            return HLOG_ERR_IO;

        if (entry.timestamp >= ts)
        {
            result = mid;
            hi = mid - 1;
        }
        else
        {
            lo = mid + 1;
        }
    }

    if (result < 0)
        return HLOG_ERR_NOT_FOUND;

    HlogIndexEntry found;
    if (index_read_entry_at(idx, (uint64_t)result, &found) != HLOG_OK)
        return HLOG_ERR_IO;

    *out_offset = found.offset;
    if (out_ts)
        *out_ts = found.timestamp;
    return HLOG_OK;
}

/* ── hlog_index_find_range ───────────────────────────────────────────────── */

int hlog_index_find_range(HlogIndex *idx,
                          int64_t from_ts, int64_t to_ts,
                          HlogIndexEntry *out_entries,
                          int max_entries)
{
    if (!idx || !out_entries || max_entries <= 0)
        return HLOG_ERR_ARGS;
    if (from_ts > to_ts)
        return HLOG_ERR_ARGS;

    uint64_t count = idx->header.entry_count;
    if (count == 0)
        return 0;

    /* Znajdź pierwszą pozycję z timestamp >= from_ts */
    int64_t lo = 0;
    int64_t hi = (int64_t)count - 1;
    int64_t start = (int64_t)count; /* domyślnie: nie znaleziono */

    while (lo <= hi)
    {
        int64_t mid = lo + (hi - lo) / 2;
        HlogIndexEntry entry;
        if (index_read_entry_at(idx, (uint64_t)mid, &entry) != HLOG_OK)
            return HLOG_ERR_IO;
        if (entry.timestamp >= from_ts)
        {
            start = mid;
            hi = mid - 1;
        }
        else
        {
            lo = mid + 1;
        }
    }

    if (start >= (int64_t)count)
        return 0;

    /* Zbieraj wpisy z timestamp <= to_ts */
    int found = 0;
    for (int64_t i = start; i < (int64_t)count && found < max_entries; i++)
    {
        HlogIndexEntry entry;
        if (index_read_entry_at(idx, (uint64_t)i, &entry) != HLOG_OK)
            return HLOG_ERR_IO;
        if (entry.timestamp > to_ts)
            break;
        out_entries[found++] = entry;
    }

    return found;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Etap 5 — Indeksy level i source
 * ═══════════════════════════════════════════════════════════════════════════ */

int hlog_level_index_append(HlogIndex *idx, int64_t ts,
                            uint32_t offset, uint8_t level)
{
    if (!idx)
        return HLOG_ERR_ARGS;
    if (fseek(idx->file, 0L, SEEK_END) != 0)
        return HLOG_ERR_IO;

    HlogLevelIndexEntry e;
    e.timestamp = ts;
    e.offset = offset;
    e.level = level;
    e._pad[0] = e._pad[1] = e._pad[2] = 0;

    if (fwrite(&e, sizeof(HlogLevelIndexEntry), 1, idx->file) != 1)
        return HLOG_ERR_IO;

    idx->header.entry_count++;
    int rc = index_header_write(idx);
    if (rc == HLOG_OK)
        fflush(idx->file);
    return rc;
}

int hlog_src_index_append(HlogIndex *idx, int64_t ts,
                          uint32_t offset, uint16_t source_id)
{
    if (!idx)
        return HLOG_ERR_ARGS;
    if (fseek(idx->file, 0L, SEEK_END) != 0)
        return HLOG_ERR_IO;

    HlogSrcIndexEntry e;
    e.timestamp = ts;
    e.offset = offset;
    e.source_id = source_id;
    e._pad[0] = e._pad[1] = 0;

    if (fwrite(&e, sizeof(HlogSrcIndexEntry), 1, idx->file) != 1)
        return HLOG_ERR_IO;

    idx->header.entry_count++;
    int rc = index_header_write(idx);
    if (rc == HLOG_OK)
        fflush(idx->file);
    return rc;
}

int hlog_level_index_scan(HlogIndex *idx, uint8_t level,
                          HlogLevelVisitFn fn, void *ctx)
{
    if (!idx || !fn)
        return HLOG_ERR_ARGS;

    uint64_t count = idx->header.entry_count;
    int matches = 0;

    for (uint64_t i = 0; i < count; i++)
    {
        long pos = (long)(HLOG_IDX_HEADER_SIZE + i * HLOG_IDX_LEVEL_ENTRY_SIZE);
        if (fseek(idx->file, pos, SEEK_SET) != 0)
            return HLOG_ERR_IO;

        HlogLevelIndexEntry e;
        if (fread(&e, sizeof(HlogLevelIndexEntry), 1, idx->file) != 1)
            break;

        if (e.level != level)
            continue;

        matches++;
        if (fn(e.timestamp, e.offset, e.level, ctx) != 0)
            break;
    }

    return matches;
}

int hlog_src_index_scan(HlogIndex *idx, uint16_t source_id,
                        HlogSrcVisitFn fn, void *ctx)
{
    if (!idx || !fn)
        return HLOG_ERR_ARGS;

    uint64_t count = idx->header.entry_count;
    int matches = 0;

    for (uint64_t i = 0; i < count; i++)
    {
        long pos = (long)(HLOG_IDX_HEADER_SIZE + i * HLOG_IDX_SRC_ENTRY_SIZE);
        if (fseek(idx->file, pos, SEEK_SET) != 0)
            return HLOG_ERR_IO;

        HlogSrcIndexEntry e;
        if (fread(&e, sizeof(HlogSrcIndexEntry), 1, idx->file) != 1)
            break;

        if (e.source_id != source_id)
            continue;

        matches++;
        if (fn(e.timestamp, e.offset, e.source_id, ctx) != 0)
            break;
    }

    return matches;
}
