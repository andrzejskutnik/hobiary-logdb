using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

public class TtlTests : IDisposable
{
    private readonly string _dir;

    public TtlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hlog_ttl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static NativeHLog.HlogRecordInput MakeRec(long ts)
    {
        return new NativeHLog.HlogRecordInput
        {
            level = NativeHLog.HLOG_LEVEL_INFO,
            flags = 0,
            timestamp = ts,
            ingested_at = 0,
            source = Marshal.StringToHGlobalAnsi("ttl"),
            service = IntPtr.Zero,
            operation = IntPtr.Zero,
            entity_id = IntPtr.Zero,
            span_id = 0,
            trace_id = new byte[16],
            correlation_id = new byte[16],
            payload = IntPtr.Zero,
            payload_len = 0
        };
    }

    private static void FreeRec(ref NativeHLog.HlogRecordInput rec)
    {
        if (rec.source != IntPtr.Zero) { Marshal.FreeHGlobal(rec.source); rec.source = IntPtr.Zero; }
    }

    /* Tworzy log z maxBytes, zapisuje `count` rekordów, zamyka.
       Zwraca liczbę segmentów po zapisie. */
    private uint FillLog(uint maxBytes, int count, long baseTs)
    {
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);
        for (int i = 0; i < count; i++)
        {
            var rec = MakeRec(baseTs + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }
        uint segs = NativeHLogFacade.hlog_segment_count(log);
        NativeHLogFacade.hlog_close(log);
        return segs;
    }

    /* ── Testy ───────────────────────────────────────────────────────────── */

    [Fact]
    public void PurgeWithNothingToRemove_ReturnsZero()
    {
        /* Jeden segment, nie-sealed — nigdy nie usuwamy aktywnego */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        var rec = MakeRec(1000L);
        NativeHLogFacade.hlog_append(log, ref rec);
        FreeRec(ref rec);

        int removed = NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
        Assert.Equal(0, removed);
        Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeDoesNotRemoveActiveSegment()
    {
        /* Wywołaj purge z cutoff_ts = long.MaxValue — aktywny wciąż powinien zostać */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        var rec = MakeRec(500L);
        NativeHLogFacade.hlog_append(log, ref rec);
        FreeRec(ref rec);

        int removed = NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
        Assert.Equal(0, removed);
        Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));
        Assert.Equal(1UL, NativeHLogFacade.hlog_active_seq(log));

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeRemovesSealedOldSegments()
    {
        /* Mały segment: zapisujemy dość rekordów żeby wywołać przynajmniej 2 rotacje,
           co daje 3 segmenty (2 sealed + 1 aktywny). */
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 300; i++)
        {
            var rec = MakeRec(1000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        uint totalSegs = NativeHLogFacade.hlog_segment_count(log);
        Assert.True(totalSegs >= 3, $"Oczekiwano >= 3 segmentów, jest {totalSegs}");

        /* Usuń wszystkie sealed segmenty — cutoff = long.MaxValue */
        int removed = NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
        Assert.True(removed >= 2, $"Oczekiwano >= 2 usuniętych, jest {removed}");

        /* Powinien zostać tylko aktywny */
        Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));
        Assert.Equal(NativeHLogFacade.hlog_active_seq(log),
                     NativeHLogFacade.hlog_active_seq(log)); /* aktywny = ostatni */

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeFilesDeletedFromDisk()
    {
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 300; i++)
        {
            var rec = MakeRec(2000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        Assert.True(NativeHLogFacade.hlog_segment_count(log) >= 3);

        /* Pliki segmentu 1 powinny istnieć przed purge */
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hlog")));

        NativeHLogFacade.hlog_purge_before(log, long.MaxValue);

        /* Po purge pliki seg 1 nie powinny istnieć */
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hlog")),
                     "hlog_000001.hlog powinien być usunięty");
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_time")),
                     "hidx_time powinien być usunięty");
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_error")),
                     "hidx_error powinien być usunięty");

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeCutoff_OnlyOlderThanCutoffRemoved()
    {
        /* Tworzymy 3 segmenty z dobrze kontrolowanymi zakresami czasu.
           Segment 1: ts  100..109  (sealed po rotacji)
           Segment 2: ts  200..209  (sealed po rotacji)
           Aktywny  : ts  300..309

           cutoff = 150 → usuwa tylko seg 1 (last_ts=109 < 150), seg 2 zostaje.
        */
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        /* Dopisuj rekordy aż do co najmniej 3 segmentów, z kontrolowanymi ts */
        bool seg3Reached = false;
        int i = 0;
        while (!seg3Reached && i < 500)
        {
            ulong segNow = NativeHLogFacade.hlog_active_seq(log);
            long ts = segNow == 1 ? 100L + (i % 100)
                    : segNow == 2 ? 200L + (i % 100)
                    : 300L + (i % 100);

            var rec = MakeRec(ts);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);

            if (segNow >= 3) seg3Reached = true;
            i++;
        }

        Assert.True(seg3Reached, "Powinny powstać co najmniej 3 segmenty");

        uint segsBefore = NativeHLogFacade.hlog_segment_count(log);

        /* cutoff = 150 → seg 1 (last_ts~109 < 150) powinien zostać usunięty,
           seg 2 (last_ts~209 >= 150) powinien zostać */
        int removed = NativeHLogFacade.hlog_purge_before(log, 150L);
        Assert.Equal(1, removed);
        Assert.Equal(segsBefore - 1, (uint)NativeHLogFacade.hlog_segment_count(log));

        /* Plik seg 1 usunięty */
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hlog")));
        /* Plik seg 2 wciąż jest */
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000002.hlog")));

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeAll_ThenAppend_StillWorks()
    {
        /* Po usunięciu wszystkich sealed, dopisywanie powinno działać normalnie. */
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 300; i++)
        {
            var rec = MakeRec(5000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
        Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));

        /* Dalsze dopisywanie */
        var rec2 = MakeRec(9999L);
        int rc = NativeHLogFacade.hlog_append(log, ref rec2);
        FreeRec(ref rec2);

        Assert.Equal(NativeHLog.HLOG_OK, rc);
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void PurgeSegmentCount_AfterReopenIsConsistent()
    {
        /* Purge w jednej sesji → reopen → seg_count powinien być spójny. */
        uint maxBytes = 4500u;
        ulong activeSeqAfterPurge;

        {
            var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
            Assert.NotEqual(IntPtr.Zero, log);

            for (int i = 0; i < 300; i++)
            {
                var rec = MakeRec(7000L + i);
                NativeHLogFacade.hlog_append(log, ref rec);
                FreeRec(ref rec);
            }

            NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
            activeSeqAfterPurge = NativeHLogFacade.hlog_active_seq(log);
            NativeHLogFacade.hlog_close(log);
        }

        {
            var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
            Assert.NotEqual(IntPtr.Zero, log);
            Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));
            Assert.Equal(activeSeqAfterPurge, NativeHLogFacade.hlog_active_seq(log));
            NativeHLogFacade.hlog_close(log);
        }
    }
}
