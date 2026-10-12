using Serilog;
using Serilog.Events;

namespace HobiaryLogDB.Tests;

/// <summary>
/// Etap 8 — testy Serilog sink <see cref="HobiaryLogSink"/>.
/// </summary>
public class SerilogSinkTests : IDisposable
{
    private readonly string _dir;

    public SerilogSinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "hlog_sl8_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private ILogger BuildLogger(HobiaryLogClient client,
        LogEventLevel minLevel = LogEventLevel.Verbose)
        => new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.HobiaryLog(client, minLevel)
            .CreateLogger();

    private ILogger BuildLoggerWithDir(LogEventLevel minLevel = LogEventLevel.Verbose)
        => new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.HobiaryLog(_dir, restrictedToMinimumLevel: minLevel)
            .CreateLogger();

    /* ── Testy ───────────────────────────────────────────────────────────── */

    [Fact]
    public void Information_WrittenToLog()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        logger.Information("Hello {World}", "HobiaryLog");
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.Equal(HlogLevel.Info, records[0].Level);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, HlogLevel.Trace)]
    [InlineData(LogEventLevel.Debug, HlogLevel.Debug)]
    [InlineData(LogEventLevel.Information, HlogLevel.Info)]
    [InlineData(LogEventLevel.Warning, HlogLevel.Warn)]
    [InlineData(LogEventLevel.Error, HlogLevel.Error)]
    [InlineData(LogEventLevel.Fatal, HlogLevel.Fatal)]
    public void LevelMapping_IsCorrect(LogEventLevel serilogLevel, HlogLevel expectedLevel)
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        /* Wyślij jedno zdarzenie na danym poziomie */
        switch (serilogLevel)
        {
            case LogEventLevel.Verbose: logger.Verbose("v"); break;
            case LogEventLevel.Debug: logger.Debug("d"); break;
            case LogEventLevel.Information: logger.Information("i"); break;
            case LogEventLevel.Warning: logger.Warning("w"); break;
            case LogEventLevel.Error: logger.Error("e"); break;
            case LogEventLevel.Fatal: logger.Fatal("f"); break;
        }
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.Equal(expectedLevel, records[0].Level);
    }

    [Fact]
    public void RenderedMessage_StoredAsPayload()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        logger.Information("Count = {Count}", 42);
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.NotNull(records[0].Payload);

        string text = System.Text.Encoding.UTF8.GetString(records[0].Payload!);
        Assert.Contains("42", text);
    }

    [Fact]
    public void SourceContext_StoredAsSource()
    {
        using var client = new HobiaryLogClient(_dir);
        /* ForContext ustawia SourceContext */
        var logger = BuildLogger(client)
            .ForContext("SourceContext", "MyApp.Service");

        logger.Information("started");
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.Equal("MyApp.Service", records[0].Source);
    }

    [Fact]
    public void MinimumLevel_FiltersLowPriorityEvents()
    {
        using var client = new HobiaryLogClient(_dir);
        /* Sink przyjmuje tylko Warning+ */
        var logger = BuildLogger(client, minLevel: LogEventLevel.Warning);

        logger.Debug("debug — pominięte");
        logger.Information("info — pominięte");
        logger.Warning("warn — zapisane");
        logger.Error("error — zapisane");
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.True(r.Level >= HlogLevel.Warn));
    }

    [Fact]
    public void MultipleEvents_AllWritten()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        for (int i = 0; i < 10; i++) logger.Information("msg {I}", i);
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Equal(10, records.Count);
    }

    [Fact]
    public void Timestamp_StoredAsUnixMicroseconds()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        long beforeUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        logger.Information("ts test");
        long afterUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.InRange(records[0].Timestamp, beforeUs - 1_000_000L, afterUs + 1_000_000L);
    }

    [Fact]
    public void SinkWithDir_OpenAndClosesOwnClient()
    {
        /* Sink z konstruktorem dir — sam zarządza klientem */
        var logger = BuildLoggerWithDir();

        logger.Information("test via dir-sink");
        (logger as IDisposable)?.Dispose(); /* zamknie sink → zamknie klienta */

        /* Reopen i zweryfikuj */
        using var client2 = new HobiaryLogClient(_dir);
        var records = client2.ReadRange();
        Assert.Single(records);
    }

    [Fact]
    public void EmptyMessage_DoesNotCrash()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        logger.Information(""); /* pusty komunikat */
        (logger as IDisposable)?.Dispose();

        /* Nie rzucono wyjątku; rekord może mieć null/pusty payload */
        var records = client.ReadRange();
        Assert.Single(records);
    }

    [Fact]
    public void ExceptionProperty_DoesNotBreakSink()
    {
        using var client = new HobiaryLogClient(_dir);
        var logger = BuildLogger(client);

        try { throw new InvalidOperationException("test error"); }
        catch (Exception ex) { logger.Error(ex, "caught {Ex}", ex.Message); }
        (logger as IDisposable)?.Dispose();

        var records = client.ReadRange();
        Assert.Single(records);
        Assert.Equal(HlogLevel.Error, records[0].Level);
    }
}
