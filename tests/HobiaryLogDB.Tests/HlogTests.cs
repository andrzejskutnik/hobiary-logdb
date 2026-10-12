using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

public class HlogTests : IDisposable
{
    private readonly string _dir;

    public HlogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hlog_facade_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private static NativeHLog.HlogRecordInput MakeRec(byte level, long ts, string? msg = null)
    {
        var rec = new NativeHLog.HlogRecordInput
        {
            level = level,
            flags = 0,
            timestamp = ts,
            ingested_at = 0,
            source = Marshal.StringToHGlobalAnsi("test"),
            service = IntPtr.Zero,
            operation = IntPtr.Zero,
            entity_id = IntPtr.Zero,
            span_id = 0,
            trace_id = new byte[16],
            correlation_id = new byte[16],
            payload = IntPtr.Zero,
            payload_len = 0
        };
        if (msg != null)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(msg + "\0");
            rec.payload = Marshal.AllocHGlobal(bytes.Length);
            rec.payload_len = (uint)(bytes.Length - 1);
            Marshal.Copy(bytes, 0, rec.payload, bytes.Length);
        }
        return rec;
    }

    private static void FreeRec(ref NativeHLog.HlogRecordInput rec)
    {
        if (rec.source != IntPtr.Zero) { Marshal.FreeHGlobal(rec.source); rec.source = IntPtr.Zero; }
        if (rec.payload != IntPtr.Zero) { Marshal.FreeHGlobal(rec.payload); rec.payload = IntPtr.Zero; }
    }

    /* ── Testy podstawowe ────────────────────────────────────────────────── */

    [Fact]
    public void CanOpenNewLogDir()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));
        Assert.Equal(1UL, NativeHLogFacade.hlog_active_seq(log));
        Assert.Equal(0UL, NativeHLogFacade.hlog_active_record_count(log));
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void CanAppendRecord()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 1000L);
        int rc = NativeHLogFacade.hlog_append(log, ref rec);
        FreeRec(ref rec);

        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(1UL, NativeHLogFacade.hlog_active_record_count(log));
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void CanReopenLog_RecordCountPreserved()
    {
        /* Pierwsza sesja: 5 rekordów */
        {
            var log = NativeHLogFacade.hlog_open(_dir, 0);
            Assert.NotEqual(IntPtr.Zero, log);
            for (int i = 0; i < 5; i++)
            {
                var rec = MakeRec(NativeHLog.HLOG_LEVEL_DEBUG, 1000L + i);
                NativeHLogFacade.hlog_append(log, ref rec);
                FreeRec(ref rec);
            }
            NativeHLogFacade.hlog_close(log);
        }

        /* Druga sesja: reopen */
        {
            var log = NativeHLogFacade.hlog_open(_dir, 0);
            Assert.NotEqual(IntPtr.Zero, log);
            Assert.Equal(1u, NativeHLogFacade.hlog_segment_count(log));
            Assert.Equal(1UL, NativeHLogFacade.hlog_active_seq(log));
            Assert.Equal(5UL, NativeHLogFacade.hlog_active_record_count(log));
            NativeHLogFacade.hlog_close(log);
        }
    }

    /* ── Rotacja segmentów ───────────────────────────────────────────────── */

    [Fact]
    public void AutoRotation_CreatesNewSegment()
    {
        /* max_segment_bytes = 5000 — przy typowym rekordzie (~84B bez payload)
           rotacja nastąpi po ok. 59 rekordach */
        uint maxBytes = 5000u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);
        Assert.Equal(1UL, NativeHLogFacade.hlog_active_seq(log));

        /* Dopisuj aż do rotacji */
        bool rotated = false;
        for (int i = 0; i < 200; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 1000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);

            if (NativeHLogFacade.hlog_active_seq(log) > 1)
            {
                rotated = true;
                break;
            }
        }

        Assert.True(rotated, "Rotacja powinna nastąpić przed 200 rekordami");
        Assert.Equal(2u, NativeHLogFacade.hlog_segment_count(log));
        Assert.Equal(2UL, NativeHLogFacade.hlog_active_seq(log));
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void AfterRotation_SealedSegmentExistsOnDisk()
    {
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 200; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_WARN, 2000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
            if (NativeHLogFacade.hlog_segment_count(log) >= 2) break;
        }
        NativeHLogFacade.hlog_close(log);

        /* Plik sealed powinien istnieć na dysku */
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hlog")));
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000002.hlog")));

        /* Segment 1 powinien być sealed */
        var seg1 = NativeHLog.hlog_segment_open(Path.Combine(_dir, "hlog_000001.hlog"));
        Assert.NotEqual(IntPtr.Zero, seg1);
        Assert.Equal(1, NativeHLog.hlog_segment_is_sealed(seg1));
        NativeHLog.hlog_segment_close(seg1, 0);
    }

    [Fact]
    public void ReopenAfterRotation_FindsActiveSegment()
    {
        uint maxBytes = 4500u;

        /* Sesja 1: dość rekordów żeby wywołać rotację */
        {
            var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
            Assert.NotEqual(IntPtr.Zero, log);
            for (int i = 0; i < 200; i++)
            {
                var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 3000L + i);
                NativeHLogFacade.hlog_append(log, ref rec);
                FreeRec(ref rec);
                if (NativeHLogFacade.hlog_segment_count(log) >= 2) break;
            }
            NativeHLogFacade.hlog_close(log);
        }

        /* Sesja 2: reopen — aktywny powinien być segment 2 */
        {
            var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
            Assert.NotEqual(IntPtr.Zero, log);
            Assert.Equal(2u, NativeHLogFacade.hlog_segment_count(log));
            Assert.Equal(2UL, NativeHLogFacade.hlog_active_seq(log));
            NativeHLogFacade.hlog_close(log);
        }
    }

    /* ── hlog_visit_range ────────────────────────────────────────────────── */

    private static int s_visitCount;
    private static long s_lastVisitedTs;

    private static int CountVisitor(IntPtr seg,
                                     ref NativeHLog.HlogRecordHeader hdr,
                                     IntPtr payload,
                                     IntPtr ctx)
    {
        s_visitCount++;
        s_lastVisitedTs = hdr.timestamp;
        return 0; /* kontynuuj */
    }

    [Fact]
    public void VisitRange_ReturnsMatchingRecords()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* Dopisz 10 rekordów ts=100..109 */
        for (int i = 0; i < 10; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 100L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        s_visitCount = 0;
        s_lastVisitedTs = 0;
        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitor);
        int total = NativeHLogFacade.hlog_visit_range(log, 103L, 106L, fn, IntPtr.Zero);

        Assert.Equal(4, total);
        Assert.Equal(4, s_visitCount);
        Assert.Equal(106L, s_lastVisitedTs);
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void VisitRange_EmptyResult_ReturnsZero()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 5; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 100L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        s_visitCount = 0;
        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitor);
        int total = NativeHLogFacade.hlog_visit_range(log, 500L, 999L, fn, IntPtr.Zero);

        Assert.Equal(0, total);
        Assert.Equal(0, s_visitCount);
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void VisitRange_AcrossSegments()
    {
        uint maxBytes = 4500u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        /* Dopisz 200 rekordów — rotacja nastąpi po drodze */
        for (int i = 0; i < 200; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_DEBUG, 1000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        Assert.True(NativeHLogFacade.hlog_segment_count(log) >= 2,
                    "Powinno być co najmniej 2 segmenty po 200 rekordach z małym limitem");

        /* Odwiedź wszystkie rekordy w pełnym zakresie */
        s_visitCount = 0;
        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitor);
        int total = NativeHLogFacade.hlog_visit_range(log, 1000L, 1199L, fn, IntPtr.Zero);

        Assert.Equal(200, total);
        Assert.Equal(200, s_visitCount);
        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void VisitRange_StopsOnNonZeroReturn()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 10; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 100L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }

        /* Visitor zatrzymuje po 3 rekordach */
        int callCount = 0;
        NativeHLogFacade.HlogVisitFn stopper = (seg, ref hdr, payload, ctx) =>
        {
            callCount++;
            return callCount >= 3 ? 1 : 0;
        };

        int total = NativeHLogFacade.hlog_visit_range(log, 100L, 109L, stopper, IntPtr.Zero);

        Assert.Equal(3, total);
        Assert.Equal(3, callCount);
        NativeHLogFacade.hlog_close(log);
    }

    /* ── Indeks błędów ───────────────────────────────────────────────────── */

    [Fact]
    public void ErrorIndex_BuiltDuringAppend()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* 10 INFO + 2 ERROR + 1 FATAL */
        for (int i = 0; i < 10; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 500L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }
        var err1 = MakeRec(NativeHLog.HLOG_LEVEL_ERROR, 600L);
        NativeHLogFacade.hlog_append(log, ref err1);
        FreeRec(ref err1);

        var err2 = MakeRec(NativeHLog.HLOG_LEVEL_FATAL, 700L);
        NativeHLogFacade.hlog_append(log, ref err2);
        FreeRec(ref err2);

        NativeHLogFacade.hlog_close(log);

        /* Sprawdź plik indeksu błędów — powinien mieć 2 wpisy */
        var idxPath = Path.Combine(_dir, "hlog_000001.hidx_error");
        Assert.True(File.Exists(idxPath));

        var idx = NativeHLogIndex.hlog_index_open(idxPath, NativeHLogIndex.HLOG_IDX_ERROR_MAGIC);
        Assert.NotEqual(IntPtr.Zero, idx);
        Assert.Equal(2UL, NativeHLogIndex.hlog_index_entry_count(idx));

        NativeHLogIndex.hlog_index_read_at(idx, 0, out var e0);
        NativeHLogIndex.hlog_index_read_at(idx, 1, out var e1);
        Assert.Equal(600L, e0.timestamp);
        Assert.Equal(700L, e1.timestamp);

        NativeHLogIndex.hlog_index_close(idx);
    }

    /* ── Time index stride ───────────────────────────────────────────────── */

    [Fact]
    public void TimeIndex_EntriesAtStrideBoundaries()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* 130 rekordów → oczekiwane wpisy przy 0, 64, 128 */
        for (int i = 0; i < 130; i++)
        {
            var rec = MakeRec(NativeHLog.HLOG_LEVEL_TRACE, 10_000L + i);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }
        NativeHLogFacade.hlog_close(log);

        var idxPath = Path.Combine(_dir, "hlog_000001.hidx_time");
        var idx = NativeHLogIndex.hlog_index_open(idxPath, NativeHLogIndex.HLOG_IDX_TIME_MAGIC);
        Assert.NotEqual(IntPtr.Zero, idx);
        Assert.Equal(3UL, NativeHLogIndex.hlog_index_entry_count(idx));

        NativeHLogIndex.hlog_index_read_at(idx, 0, out var e0);
        NativeHLogIndex.hlog_index_read_at(idx, 1, out var e1);
        NativeHLogIndex.hlog_index_read_at(idx, 2, out var e2);
        Assert.Equal(10_000L, e0.timestamp);
        Assert.Equal(10_000L + 64, e1.timestamp);
        Assert.Equal(10_000L + 128, e2.timestamp);

        NativeHLogIndex.hlog_index_close(idx);
    }
}
