#include "hlog.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <sys/stat.h>
#include <dirent.h>
#include <errno.h>
#endif

/* ── Wewnętrzne limity ───────────────────────────────────────────────────── */
#define HLOG_MAX_SEGS 256u
#define HLOG_DIR_MAX 900u   /* max dł. ścieżki katalogu */
#define HLOG_PATH_MAX 1024u /* max pełna ścieżka (dir + nazwa pliku) */

/* ── Prywatna struktura fasady ───────────────────────────────────────────── */
struct Hlog
{
    char dir[HLOG_DIR_MAX];
    uint64_t seqs[HLOG_MAX_SEGS]; /* numery sekwencji, posortowane rosnąco */
    uint32_t seg_count;
    uint32_t max_segment_bytes;

    /* Aktywny (do zapisu) segment */
    HlogSegment *active_seg;
    HlogIndex *active_idx_time;
    HlogIndex *active_idx_error;
    HlogIndex *active_idx_level; /* etap 5 — co rekord */
    HlogIndex *active_idx_src;   /* etap 5 — rekordy z source_id != 0 */
    uint64_t active_seq;
    uint64_t active_record_count; /* rekordy od otwarcia/stworzenia */
};

/* ── Konstruktory ścieżek ────────────────────────────────────────────────── */

static void make_seg_path(const Hlog *log, uint64_t seq,
                          char *out, size_t sz)
{
    snprintf(out, sz, "%s/hlog_%06llu.hlog",
             log->dir, (unsigned long long)seq);
}

static void make_idx_time_path(const Hlog *log, uint64_t seq,
                               char *out, size_t sz)
{
    snprintf(out, sz, "%s/hlog_%06llu.hidx_time",
             log->dir, (unsigned long long)seq);
}

static void make_idx_error_path(const Hlog *log, uint64_t seq,
                                char *out, size_t sz)
{
    snprintf(out, sz, "%s/hlog_%06llu.hidx_error",
             log->dir, (unsigned long long)seq);
}

static void make_idx_level_path(const Hlog *log, uint64_t seq,
                                char *out, size_t sz)
{
    snprintf(out, sz, "%s/hlog_%06llu.hidx_level",
             log->dir, (unsigned long long)seq);
}

static void make_idx_src_path(const Hlog *log, uint64_t seq,
                              char *out, size_t sz)
{
    snprintf(out, sz, "%s/hlog_%06llu.hidx_src",
             log->dir, (unsigned long long)seq);
}

/* ── Tworzenie katalogu ──────────────────────────────────────────────────── */

static int mkdir_if_needed(const char *dir)
{
#ifdef _WIN32
    if (!CreateDirectoryA(dir, NULL) &&
        GetLastError() != ERROR_ALREADY_EXISTS)
        return HLOG_ERR_IO;
    return HLOG_OK;
#else
    if (mkdir(dir, 0755) != 0 && errno != EEXIST)
        return HLOG_ERR_IO;
    return HLOG_OK;
#endif
}

/* ── Sortowanie (insertion sort — max HLOG_MAX_SEGS = 256) ──────────────── */

static void sort_seqs(uint64_t *seqs, uint32_t count)
{
    for (uint32_t i = 1; i < count; i++)
    {
        uint64_t key = seqs[i];
        int32_t j = (int32_t)i - 1;
        while (j >= 0 && seqs[j] > key)
        {
            seqs[j + 1] = seqs[j];
            j--;
        }
        seqs[j + 1] = key;
    }
}

/* ── Skan katalogu — wypełnia log->seqs[] ────────────────────────────────── */

