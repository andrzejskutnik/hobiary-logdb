#ifndef HOBIARY_HLOG_H
#define HOBIARY_HLOG_H

/*
 * hlog — etap 3
 * ════════════════════════════════════════════════════════════════════════════
 *
 * Fasada zarządzająca katalogiem logów:
 *   - automatyczna rotacja segmentów (sealed gdy osiągną max rozmiar)
 *   - bieżące budowanie indeksów hidx_time i hidx_error
 *   - iteracja rekordów po czasie (visit_range)
 *
 * Układ katalogu:
 *
 *   logs/
 *     hlog_000001.hlog
 *     hlog_000001.hidx_time
 *     hlog_000001.hidx_error
 *     hlog_000002.hlog        ← aktywny (nie-sealed)
 *     hlog_000002.hidx_time
 *     hlog_000002.hidx_error
 *
 * Aktywny segment = segment o najwyższym numerze bez flagi HLOG_FLAG_SEALED.
 * Brak dodatkowych metaplików.
 *
 * ════════════════════════════════════════════════════════════════════════════
 */

#include "hlog_segment.h"
#include "hlog_index.h"

/* ── Visitor callback ────────────────────────────────────────────────────── */

/*
 * Wywoływany przez hlog_visit_range dla każdego pasującego rekordu.
 *
 * seg:     segment zawierający rekord — użyj hlog_segment_str(seg, id)
 *          aby zamienić source_id/service_id/... na string.
 * hdr:     nagłówek rekordu (packed on-disk layout).
 * payload: dane payloadu lub NULL jeśli payload_len == 0.
 *          Bufor jest ważny tylko przez czas trwania wywołania — nie zatrzymuj wskaźnika.
 * ctx:     wartość ctx przekazana do hlog_visit_range.
 *
 * Zwróć 0 aby kontynuować, niezerową wartość aby przerwać iterację.
 */
typedef int (*HlogVisitFn)(HlogSegment *seg,
                           const HlogRecordHeader *hdr,
                           const uint8_t *payload,
                           void *ctx);

/* ── Opaque handle ───────────────────────────────────────────────────────── */
typedef struct Hlog Hlog;

/* ── API ─────────────────────────────────────────────────────────────────── */
#ifdef __cplusplus
extern "C"
{
#endif

    /*
     * Otwiera lub tworzy katalog logów.
     *
     * dir:                ścieżka do katalogu (tworzona jeśli nie istnieje).
     * max_segment_bytes:  maksymalny rozmiar pliku .hlog przed rotacją.
     *                     0 = użyj domyślnego (HLOG_MAX_SEGMENT_SIZE = 64 MB).
     *
     * Zwraca nowy handle lub NULL przy błędzie.
     */
    HLOGDB_API Hlog *hlog_open(const char *dir, uint32_t max_segment_bytes);

    /*
     * Zamyka log — flushuje dane, zwalnia zasoby.
     * Aktywny segment NIE jest sealowany (seal = false).
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_close(Hlog *log);

    /*
     * Dopisuje rekord do aktywnego segmentu.
     * Jeśli segment osiągnął max_segment_bytes:
     *   - zamknij z seal=1
     *   - utwórz nowy segment o numerze active_seq+1
     * Aktualizuje hidx_time (co HLOG_IDX_STRIDE rekordów) i hidx_error
     * (każdy rekord >= HLOG_LEVEL_ERROR).
     * Zwraca HLOG_OK lub kod błędu.
     */
    HLOGDB_API int hlog_append(Hlog *log, const HlogRecordInput *rec);

    /* Łączna liczba segmentów (sealed + aktywny). */
    HLOGDB_API uint32_t hlog_segment_count(const Hlog *log);

    /* Numer sekwencji aktywnego segmentu (1-based). 0 = brak. */
    HLOGDB_API uint64_t hlog_active_seq(const Hlog *log);

    /* Liczba rekordów zapisanych do aktywnego segmentu od jego otwarcia/stworzenia. */
    HLOGDB_API uint64_t hlog_active_record_count(const Hlog *log);

    /*
     * Iteruje rekordy z timestamp w przedziale [from_ts, to_ts] (włącznie).
     * Zakłada niemalejące timestampy w obrębie każdego segmentu.
     * Używa hidx_time do szybkiego odnalezienia startowego offsetu;
     * następnie skanuje liniowo.
     *
     * Zwraca łączną liczbę odwiedzonych rekordów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_visit_range(Hlog *log, int64_t from_ts, int64_t to_ts,
                                    HlogVisitFn fn, void *ctx);

    /*
     * Usuwa sealed segmenty, których last_ts < cutoff_ts.
     * Aktywny segment nigdy nie jest usuwany.
     * Pliki .hlog, .hidx_time i .hidx_error są kasowane z dysku.
     *
     * Zwraca liczbę usuniętych segmentów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_purge_before(Hlog *log, int64_t cutoff_ts);

    /* ── Etap 5: iteracja po poziomie i źródle ───────────────────────────── */

    /*
     * Iteruje wszystkie rekordy o podanym poziomie logowania (we wszystkich
     * segmentach). Używa hidx_level do szybkiego znalezienia offsetów.
     * Callback fn działa tak samo jak w hlog_visit_range.
     * Zwraca łączną liczbę odwiedzonych rekordów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_visit_level(Hlog *log, uint8_t level,
                                    HlogVisitFn fn, void *ctx);

    /*
     * Iteruje wszystkie rekordy o podanym source_id (we wszystkich segmentach).
     * source_id jest indeksem w StringTable segmentu (1-based dla niepustych stringów).
     * Używa hidx_src do szybkiego znalezienia offsetów.
     * Zwraca łączną liczbę odwiedzonych rekordów lub ujemny kod błędu.
     */
    HLOGDB_API int hlog_visit_source(Hlog *log, uint16_t source_id,
                                     HlogVisitFn fn, void *ctx);

#ifdef __cplusplus
}
#endif

#endif /* HOBIARY_HLOG_H */
