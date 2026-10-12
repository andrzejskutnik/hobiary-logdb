namespace HobiaryLogDB.Tests;

public class IndexTests : IDisposable
{
    private readonly string _dir;

    public IndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hlog_idx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /* ── Testy tworzenia ─────────────────────────────────────────────────── */

    [Fact]
    public void CanCreateTimeIndex()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("t.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        Assert.NotEqual(IntPtr.Zero, idx);
        Assert.True(File.Exists(P("t.hidx_time")));
        Assert.Equal(0UL, NativeHLogIndex.hlog_index_entry_count(idx));
        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void CanCreateErrorIndex()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("t.hidx_error"),
            NativeHLogIndex.HLOG_IDX_ERROR_MAGIC, 1);
        Assert.NotEqual(IntPtr.Zero, idx);
        Assert.Equal(0UL, NativeHLogIndex.hlog_index_entry_count(idx));
        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void OpenWithWrongMagic_ReturnsNull()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("t.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        NativeHLogIndex.hlog_index_close(idx);

        /* Próba otwarcia pliku time-index jako error-index */
        var idx2 = NativeHLogIndex.hlog_index_open(P("t.hidx_time"),
            NativeHLogIndex.HLOG_IDX_ERROR_MAGIC);
        Assert.Equal(IntPtr.Zero, idx2);
    }

    /* ── Testy zapisu i odczytu ──────────────────────────────────────────── */

    [Fact]
    public void CanAppendAndReadEntry()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("t.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        Assert.NotEqual(IntPtr.Zero, idx);

        int rc = NativeHLogIndex.hlog_index_append(idx, timestamp: 1000L, offset: 4160u);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(1UL, NativeHLogIndex.hlog_index_entry_count(idx));

        rc = NativeHLogIndex.hlog_index_read_at(idx, 0, out var entry);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(1000L, entry.timestamp);
        Assert.Equal(4160u, entry.offset);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void EntryCountPersistedAcrossReopen()
    {
        var path = P("t2.hidx_time");
        var idx = NativeHLogIndex.hlog_index_create(path,
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 2);
        Assert.NotEqual(IntPtr.Zero, idx);

        for (int i = 0; i < 5; i++)
            NativeHLogIndex.hlog_index_append(idx, timestamp: 1000L + i, offset: (uint)(4160 + i * 100));
        NativeHLogIndex.hlog_index_close(idx);

        var idx2 = NativeHLogIndex.hlog_index_open(path, NativeHLogIndex.HLOG_IDX_TIME_MAGIC);
        Assert.NotEqual(IntPtr.Zero, idx2);
        Assert.Equal(5UL, NativeHLogIndex.hlog_index_entry_count(idx2));
        NativeHLogIndex.hlog_index_close(idx2);
    }

    [Fact]
    public void ReadAt_OutOfBounds_ReturnsNotFound()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("t3.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        Assert.NotEqual(IntPtr.Zero, idx);
        NativeHLogIndex.hlog_index_append(idx, 1000L, 4160u);

        int rc = NativeHLogIndex.hlog_index_read_at(idx, pos: 99, out _);
        Assert.Equal(NativeHLog.HLOG_ERR_NOT_FOUND, rc);

        NativeHLogIndex.hlog_index_close(idx);
    }

    /* ── Testy binary search (find_ge) ───────────────────────────────────── */

    [Fact]
    public void FindGe_ExactMatch()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fg1.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 100L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 200L, 5000u);
        NativeHLogIndex.hlog_index_append(idx, 300L, 6000u);