static void scan_segments(Hlog *log)
{
#ifdef _WIN32
    char pattern[HLOG_PATH_MAX];
    snprintf(pattern, sizeof(pattern), "%s/hlog_*.hlog", log->dir);

    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE)
        return;
    do
    {
        unsigned long long seq_ull = 0;
        int name_end = 0;
        if (sscanf(fd.cFileName, "hlog_%llu.hlog%n", &seq_ull, &name_end) == 1 &&
            name_end > 0 && fd.cFileName[name_end] == '\0')
        {
            if (log->seg_count < HLOG_MAX_SEGS)
                log->seqs[log->seg_count++] = (uint64_t)seq_ull;
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR *d = opendir(log->dir);
    if (!d)
        return;
    struct dirent *e;
    while ((e = readdir(d)) != NULL)
    {
        unsigned long long seq_ull = 0;
        int name_end = 0;
        if (sscanf(e->d_name, "hlog_%llu.hlog%n", &seq_ull, &name_end) == 1 &&
            name_end > 0 && e->d_name[name_end] == '\0')
        {
            if (log->seg_count < HLOG_MAX_SEGS)
                log->seqs[log->seg_count++] = (uint64_t)seq_ull;
        }
    }
    closedir(d);
#endif
}

/* ── Dodaj seq do tablicy seqs (jeśli jeszcze nie ma) ─────────────────────── */

static void add_seq(Hlog *log, uint64_t seq)
{
    for (uint32_t i = 0; i < log->seg_count; i++)
        if (log->seqs[i] == seq)
            return;
    if (log->seg_count < HLOG_MAX_SEGS)
        log->seqs[log->seg_count++] = seq;
    sort_seqs(log->seqs, log->seg_count);
}

/* ── Utwórz nowy segment + indeksy jako aktywne ──────────────────────────── */

static int open_new_active(Hlog *log, uint64_t new_seq)
{
    char seg_path[HLOG_PATH_MAX];
    char idx_t[HLOG_PATH_MAX];
    char idx_e[HLOG_PATH_MAX];
    char idx_lv[HLOG_PATH_MAX];
    char idx_src[HLOG_PATH_MAX];
    make_seg_path(log, new_seq, seg_path, sizeof(seg_path));
    make_idx_time_path(log, new_seq, idx_t, sizeof(idx_t));
    make_idx_error_path(log, new_seq, idx_e, sizeof(idx_e));
    make_idx_level_path(log, new_seq, idx_lv, sizeof(idx_lv));
    make_idx_src_path(log, new_seq, idx_src, sizeof(idx_src));

    log->active_seg = hlog_segment_create(seg_path, new_seq);
    if (!log->active_seg)
        return HLOG_ERR_IO;

    log->active_idx_time = hlog_index_create(idx_t, HLOG_IDX_TIME_MAGIC, new_seq);
    if (!log->active_idx_time)
    {
        hlog_segment_close(log->active_seg, 0);
        log->active_seg = NULL;
        return HLOG_ERR_IO;
    }

    log->active_idx_error = hlog_index_create(idx_e, HLOG_IDX_ERROR_MAGIC, new_seq);
    if (!log->active_idx_error)
    {
        hlog_segment_close(log->active_seg, 0);
        log->active_seg = NULL;
        hlog_index_close(log->active_idx_time);
        log->active_idx_time = NULL;
        return HLOG_ERR_IO;
    }

    log->active_idx_level = hlog_index_create(idx_lv, HLOG_IDX_LEVEL_MAGIC, new_seq);
    if (!log->active_idx_level)
    {
        hlog_segment_close(log->active_seg, 0);
        log->active_seg = NULL;
        hlog_index_close(log->active_idx_time);
        log->active_idx_time = NULL;
        hlog_index_close(log->active_idx_error);
        log->active_idx_error = NULL;
        return HLOG_ERR_IO;
    }

    log->active_idx_src = hlog_index_create(idx_src, HLOG_IDX_SRC_MAGIC, new_seq);
    if (!log->active_idx_src)
    {
        hlog_segment_close(log->active_seg, 0);
        log->active_seg = NULL;
        hlog_index_close(log->active_idx_time);
        log->active_idx_time = NULL;
        hlog_index_close(log->active_idx_error);
        log->active_idx_error = NULL;
        hlog_index_close(log->active_idx_level);
        log->active_idx_level = NULL;
        return HLOG_ERR_IO;
    }

    log->active_seq = new_seq;
    log->active_record_count = 0;
    add_seq(log, new_seq);
    return HLOG_OK;
}

/* ── Otwórz istniejący segment jako aktywny ──────────────────────────────── */

/* ── Etap 6: odbudowa wszystkich indeksów przez skan segmentu ────────────── */

static int rebuild_indexes_for_seq(Hlog *log, uint64_t seq, HlogSegment *seg)
{
    char idx_t[HLOG_PATH_MAX], idx_e[HLOG_PATH_MAX];
    char idx_lv[HLOG_PATH_MAX], idx_src_p[HLOG_PATH_MAX];
    make_idx_time_path(log, seq, idx_t, sizeof(idx_t));
    make_idx_error_path(log, seq, idx_e, sizeof(idx_e));
    make_idx_level_path(log, seq, idx_lv, sizeof(idx_lv));
    make_idx_src_path(log, seq, idx_src_p, sizeof(idx_src_p));

    /* Usuń stare pliki indeksów */
    remove(idx_t);
    remove(idx_e);
    remove(idx_lv);
    remove(idx_src_p);

    /* Utwórz nowe */
    HlogIndex *it = hlog_index_create(idx_t, HLOG_IDX_TIME_MAGIC, seq);
    HlogIndex *ie = hlog_index_create(idx_e, HLOG_IDX_ERROR_MAGIC, seq);
    HlogIndex *ilv = hlog_index_create(idx_lv, HLOG_IDX_LEVEL_MAGIC, seq);
    HlogIndex *isr = hlog_index_create(idx_src_p, HLOG_IDX_SRC_MAGIC, seq);

    if (!it || !ie || !ilv || !isr)
    {
        if (it)
            hlog_index_close(it);
        if (ie)
            hlog_index_close(ie);
        if (ilv)
            hlog_index_close(ilv);
        if (isr)
            hlog_index_close(isr);
        return HLOG_ERR_IO;
    }

    /* Skan segmentu, zapis do indeksów */
    uint32_t cur = HLOG_FIRST_RECORD_OFFSET;
    uint64_t rec_num = 0;

    for (;;)
    {
        HlogRecordHeader hdr;
        if (hlog_segment_read_at(seg, cur, &hdr, NULL) != HLOG_OK)
            break;
        if (hdr.record_len < (uint32_t)(sizeof(HlogRecordHeader) + sizeof(uint32_t)))
            break;

        int64_t ts = hdr.timestamp;

        if (rec_num % HLOG_IDX_STRIDE == 0)
            hlog_index_append(it, ts, cur);

        if (hdr.level >= (uint8_t)HLOG_LEVEL_ERROR)
            hlog_index_append(ie, ts, cur);

        hlog_level_index_append(ilv, ts, cur, hdr.level);

        if (hdr.source_id != 0)
            hlog_src_index_append(isr, ts, cur, hdr.source_id);

        rec_num++;
        cur += hdr.record_len;
    }

    hlog_index_close(it);
    hlog_index_close(ie);
    hlog_index_close(ilv);
    hlog_index_close(isr);
    return HLOG_OK;
}

static int open_existing_active(Hlog *log, uint64_t seq)
{
    char seg_path[HLOG_PATH_MAX];
    char idx_t[HLOG_PATH_MAX];
    char idx_e[HLOG_PATH_MAX];
    char idx_lv[HLOG_PATH_MAX];
    char idx_src[HLOG_PATH_MAX];
    make_seg_path(log, seq, seg_path, sizeof(seg_path));
    make_idx_time_path(log, seq, idx_t, sizeof(idx_t));
    make_idx_error_path(log, seq, idx_e, sizeof(idx_e));
    make_idx_level_path(log, seq, idx_lv, sizeof(idx_lv));
    make_idx_src_path(log, seq, idx_src, sizeof(idx_src));

    log->active_seg = hlog_segment_open(seg_path);
    if (!log->active_seg)
        return HLOG_ERR_IO;

    /* ── Etap 6: wykryj i napraw obcięty segment ─────────────────────────── */
    {
        uint32_t valid_end = 0;
        int64_t last_ts = 0;
        int valid_count = hlog_segment_scan_valid(log->active_seg, &valid_end, &last_ts);

        if (valid_count >= 0)
        {
            uint32_t file_sz = hlog_segment_file_size(log->active_seg);
            uint64_t stored_cnt = hlog_segment_record_count(log->active_seg);

            /* Napraw jeśli:
             *   (a) ogon pliku jest uszkodzony (valid_end < file_sz), ALBO
             *   (b) plik był zewnętrznie obcięty — file_sz = valid_end,
             *       ale nagłówek nadal przechowuje wyższą liczbę rekordów */
            if (valid_end < file_sz || (uint64_t)valid_count != stored_cnt)
            {
                hlog_segment_truncate(log->active_seg, valid_end,
                                      (uint64_t)valid_count, last_ts);
            }
        }
    }

    /* ── Otwórz indeksy — jeśli którykolwiek jest brakujący/uszkodzony,
           odbuduj wszystkie cztery przez ponowny skan segmentu ─────────────── */
    int needs_rebuild = 0;

    log->active_idx_time = hlog_index_open(idx_t, HLOG_IDX_TIME_MAGIC);
    if (!log->active_idx_time)
        needs_rebuild = 1;

    log->active_idx_error = hlog_index_open(idx_e, HLOG_IDX_ERROR_MAGIC);
    if (!log->active_idx_error)
        needs_rebuild = 1;

    log->active_idx_level = hlog_index_open(idx_lv, HLOG_IDX_LEVEL_MAGIC);
    if (!log->active_idx_level)
        needs_rebuild = 1;

    log->active_idx_src = hlog_index_open(idx_src, HLOG_IDX_SRC_MAGIC);
    if (!log->active_idx_src)
        needs_rebuild = 1;

    if (needs_rebuild)
    {
        /* Zamknij wszystkie już otwarte */
        if (log->active_idx_time)
        {
            hlog_index_close(log->active_idx_time);
            log->active_idx_time = NULL;
        }
        if (log->active_idx_error)
        {
            hlog_index_close(log->active_idx_error);
            log->active_idx_error = NULL;
        }
        if (log->active_idx_level)
        {
            hlog_index_close(log->active_idx_level);
            log->active_idx_level = NULL;
        }
        if (log->active_idx_src)
        {
            hlog_index_close(log->active_idx_src);
            log->active_idx_src = NULL;
        }

        /* Odbuduj wszystkie 4 przez skan segmentu */
        int rc = rebuild_indexes_for_seq(log, seq, log->active_seg);
        if (rc != HLOG_OK)
        {
            hlog_segment_close(log->active_seg, 0);
            log->active_seg = NULL;
            return rc;
        }

        /* Otwórz świeżo odbudowane */
        log->active_idx_time = hlog_index_open(idx_t, HLOG_IDX_TIME_MAGIC);
        log->active_idx_error = hlog_index_open(idx_e, HLOG_IDX_ERROR_MAGIC);
        log->active_idx_level = hlog_index_open(idx_lv, HLOG_IDX_LEVEL_MAGIC);
        log->active_idx_src = hlog_index_open(idx_src, HLOG_IDX_SRC_MAGIC);

        /* Failsafe: utwórz puste jeśli dalej NULL */
        if (!log->active_idx_time)
            log->active_idx_time = hlog_index_create(idx_t, HLOG_IDX_TIME_MAGIC, seq);
        if (!log->active_idx_error)
            log->active_idx_error = hlog_index_create(idx_e, HLOG_IDX_ERROR_MAGIC, seq);
        if (!log->active_idx_level)
            log->active_idx_level = hlog_index_create(idx_lv, HLOG_IDX_LEVEL_MAGIC, seq);
        if (!log->active_idx_src)
            log->active_idx_src = hlog_index_create(idx_src, HLOG_IDX_SRC_MAGIC, seq);
    }

    log->active_seq = seq;
    log->active_record_count = hlog_segment_record_count(log->active_seg);
    return HLOG_OK;
}

/* ── Rotacja: seal aktywny, utwórz kolejny ───────────────────────────────── */

static int rotate_active(Hlog *log)
{
    hlog_segment_close(log->active_seg, 1 /* seal */);
    log->active_seg = NULL;

    hlog_index_close(log->active_idx_time);
    log->active_idx_time = NULL;

    hlog_index_close(log->active_idx_error);
    log->active_idx_error = NULL;

    hlog_index_close(log->active_idx_level);
    log->active_idx_level = NULL;

    hlog_index_close(log->active_idx_src);
    log->active_idx_src = NULL;

    uint64_t new_seq = log->active_seq + 1;
    log->active_seq = 0;
    return open_new_active(log, new_seq);
}

/* ════════════════════════════════════════════════════════════════════════════
 * Publiczne API
 * ════════════════════════════════════════════════════════════════════════════ */

Hlog *hlog_open(const char *dir, uint32_t max_segment_bytes)
{
    if (!dir || !dir[0])
        return NULL;

    size_t dlen = strlen(dir);
    if (dlen >= HLOG_DIR_MAX)
        return NULL;

    Hlog *log = (Hlog *)calloc(1, sizeof(Hlog));
    if (!log)
        return NULL;

    /* Skopiuj ścieżkę, usuń trailing slash */
    memcpy(log->dir, dir, dlen);
    while (dlen > 0 && (log->dir[dlen - 1] == '/' || log->dir[dlen - 1] == '\\'))
        log->dir[--dlen] = '\0';

    log->max_segment_bytes = (max_segment_bytes > 0)
                                 ? max_segment_bytes
                                 : HLOG_MAX_SEGMENT_SIZE;

    if (mkdir_if_needed(log->dir) != HLOG_OK)
    {
        free(log);
        return NULL;
    }

    /* Skan istniejących segmentów */
    scan_segments(log);
    sort_seqs(log->seqs, log->seg_count);

    /* Szukaj aktywnego (nie-sealed) od najwyższego seq */
    uint64_t active_seq = 0;
    for (int32_t i = (int32_t)log->seg_count - 1; i >= 0; i--)
    {
        char seg_path[HLOG_PATH_MAX];
        make_seg_path(log, log->seqs[i], seg_path, sizeof(seg_path));
        HlogSegment *tmp = hlog_segment_open(seg_path);
        if (!tmp)
            continue;
        int sealed = hlog_segment_is_sealed(tmp);
        hlog_segment_close(tmp, 0);
        if (!sealed)
        {
            active_seq = log->seqs[i];
            break;
        }
    }

    int rc;
    if (active_seq > 0)
    {
        rc = open_existing_active(log, active_seq);
    }
    else
    {
        uint64_t new_seq = (log->seg_count > 0)
                               ? log->seqs[log->seg_count - 1] + 1
                               : 1;
        rc = open_new_active(log, new_seq);
    }

    if (rc != HLOG_OK)
    {
        free(log);
        return NULL;
    }
    return log;
}

int hlog_close(Hlog *log)
{
    if (!log)
        return HLOG_ERR_ARGS;

    if (log->active_seg)
    {
        hlog_segment_close(log->active_seg, 0);
        log->active_seg = NULL;
    }
    if (log->active_idx_time)
    {
        hlog_index_close(log->active_idx_time);
        log->active_idx_time = NULL;
    }
    if (log->active_idx_error)
    {
        hlog_index_close(log->active_idx_error);
        log->active_idx_error = NULL;
    }
    if (log->active_idx_level)
    {
        hlog_index_close(log->active_idx_level);
        log->active_idx_level = NULL;
    }
    if (log->active_idx_src)
    {
        hlog_index_close(log->active_idx_src);
        log->active_idx_src = NULL;
    }
    free(log);
    return HLOG_OK;
}

int hlog_append(Hlog *log, const HlogRecordInput *rec)
{
    if (!log || !rec)
        return HLOG_ERR_ARGS;
    if (!log->active_seg)
        return HLOG_ERR_IO;

    /* Rotacja jeśli segment pełny */
    if (hlog_segment_file_size(log->active_seg) >= log->max_segment_bytes)
    {
        int rc = rotate_active(log);
        if (rc != HLOG_OK)
            return rc;
    }

    /* Offset rekordu = bieżący rozmiar pliku (przed zapisem) */
    uint32_t record_offset = hlog_segment_file_size(log->active_seg);

    int rc = hlog_segment_append(log->active_seg, rec);
    if (rc != HLOG_OK)
        return rc;

    /* idx_pos = 0-bazowy numer tego rekordu w aktywnym segmencie */
    uint64_t idx_pos = log->active_record_count;
    log->active_record_count++;

    int64_t ts = hlog_segment_last_ts(log->active_seg);

    /* Indeks czasu — co HLOG_IDX_STRIDE rekordów (0, 64, 128, ...) */
    if (idx_pos % HLOG_IDX_STRIDE == 0)
        hlog_index_append(log->active_idx_time, ts, record_offset);

    /* Indeks błędów — każdy ERROR lub FATAL */
    if (rec->level >= (uint8_t)HLOG_LEVEL_ERROR)
        hlog_index_append(log->active_idx_error, ts, record_offset);

    /* Indeks level — każdy rekord */
    hlog_level_index_append(log->active_idx_level, ts, record_offset, rec->level);

    /* Indeks source — rekordy z niepustym source_id (czytamy nagłówek cofając się) */
    {
        HlogRecordHeader hdr_back;
        if (hlog_segment_read_at(log->active_seg, record_offset, &hdr_back, NULL) == HLOG_OK && hdr_back.source_id != 0)
        {
            hlog_src_index_append(log->active_idx_src, ts, record_offset,
                                  hdr_back.source_id);
        }
    }

    return HLOG_OK;
}

uint32_t hlog_segment_count(const Hlog *log)
{
    return log ? log->seg_count : 0u;
}

uint64_t hlog_active_seq(const Hlog *log)
{
    return log ? log->active_seq : 0u;
}

uint64_t hlog_active_record_count(const Hlog *log)
{
    return log ? log->active_record_count : 0u;
}

int hlog_visit_range(Hlog *log, int64_t from_ts, int64_t to_ts,
                     HlogVisitFn fn, void *ctx)
{
    if (!log || !fn)
        return HLOG_ERR_ARGS;
    if (from_ts > to_ts)
        return HLOG_ERR_ARGS;

    int total = 0;

    for (uint32_t i = 0; i < log->seg_count; i++)
    {
        uint64_t seq = log->seqs[i];
        int is_active = (seq == log->active_seq);

        char seg_path[HLOG_PATH_MAX];
        char idx_t_path[HLOG_PATH_MAX];
        make_seg_path(log, seq, seg_path, sizeof(seg_path));
        make_idx_time_path(log, seq, idx_t_path, sizeof(idx_t_path));

        HlogSegment *seg;
        if (is_active)
        {
            seg = log->active_seg;
        }
        else
        {
            seg = hlog_segment_open(seg_path);
            if (!seg)
                continue;
        }

        /* Szybkie sprawdzenie zakresu czasu segmentu */
        int64_t seg_first = hlog_segment_first_ts(seg);
        int64_t seg_last = hlog_segment_last_ts(seg);

        if (seg_last > 0 && seg_last < from_ts)
        {
            if (!is_active)
                hlog_segment_close(seg, 0);
            continue;
        }
        if (seg_first > 0 && seg_first > to_ts)
        {
            if (!is_active)
                hlog_segment_close(seg, 0);
            break; /* segmenty posortowane rosnąco → dalsze też poza zakresem */
        }

        /* Szukaj przybliżonego offsetu startowego w indeksie czasu */
        uint32_t start_offset = HLOG_FIRST_RECORD_OFFSET;

        HlogIndex *idx_t;
        if (is_active)
        {
            idx_t = log->active_idx_time; /* już otwarty — nie zamykaj */
        }
        else
        {
            idx_t = hlog_index_open(idx_t_path, HLOG_IDX_TIME_MAGIC);
        }

        if (idx_t)
        {
            uint32_t found_off;
            int64_t found_ts;
            if (hlog_index_find_ge(idx_t, from_ts, &found_off, &found_ts) == HLOG_OK)
                start_offset = found_off;
            if (!is_active)
                hlog_index_close(idx_t);
        }

        /* Liniowy skan od start_offset */
        int stop = 0;
        uint32_t cur = start_offset;

        for (;;)
        {
            HlogRecordHeader hdr;
            uint8_t *payload = NULL;
            int rc = hlog_segment_read_at(seg, cur, &hdr, &payload);
            if (rc != HLOG_OK)
                break;

            if (hdr.timestamp > to_ts)
            {
                hlog_free(payload);
                break;
            }

            if (hdr.timestamp >= from_ts)
            {
                stop = fn(seg, &hdr, payload, ctx);
                total++;
            }

            hlog_free(payload);

            if (stop)
            {
                if (!is_active)
                    hlog_segment_close(seg, 0);
                goto done;
            }

            cur += hdr.record_len;
        }

        if (!is_active)
            hlog_segment_close(seg, 0);
    }

done:
    return total;
}

/* ── hlog_purge_before ───────────────────────────────────────────────────── */

int hlog_purge_before(Hlog *log, int64_t cutoff_ts)
{
    if (!log)
        return HLOG_ERR_ARGS;

    int removed = 0;

    /* Iteruj od najstarszego; nigdy nie usuwaj aktywnego segmentu */
    uint32_t i = 0;
    while (i < log->seg_count)
    {
        uint64_t seq = log->seqs[i];
        if (seq == log->active_seq)
        {
            i++;
            continue;
        }

        char seg_path[HLOG_PATH_MAX];
        make_seg_path(log, seq, seg_path, sizeof(seg_path));

        /* Otwórz aby sprawdzić last_ts */
        HlogSegment *seg = hlog_segment_open(seg_path);
        if (!seg)
        {
            i++;
            continue;
        }

        int64_t last_ts = hlog_segment_last_ts(seg);
        int sealed = hlog_segment_is_sealed(seg);
        hlog_segment_close(seg, 0);

        /* Usuń tylko sealed segmenty z last_ts < cutoff_ts */
        if (!sealed || last_ts >= cutoff_ts || last_ts == 0)
        {
            i++;
            continue;
        }

        /* Usuń pliki segmentu i indeksów */
        remove(seg_path);

        char idx_t[HLOG_PATH_MAX], idx_e[HLOG_PATH_MAX];
        char idx_lv[HLOG_PATH_MAX], idx_src[HLOG_PATH_MAX];
        make_idx_time_path(log, seq, idx_t, sizeof(idx_t));
        make_idx_error_path(log, seq, idx_e, sizeof(idx_e));
        make_idx_level_path(log, seq, idx_lv, sizeof(idx_lv));
        make_idx_src_path(log, seq, idx_src, sizeof(idx_src));
        remove(idx_t);
        remove(idx_e);
        remove(idx_lv);
        remove(idx_src);

        /* Usuń seq z tablicy seqs — przesuń resztę w lewo */
        for (uint32_t j = i; j + 1 < log->seg_count; j++)
            log->seqs[j] = log->seqs[j + 1];
        log->seg_count--;
        removed++;
        /* Nie inkrementuj i — na tej samej pozycji jest teraz kolejny element */
    }

    return removed;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Etap 5 — hlog_visit_level / hlog_visit_source
 * ═══════════════════════════════════════════════════════════════════════════ */

/* Kontekst pomocniczy dla visit_level */
typedef struct
{
    HlogSegment *seg;
    HlogVisitFn fn;
    void *ctx;
    int total;
    int stop;
} LevelVisitCtx;

static int level_entry_cb(int64_t ts, uint32_t offset, uint8_t level, void *ctx)
{
    (void)ts;
    (void)level;
    LevelVisitCtx *lvc = (LevelVisitCtx *)ctx;
    HlogRecordHeader hdr;
    uint8_t *payload = NULL;
    if (hlog_segment_read_at(lvc->seg, offset, &hdr, &payload) != HLOG_OK)
        return 0; /* pomiń uszkodzony rekord */
    lvc->total++;
    int stop = lvc->fn(lvc->seg, &hdr, payload, lvc->ctx);
    hlog_free(payload);
    if (stop)
    {
        lvc->stop = 1;
        return 1;
    }
    return 0;
}

int hlog_visit_level(Hlog *log, uint8_t level, HlogVisitFn fn, void *ctx)
{
    if (!log || !fn)
        return HLOG_ERR_ARGS;

    int total = 0;

    for (uint32_t i = 0; i < log->seg_count; i++)
    {
        uint64_t seq = log->seqs[i];
        int is_active = (seq == log->active_seq);

        char seg_path[HLOG_PATH_MAX], idx_lv_path[HLOG_PATH_MAX];
        make_seg_path(log, seq, seg_path, sizeof(seg_path));
        make_idx_level_path(log, seq, idx_lv_path, sizeof(idx_lv_path));

        HlogSegment *seg = is_active ? log->active_seg
                                     : hlog_segment_open(seg_path);
        if (!seg)
            continue;

        HlogIndex *idx_lv = is_active ? log->active_idx_level
                                      : hlog_index_open(idx_lv_path,
                                                        HLOG_IDX_LEVEL_MAGIC);
        if (!idx_lv)
        {
            if (!is_active)
                hlog_segment_close(seg, 0);
            continue;
        }

        LevelVisitCtx lvc = {seg, fn, ctx, 0, 0};
        hlog_level_index_scan(idx_lv, level, level_entry_cb, &lvc);
        total += lvc.total;

        if (!is_active)
        {
            hlog_index_close(idx_lv);
            hlog_segment_close(seg, 0);
        }

        if (lvc.stop)
            break;
    }

    return total;
}

/* Kontekst pomocniczy dla visit_source */
typedef struct
{
    HlogSegment *seg;
    HlogVisitFn fn;
    void *ctx;
    int total;
    int stop;
} SrcVisitCtx;

static int src_entry_cb(int64_t ts, uint32_t offset, uint16_t source_id, void *ctx)
{
    (void)ts;
    (void)source_id;
    SrcVisitCtx *svc = (SrcVisitCtx *)ctx;
    HlogRecordHeader hdr;
    uint8_t *payload = NULL;
    if (hlog_segment_read_at(svc->seg, offset, &hdr, &payload) != HLOG_OK)
        return 0;
    svc->total++;
    int stop = svc->fn(svc->seg, &hdr, payload, svc->ctx);
    hlog_free(payload);
    if (stop)
    {
        svc->stop = 1;
        return 1;
    }
    return 0;
}

int hlog_visit_source(Hlog *log, uint16_t source_id, HlogVisitFn fn, void *ctx)
{
    if (!log || !fn)
        return HLOG_ERR_ARGS;

    int total = 0;

    for (uint32_t i = 0; i < log->seg_count; i++)
    {
        uint64_t seq = log->seqs[i];
        int is_active = (seq == log->active_seq);

        char seg_path[HLOG_PATH_MAX], idx_src_path[HLOG_PATH_MAX];
        make_seg_path(log, seq, seg_path, sizeof(seg_path));
        make_idx_src_path(log, seq, idx_src_path, sizeof(idx_src_path));

        HlogSegment *seg = is_active ? log->active_seg
                                     : hlog_segment_open(seg_path);
        if (!seg)
            continue;

        HlogIndex *idx_src = is_active ? log->active_idx_src
                                       : hlog_index_open(idx_src_path,
                                                         HLOG_IDX_SRC_MAGIC);
        if (!idx_src)
        {
            if (!is_active)
                hlog_segment_close(seg, 0);
            continue;
        }

        SrcVisitCtx svc = {seg, fn, ctx, 0, 0};
        hlog_src_index_scan(idx_src, source_id, src_entry_cb, &svc);
        total += svc.total;

        if (!is_active)
        {
            hlog_index_close(idx_src);
            hlog_segment_close(seg, 0);
        }

        if (svc.stop)
            break;
    }

    return total;
}
