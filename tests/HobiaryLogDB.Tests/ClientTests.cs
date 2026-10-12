namespace HobiaryLogDB.Tests;

/// <summary>
/// Etap 7 — testy zarządzanego wrappera <see cref="HobiaryLogClient"/>.
/// </summary>
public class ClientTests : IDisposable
{
    private readonly string _dir;

    public ClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "hlog_cl7_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private static LogRecord Rec(HlogLevel lvl, long ts,
        string? src = null, byte[]? payload = null)
        => new(lvl, ts, Source: src, Payload: payload);

    /* ── Testy ───────────────────────────────────────────────────────────── */

    [Fact]
    public void Append_AndReadRange_ReturnsRecord()
    {
        using var client = new HobiaryLogClient(_dir);

        int rc = client.Append(Rec(HlogLevel.Info, 1000L));
        Assert.Equal(0, rc);          /* HLOG_OK */

        var list = client.ReadRange();
        Assert.Single(list);
        Assert.Equal(HlogLevel.Info, list[0].Level);
        Assert.Equal(1000L, list[0].Timestamp);
    }

    [Fact]
    public void Append_WithPayload_PayloadPreserved()
    {
        using var client = new HobiaryLogClient(_dir);

        byte[] data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; /* "Hello" */
        client.Append(Rec(HlogLevel.Debug, 2000L, payload: data));

        var list = client.ReadRange();
        Assert.Single(list);
        Assert.NotNull(list[0].Payload);
        Assert.Equal(data, list[0].Payload);
    }

    [Fact]
    public void Append_WithSource_SourcePreserved()
    {
        using var client = new HobiaryLogClient(_dir);

        client.Append(Rec(HlogLevel.Warn, 3000L, src: "engine"));

        var list = client.ReadRange();
        Assert.Single(list);
        Assert.Equal("engine", list[0].Source);
    }

    [Fact]
    public void VisitLevel_FiltersCorrectly()
    {
        using var client = new HobiaryLogClient(_dir);

        for (int i = 0; i < 4; i++) client.Append(Rec(HlogLevel.Info, 1000L + i));
        for (int i = 0; i < 2; i++) client.Append(Rec(HlogLevel.Error, 2000L + i));

        int infoCount = 0, errCount = 0;
        client.VisitLevel(HlogLevel.Info, r => { infoCount++; return true; });
        client.VisitLevel(HlogLevel.Error, r => { errCount++; return true; });

        Assert.Equal(4, infoCount);
        Assert.Equal(2, errCount);
    }

    [Fact]
    public void VisitSource_FiltersCorrectly()
    {
        using var client = new HobiaryLogClient(_dir);

        for (int i = 0; i < 3; i++) client.Append(Rec(HlogLevel.Info, 1000L + i, src: "app"));
        for (int i = 0; i < 2; i++) client.Append(Rec(HlogLevel.Info, 2000L + i, src: "db"));

        int appCount = 0;
        int total = client.VisitSource(1 /* "app" — pierwsze internowane źródło */, r =>
        {
            appCount++;
            return true;
        });

        Assert.Equal(3, total);
        Assert.Equal(3, appCount);
    }

    [Fact]
    public void VisitRange_EarlyExit_StopsIteration()
    {
        using var client = new HobiaryLogClient(_dir);
        for (int i = 0; i < 10; i++) client.Append(Rec(HlogLevel.Info, 1000L + i));

        int visited = 0;
        /* Zwróć false po 3 rekordach → iteracja przerywana */
        client.VisitRange(long.MinValue, long.MaxValue, r =>
        {
            visited++;
            return visited < 3;
        });

        Assert.Equal(3, visited);
    }

    [Fact]
    public void Properties_SegmentCount_ActiveSeq_ActiveRecordCount()
    {
        using var client = new HobiaryLogClient(_dir);

        Assert.Equal(1u, client.SegmentCount);
        Assert.Equal(1UL, client.ActiveSeq);
        Assert.Equal(0UL, client.ActiveRecordCount);

        client.Append(Rec(HlogLevel.Info, 1000L));
        client.Append(Rec(HlogLevel.Info, 2000L));

        Assert.Equal(2UL, client.ActiveRecordCount);
    }

    [Fact]
    public void PurgeBefore_RemovesOldSegments()
    {
        /* Mały limit segmentu → wymuszamy rotację */
        const uint SmallSeg = 8 * 1024; /* 8 KB */
        using var client = new HobiaryLogClient(_dir, SmallSeg);

        /* Zapisz dużo rekordów żeby wymusić rotację i zapieczętowanie seg 1 */
        byte[] pad = new byte[256];
        for (int i = 0; i < 30; i++) client.Append(Rec(HlogLevel.Info, 1000L + i * 1000, payload: pad));

        uint segsAfterAppend = client.SegmentCount;
        Assert.True(segsAfterAppend >= 2, $"Oczekiwano rotacji (segów={segsAfterAppend})");

        /* Purge ze znacznikiem >= ostatni timestamp → usuwa starsze zapieczętowane */
        int purged = client.PurgeBefore(long.MaxValue);
        Assert.True(purged > 0, $"Oczekiwano usunięcia segmentów (purged={purged})");
    }

    [Fact]
    public void ReadRange_ReturnsOnlyRecordsInRange()
    {
        using var client = new HobiaryLogClient(_dir);

        for (int i = 1; i <= 10; i++) client.Append(Rec(HlogLevel.Info, (long)i * 1000));

        /* Tylko ts 3000..7000 */
        var list = client.ReadRange(fromTs: 3000L, toTs: 7000L);
        Assert.Equal(5, list.Count);
        Assert.All(list, r => Assert.InRange(r.Timestamp, 3000L, 7000L));
    }

    [Fact]
    public void Dispose_PreventsFurtherUse()
    {
        var client = new HobiaryLogClient(_dir);
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            client.Append(Rec(HlogLevel.Info, 1L)));
    }

    [Fact]
    public void ReopenLog_PreviousRecordsVisible()
    {
        using (var client = new HobiaryLogClient(_dir))
        {
            for (int i = 0; i < 5; i++)
                client.Append(Rec(HlogLevel.Info, 1000L + i));
        }

        using (var client2 = new HobiaryLogClient(_dir))
        {
            var list = client2.ReadRange();
            Assert.Equal(5, list.Count);
        }
    }
}