        int rc = NativeHLogIndex.hlog_index_find_ge(idx, 200L, out uint off, out long ts);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(5000u, off);
        Assert.Equal(200L, ts);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindGe_BetweenEntries_ReturnsNext()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fg2.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 100L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 300L, 6000u);
        NativeHLogIndex.hlog_index_append(idx, 500L, 8000u);

        /* Szukamy 150 — powinniśmy dostać wpis 300 */
        int rc = NativeHLogIndex.hlog_index_find_ge(idx, 150L, out uint off, out long ts);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(6000u, off);
        Assert.Equal(300L, ts);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindGe_BeforeAll_ReturnsFirst()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fg3.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 500L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 700L, 5000u);

        int rc = NativeHLogIndex.hlog_index_find_ge(idx, 1L, out uint off, out long ts);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(4160u, off);
        Assert.Equal(500L, ts);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindGe_AfterAll_ReturnsNotFound()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fg4.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 100L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 200L, 5000u);

        int rc = NativeHLogIndex.hlog_index_find_ge(idx, 9999L, out _, out _);
        Assert.Equal(NativeHLog.HLOG_ERR_NOT_FOUND, rc);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindGe_EmptyIndex_ReturnsNotFound()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fg5.hidx_time"),
            NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);

        int rc = NativeHLogIndex.hlog_index_find_ge(idx, 100L, out _, out _);
        Assert.Equal(NativeHLog.HLOG_ERR_NOT_FOUND, rc);

        NativeHLogIndex.hlog_index_close(idx);
    }

    /* ── Testy find_range ────────────────────────────────────────────────── */

    [Fact]
    public void FindRange_Normal()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fr1.hidx_error"),
            NativeHLogIndex.HLOG_IDX_ERROR_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 100L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 200L, 5000u);
        NativeHLogIndex.hlog_index_append(idx, 300L, 6000u);
        NativeHLogIndex.hlog_index_append(idx, 400L, 7000u);
        NativeHLogIndex.hlog_index_append(idx, 500L, 8000u);

        var buf = new NativeHLogIndex.HlogIndexEntry[10];
        int count = NativeHLogIndex.hlog_index_find_range(idx, 200L, 400L, buf, buf.Length);

        Assert.Equal(3, count);
        Assert.Equal(200L, buf[0].timestamp);
        Assert.Equal(300L, buf[1].timestamp);
        Assert.Equal(400L, buf[2].timestamp);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindRange_NoResults_ReturnsZero()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fr2.hidx_error"),
            NativeHLogIndex.HLOG_IDX_ERROR_MAGIC, 1);
        NativeHLogIndex.hlog_index_append(idx, 100L, 4160u);
        NativeHLogIndex.hlog_index_append(idx, 200L, 5000u);

        var buf = new NativeHLogIndex.HlogIndexEntry[10];
        int count = NativeHLogIndex.hlog_index_find_range(idx, 500L, 900L, buf, buf.Length);
        Assert.Equal(0, count);

        NativeHLogIndex.hlog_index_close(idx);
    }

    [Fact]
    public void FindRange_RespectsMaxEntries()
    {
        var idx = NativeHLogIndex.hlog_index_create(P("fr3.hidx_error"),
            NativeHLogIndex.HLOG_IDX_ERROR_MAGIC, 1);
        for (int i = 0; i < 20; i++)
            NativeHLogIndex.hlog_index_append(idx, 100L + i, (uint)(4160 + i * 100));

        var buf = new NativeHLogIndex.HlogIndexEntry[5];
        int count = NativeHLogIndex.hlog_index_find_range(idx, 100L, 9999L, buf, buf.Length);
        Assert.Equal(5, count); /* ograniczone przez max_entries */

        NativeHLogIndex.hlog_index_close(idx);
    }

    /* ── Test integracyjny: segment + index ──────────────────────────────── */

    [Fact]
    public void SegmentAndIndex_Integrated()
    {
        /* Symuluje to, co będzie robić fasada hlog w Etapie 3:
           dopisuje rekordy do segmentu, równolegle buduje indeksy */
        var segPath = P("hlog_000001.hlog");
        var idxTPath = P("hlog_000001.hidx_time");
        var idxEPath = P("hlog_000001.hidx_error");

        var seg = NativeHLog.hlog_segment_create(segPath, 1);
        var idxT = NativeHLogIndex.hlog_index_create(idxTPath,
                         NativeHLogIndex.HLOG_IDX_TIME_MAGIC, 1);
        var idxE = NativeHLogIndex.hlog_index_create(idxEPath,
                         NativeHLogIndex.HLOG_IDX_ERROR_MAGIC, 1);

        Assert.NotEqual(IntPtr.Zero, seg);
        Assert.NotEqual(IntPtr.Zero, idxT);
        Assert.NotEqual(IntPtr.Zero, idxE);

        /* Dopisz 130 rekordów — 3 to ERROR */
        long baseTs = 1_000_000L;
        for (int i = 0; i < 130; i++)
        {
            bool isError = (i == 10 || i == 64 || i == 128);
            byte level = isError ? NativeHLog.HLOG_LEVEL_ERROR : NativeHLog.HLOG_LEVEL_INFO;

            uint offsetBefore = NativeHLog.hlog_segment_file_size(seg);

            var rec = new NativeHLog.HlogRecordInput
            {
                level = level,
                flags = 0,
                timestamp = baseTs + i,
                ingested_at = 0,
                source = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("svc"),
                service = IntPtr.Zero,
                operation = IntPtr.Zero,
                entity_id = IntPtr.Zero,
                span_id = 0,
                payload = IntPtr.Zero,
                payload_len = 0,
                trace_id = new byte[16],
                correlation_id = new byte[16]
            };
            NativeHLog.hlog_segment_append(seg, ref rec);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(rec.source);

            /* Sparse time index co HLOG_IDX_STRIDE rekordów */
            if (i % (int)NativeHLogIndex.HLOG_IDX_STRIDE == 0)
                NativeHLogIndex.hlog_index_append(idxT, baseTs + i, offsetBefore);

            /* Error index: każdy ERROR */
            if (isError)
                NativeHLogIndex.hlog_index_append(idxE, baseTs + i, offsetBefore);
        }

        /* time index: wpisy co 64 → powinny być 3 (i=0, i=64, i=128) */
        Assert.Equal(3UL, NativeHLogIndex.hlog_index_entry_count(idxT));

        /* error index: 3 ERROR-y */
        Assert.Equal(3UL, NativeHLogIndex.hlog_index_entry_count(idxE));

        /* FindGe na time index: szukaj od i=64 */
        int rc = NativeHLogIndex.hlog_index_find_ge(idxT, baseTs + 64,
            out uint startOffset, out long foundTs);
        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(baseTs + 64, foundTs);

        /* Odczytaj rekord ze znalezionego offsetu — powinien mieć timestamp = baseTs+64 */
        int rdRc = NativeHLog.hlog_segment_read_at(seg, startOffset,
            out var hdr, out var pl);
        NativeHLog.hlog_free(pl);
        Assert.Equal(NativeHLog.HLOG_OK, rdRc);
        Assert.Equal(baseTs + 64, hdr.timestamp);

        /* FindRange na error index: wszystkie 3 błędy */
        var errBuf = new NativeHLogIndex.HlogIndexEntry[10];
        int errCount = NativeHLogIndex.hlog_index_find_range(idxE,
            baseTs, baseTs + 130, errBuf, errBuf.Length);
        Assert.Equal(3, errCount);

        NativeHLog.hlog_segment_close(seg, seal: 1);
        NativeHLogIndex.hlog_index_close(idxT);
        NativeHLogIndex.hlog_index_close(idxE);
    }
}
