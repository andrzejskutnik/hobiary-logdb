using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

/// <summary>
/// Etap 6 — crash recovery:
///   - automatyczna odbudowa indeksów gdy brakuje pliku lub CRC jest złe
///   - automatyczne obcięcie segmentu gdy ogon jest uszkodzony
/// </summary>
public class CrashRecoveryTests : IDisposable
{
    private readonly string _dir;

    public CrashRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "hlog_cr6_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private static NativeHLog.HlogRecordInput MakeRec(byte level, long ts,
        string? source = null)
    {
        return new NativeHLog.HlogRecordInput
        {
            level = level,
            flags = 0,
            timestamp = ts,
            ingested_at = 0,
            source = source != null ? Marshal.StringToHGlobalAnsi(source) : IntPtr.Zero,
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

    private static void FreeRec(ref NativeHLog.HlogRecordInput r)
    {
        if (r.source != IntPtr.Zero) { Marshal.FreeHGlobal(r.source); r.source = IntPtr.Zero; }
        if (r.payload != IntPtr.Zero) { Marshal.FreeHGlobal(r.payload); r.payload = IntPtr.Zero; }
    }

    private void AppendN(IntPtr log, int n, byte level = NativeHLog.HLOG_LEVEL_INFO,
        string? source = null)
    {
        for (int i = 0; i < n; i++)
        {
            var rec = MakeRec(level, 1000L + i, source);
            NativeHLogFacade.hlog_append(log, ref rec);
            FreeRec(ref rec);
        }
    }

    private static int s_count;
    private static int CountFn(IntPtr seg, ref NativeHLog.HlogRecordHeader hdr,
        IntPtr payload, IntPtr ctx)
    {
        s_count++;
        return 0;
    }

    /* ── Testy ───────────────────────────────────────────────────────────── */

    [Fact]
    public void ScanValid_EmptySegment_ReturnsZero()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        NativeHLogFacade.hlog_close(log);

        var segPath = Path.Combine(_dir, "hlog_000001.hlog");
        var seg = NativeHLog.hlog_segment_open(segPath);
        Assert.NotEqual(IntPtr.Zero, seg);

        int count = NativeHLog.hlog_segment_scan_valid(seg, out uint endOff, out long lastTs);
        NativeHLog.hlog_segment_close(seg, 0);

        Assert.Equal(0, count);
        Assert.Equal(NativeHLog.HLOG_FIRST_RECORD_OFFSET, endOff);
        Assert.Equal(0L, lastTs);
    }

    [Fact]
    public void ScanValid_CountsAllRecords()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 10);
        NativeHLogFacade.hlog_close(log);

        var segPath = Path.Combine(_dir, "hlog_000001.hlog");
        var seg = NativeHLog.hlog_segment_open(segPath);
        Assert.NotEqual(IntPtr.Zero, seg);

        int count = NativeHLog.hlog_segment_scan_valid(seg, out uint endOff, out _);
        NativeHLog.hlog_segment_close(seg, 0);

        Assert.Equal(10, count);
        Assert.True(endOff > NativeHLog.HLOG_FIRST_RECORD_OFFSET);
    }

    [Fact]
    public void MissingTimeIndex_AutoRebuilt_VisitRangeWorks()
    {
        /* 1. Utwórz segment z 5 rekordami */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 5);
        NativeHLogFacade.hlog_close(log);

        /* 2. Usuń hidx_time */
        File.Delete(Path.Combine(_dir, "hlog_000001.hidx_time"));

        /* 3. Reopen — powinien odbudować */
        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* 4. Plik indeksu czasu powinien znowu istnieć */
        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_time")));

        /* 5. visit_range zwraca 5 rekordów */
        var fn = new NativeHLogFacade.HlogVisitFn(CountFn);
        s_count = 0;
        int total = NativeHLogFacade.hlog_visit_range(log, long.MinValue, long.MaxValue, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(5, total);
        Assert.Equal(5, s_count);
    }

    [Fact]
    public void MissingLevelIndex_AutoRebuilt_VisitLevelWorks()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* 3 INFO + 2 WARN */
        for (int i = 0; i < 3; i++) { var r = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 1000L + i); NativeHLogFacade.hlog_append(log, ref r); FreeRec(ref r); }
        for (int i = 0; i < 2; i++) { var r = MakeRec(NativeHLog.HLOG_LEVEL_WARN, 2000L + i); NativeHLogFacade.hlog_append(log, ref r); FreeRec(ref r); }

        NativeHLogFacade.hlog_close(log);

        /* Usuń hidx_level */
        File.Delete(Path.Combine(_dir, "hlog_000001.hidx_level"));

        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        Assert.True(File.Exists(Path.Combine(_dir, "hlog_000001.hidx_level")));

        var fn = new NativeHLogFacade.HlogVisitFn(CountFn);
        s_count = 0;
        int infoTotal = NativeHLogFacade.hlog_visit_level(log, NativeHLog.HLOG_LEVEL_INFO, fn, IntPtr.Zero);
        s_count = 0;
        int warnTotal = NativeHLogFacade.hlog_visit_level(log, NativeHLog.HLOG_LEVEL_WARN, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(3, infoTotal);
        Assert.Equal(2, warnTotal);
    }

    [Fact]
    public void CorruptTimeIndex_AutoRebuilt()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 8);
        NativeHLogFacade.hlog_close(log);

        /* Uszkódź nagłówek hidx_time (nadpisz pierwsze 4 bajty — magic) */
        var idxPath = Path.Combine(_dir, "hlog_000001.hidx_time");
        using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Write))
        {
            fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 0, 4);
        }

        /* Reopen — zły magic → rebuild */
        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* visit_range musi działać poprawnie */
        var fn = new NativeHLogFacade.HlogVisitFn(CountFn);
        s_count = 0;
        int total = NativeHLogFacade.hlog_visit_range(log, long.MinValue, long.MaxValue, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(8, total);
    }

    [Fact]
    public void AllIndexesMissing_AutoRebuilt_AllQueriesWork()
    {
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        /* 4 INFO z source, 2 ERROR bez source */
        for (int i = 0; i < 4; i++) { var r = MakeRec(NativeHLog.HLOG_LEVEL_INFO, 1000L + i, "app"); NativeHLogFacade.hlog_append(log, ref r); FreeRec(ref r); }
        for (int i = 0; i < 2; i++) { var r = MakeRec(NativeHLog.HLOG_LEVEL_ERROR, 2000L + i); NativeHLogFacade.hlog_append(log, ref r); FreeRec(ref r); }

        NativeHLogFacade.hlog_close(log);

        /* Usuń wszystkie 4 pliki indeksów */
        foreach (var ext in new[] { "hidx_time", "hidx_error", "hidx_level", "hidx_src" })
            File.Delete(Path.Combine(_dir, $"hlog_000001.{ext}"));

        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        var fn = new NativeHLogFacade.HlogVisitFn(CountFn);

        s_count = 0;
        int rangeTotal = NativeHLogFacade.hlog_visit_range(log, long.MinValue, long.MaxValue, fn, IntPtr.Zero);
        Assert.Equal(6, rangeTotal);

        s_count = 0;
        int levelTotal = NativeHLogFacade.hlog_visit_level(log, NativeHLog.HLOG_LEVEL_INFO, fn, IntPtr.Zero);
        Assert.Equal(4, levelTotal);

        s_count = 0;
        int srcTotal = NativeHLogFacade.hlog_visit_source(log, 1 /* "app" */, fn, IntPtr.Zero);
        Assert.Equal(4, srcTotal);

        NativeHLogFacade.hlog_close(log);
    }

    [Fact]
    public void TruncatedSegment_AutoRecovered_RecordCountReduced()
    {
        /* Dopisz 10 rekordów */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 10);
        NativeHLogFacade.hlog_close(log);

        /* Znajdź rozmiar po 5 rekordach używając hlog_segment_scan_valid */
        var segPath = Path.Combine(_dir, "hlog_000001.hlog");
        var seg = NativeHLog.hlog_segment_open(segPath);
        Assert.NotEqual(IntPtr.Zero, seg);

        /* Skanuj do 5 rekordów ręcznie, żeby znaleźć offset */
        /* Prostsze: odczytaj rekord[4] (0-based) i weź cur po nim */
        uint cur = NativeHLog.HLOG_FIRST_RECORD_OFFSET;
        for (int i = 0; i < 5; i++)
        {
            int rc = NativeHLog.hlog_segment_read_at(seg, cur,
                out NativeHLog.HlogRecordHeader hdr, out IntPtr pay);
            if (pay != IntPtr.Zero) NativeHLog.hlog_free(pay);
            Assert.Equal(NativeHLog.HLOG_OK, rc);
            cur += hdr.record_len;
        }
        uint cutAt = cur; /* offset za rekordem [4] */
        NativeHLog.hlog_segment_close(seg, 0);

        /* Obetnij plik do cutAt */
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.Write))
            fs.SetLength(cutAt);

        /* Reopen — powinien rozpoznać truncation i naprawić */
        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);

        ulong recCount = NativeHLogFacade.hlog_active_record_count(log);

        var fn = new NativeHLogFacade.HlogVisitFn(CountFn);
        s_count = 0;
        int total = NativeHLogFacade.hlog_visit_range(log, long.MinValue, long.MaxValue, fn, IntPtr.Zero);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(5UL, recCount);
        Assert.Equal(5, total);
    }

    [Fact]
    public void RecoveredSegment_AppendStillWorks()
    {
        /* 1. Utwórz log, 5 rekordów */
        var log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 5);
        NativeHLogFacade.hlog_close(log);

        /* 2. Usuń hidx_src */
        File.Delete(Path.Combine(_dir, "hlog_000001.hidx_src"));

        /* 3. Reopen + append 3 nowych */
        log = NativeHLogFacade.hlog_open(_dir, 0);
        Assert.NotEqual(IntPtr.Zero, log);
        AppendN(log, 3);
        NativeHLogFacade.hlog_close(log);

        /* 4. Sprawdź łączną liczbę rekordów */
        log = NativeHLogFacade.hlog_open(_dir, 0);
        ulong count = NativeHLogFacade.hlog_active_record_count(log);
        NativeHLogFacade.hlog_close(log);

        Assert.Equal(8UL, count);
    }
}
