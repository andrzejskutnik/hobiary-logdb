using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

/// <summary>
/// Etap 5 — testy indeksów level (hidx_level) i source (hidx_src)
/// oraz fasadowych metod hlog_visit_level / hlog_visit_source.
/// </summary>
public class Level5Tests : IDisposable
{
    private readonly string _dir;

    public Level5Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "hlog_lvl5_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private static NativeHLog.HlogRecordInput MakeRec(
        byte level, long ts, string? source = null)
    {
        return new NativeHLog.HlogRecordInput
        {
            level = level,
            flags = 0,
            timestamp = ts,
            ingested_at = 0,
            source = source != null
                               ? Marshal.StringToHGlobalAnsi(source)
                               : IntPtr.Zero,
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
        if (rec.payload != IntPtr.Zero) { Marshal.FreeHGlobal(rec.payload); rec.payload = IntPtr.Zero; }
    }

    private static void Append(IntPtr log, byte level, long ts, string? source = null)
    {
        var rec = MakeRec(level, ts, source);
        NativeHLogFacade.hlog_append(log, ref rec);
        FreeRec(ref rec);
    }

    /* Delegate-based visitor (bezpieczny sposób) */
    private static int CountVisitorDelegate(IntPtr seg,
        ref NativeHLog.HlogRecordHeader hdr, IntPtr payload, IntPtr ctx)
    {
        s_visitCount++;
        return 0;
    }

    /* ── Testy ───────────────────────────────────────────────────────────── */

    private static int s_visitCount;

    [Fact]
    public void LevelAndSrcIndexFiles_Created_OnNewSegment()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L, "srv");
        NativeHLogFacade.hlog_close(log);

        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_level")));
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_src")));
    }

    [Fact]
    public void LevelIndex_EntryCount_MatchesRecordCount()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 5; i++)
            Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L + i);

        NativeHLogFacade.hlog_close(log);

        /* Odczytaj plik .hidx_level bezpośrednio i sprawdź entry_count */
        var idxPath = Path.Combine(_dir, "hlog_000001.hidx_level");
        var idx = NativeHLogIndex.hlog_index_open(idxPath, NativeHLogIndex.HLOG_IDX_LEVEL_MAGIC);
        Assert.NotEqual(IntPtr.Zero, idx);

        ulong count = NativeHLogIndex.hlog_index_entry_count(idx);
        NativeHLogIndex.hlog_index_close(idx);

        Assert.Equal(5UL, count);
    }

    [Fact]
    public void SrcIndex_EntryCount_MatchesRecordsWithNonNullSource()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L, "alpha");   /* source_id = 1 */
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 101L);             /* brak source → 0 */
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 102L, "alpha");   /* source_id = 1 */
        Append(log, NativeHLog.HLOG_LEVEL_WARN, 103L, "beta");    /* source_id = 2 */

        NativeHLogFacade.hlog_close(log);

        var idxPath = Path.Combine(_dir, "hlog_000001.hidx_src");
        var idx = NativeHLogIndex.hlog_index_open(idxPath, NativeHLogIndex.HLOG_IDX_SRC_MAGIC);
        Assert.NotEqual(IntPtr.Zero, idx);

        ulong count = NativeHLogIndex.hlog_index_entry_count(idx);
        NativeHLogIndex.hlog_index_close(idx);

        /* 3 rekordy mają source (alpha, alpha, beta); 1 bez source */
        Assert.Equal(3UL, count);
    }

    [Fact]
    public void VisitLevel_ReturnsCorrectCount_SingleSegment()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L); /* INFO  */
        Append(log, NativeHLog.HLOG_LEVEL_WARN, 101L); /* WARN  */
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 102L); /* INFO  */
        Append(log, NativeHLog.HLOG_LEVEL_ERROR, 103L); /* ERROR */
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 104L); /* INFO  */

        NativeHLogFacade.hlog_close(log);
        log = NativeHLogFacade.hlog_open(_dir, 0);

        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitorDelegate);
        s_visitCount = 0;
        int total = NativeHLogFacade.hlog_visit_level(log, NativeHLog.HLOG_LEVEL_INFO, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(3, total);
        Assert.Equal(3, s_visitCount);
    }

    [Fact]
    public void VisitSource_ReturnsCorrectCount_SingleSegment()
    {
        /* alpha → source_id=1, beta → source_id=2 */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L, "alpha");
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 101L, "beta");
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 102L, "alpha");
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 103L);          /* bez source */
        Append(log, NativeHLog.HLOG_LEVEL_INFO, 104L, "alpha");

        NativeHLogFacade.hlog_close(log);
        log = NativeHLogFacade.hlog_open(_dir, 0);

        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitorDelegate);
        s_visitCount = 0;
        int total = NativeHLogFacade.hlog_visit_source(log, 1 /* alpha */, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(3, total);
        Assert.Equal(3, s_visitCount);
    }

    [Fact]
    public void VisitLevel_AcrossSegments()
    {
        /* Mały segment → szybka rotacja */
        uint maxBytes = NativeHLog.HLOG_FIRST_RECORD_OFFSET + 200u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        /* Dopisuj aż do co najmniej 2 segmentów */
        bool seg2 = false;
        for (int i = 0; i < 200 && !seg2; i++)
        {
            byte lvl = (i % 2 == 0)
                ? NativeHLog.HLOG_LEVEL_DEBUG
                : NativeHLog.HLOG_LEVEL_WARN;
            Append(log, lvl, 100L + i);
            if (NativeHLogFacade.hlog_active_seq(log) >= 2) seg2 = true;
        }
        Assert.True(seg2, "Powinny powstać co najmniej 2 segmenty");

        NativeHLogFacade.hlog_close(log);
        log = NativeHLogFacade.hlog_open(_dir, maxBytes);

        var fn = new NativeHLogFacade.HlogVisitFn(CountVisitorDelegate);

        s_visitCount = 0;
        int debugTotal = NativeHLogFacade.hlog_visit_level(log,
            NativeHLog.HLOG_LEVEL_DEBUG, fn, IntPtr.Zero);

        s_visitCount = 0;
        int warnTotal = NativeHLogFacade.hlog_visit_level(log,
            NativeHLog.HLOG_LEVEL_WARN, fn, IntPtr.Zero);

        NativeHLogFacade.hlog_close(log);

        /* Oba poziomy muszą mieć co najmniej 1 rekord */
        Assert.True(debugTotal > 0, "Powinny być rekordy DEBUG");
        Assert.True(warnTotal > 0, "Powinny być rekordy WARN");
        Assert.True(debugTotal + warnTotal > 1);
    }

    [Fact]
    public void VisitLevel_StopsOnNonZeroReturn()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        for (int i = 0; i < 10; i++)
            Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L + i);

        NativeHLogFacade.hlog_close(log);
        log = NativeHLogFacade.hlog_open(_dir, 0);

        int callCount = 0;
        var fn = new NativeHLogFacade.HlogVisitFn((seg, ref hdr, payload, ctx) =>
        {
            callCount++;
            return callCount >= 3 ? 1 : 0; /* zatrzymaj po 3 */
        });

        int total = NativeHLogFacade.hlog_visit_level(log,
            NativeHLog.HLOG_LEVEL_INFO, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(3, total);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void PurgeBeforeDeletesLevelAndSrcFiles()
    {
        uint maxBytes = NativeHLog.HLOG_FIRST_RECORD_OFFSET + 300u;
        var log = NativeHLogFacade.hlog_open(_dir, maxBytes);
        Assert.NotEqual(IntPtr.Zero, log);

        /* Wypełnij co najmniej 2 segmenty */
        bool seg2 = false;
        for (int i = 0; i < 200 && !seg2; i++)
        {
            Append(log, NativeHLog.HLOG_LEVEL_INFO, 100L + i, "src");
            if (NativeHLogFacade.hlog_active_seq(log) >= 2) seg2 = true;
        }
        Assert.True(seg2);

        NativeHLogFacade.hlog_close(log);
        log = NativeHLogFacade.hlog_open(_dir, maxBytes);

        int removed = NativeHLogFacade.hlog_purge_before(log, long.MaxValue);
        Assert.True(removed >= 1);

        NativeHLogFacade.hlog_close(log);

        /* Pliki .hidx_level i .hidx_src segmentu 1 powinny zniknąć */
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_level")));
        Assert.False(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_src")));
    }
}
