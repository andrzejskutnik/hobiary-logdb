using System.Text;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace HobiaryLogDB.Tests;

/*
 * Etap 8 — Serilog sink dla HobiaryLogDB.
 *
 * Mapowanie poziomów:
 *   Verbose     → HlogLevel.Trace  (0)
 *   Debug       → HlogLevel.Debug  (1)
 *   Information → HlogLevel.Info   (2)
 *   Warning     → HlogLevel.Warn   (3)
 *   Error       → HlogLevel.Error  (4)
 *   Fatal       → HlogLevel.Fatal  (5)
 *
 * Mapowanie pól:
 *   SourceContext property → source
 *   Rendered message       → payload UTF-8
 *   EventId / RequestId    → service (jeśli obecne)
 *
 * Użycie:
 *   var log = new LoggerConfiguration()
 *       .WriteTo.HobiaryLog(client)
 *       .CreateLogger();
 */

/// <summary>
/// Serilog sink zapisujący zdarzenia do <see cref="HobiaryLogClient"/>.
/// Sink nie przejmuje własności klienta — wywołujący odpowiada za jego Dispose.
/// </summary>
public sealed class HobiaryLogSink : ILogEventSink, IDisposable
{
    private readonly HobiaryLogClient _client;
    private readonly bool _ownsClient;
    private readonly IFormatProvider? _formatProvider;
    private bool _disposed;

    /* ── Konstruktory ─────────────────────────────────────────────────────── */

    /// <summary>
    /// Sink z istniejącym klientem (sink nie wywołuje Dispose na kliencie).
    /// </summary>
    public HobiaryLogSink(HobiaryLogClient client,
                          IFormatProvider? formatProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = false;
        _formatProvider = formatProvider;
    }

    /// <summary>
    /// Sink otwierający własny klient dla <paramref name="logDir"/>.
    /// Dispose zamknie klienta.
    /// </summary>
    public HobiaryLogSink(string logDir,
                          uint maxSegmentBytes = 0,
                          IFormatProvider? formatProvider = null)
    {
        _client = new HobiaryLogClient(logDir, maxSegmentBytes);
        _ownsClient = true;
        _formatProvider = formatProvider;
    }

    /* ── ILogEventSink ────────────────────────────────────────────────────── */

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        HlogLevel level = MapLevel(logEvent.Level);

        /* Rendered message → payload UTF-8 */
        string rendered = logEvent.RenderMessage(_formatProvider);
        byte[]? payload = rendered.Length > 0
            ? Encoding.UTF8.GetBytes(rendered)
            : null;

        /* SourceContext → source */
        string? source = null;
        if (logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? scv))
            source = (scv as ScalarValue)?.Value as string;

        /* RequestId / EventId → service (opcjonalnie) */
        string? service = null;
        if (logEvent.Properties.TryGetValue("RequestId", out LogEventPropertyValue? ridv))
            service = (ridv as ScalarValue)?.Value?.ToString();
        else if (logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? eidv))
            service = (eidv as ScalarValue)?.Value?.ToString();

        /* Timestamp: Serilog używa DateTimeOffset → unix epoch µs */
        long ts = logEvent.Timestamp.ToUnixTimeMilliseconds() * 1000L;

        var rec = new LogRecord(level, ts, Source: source, Service: service, Payload: payload);
        _client.Append(rec);
    }

    /* ── IDisposable ─────────────────────────────────────────────────────── */

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }

    /* ── Helper ──────────────────────────────────────────────────────────── */

    private static HlogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => HlogLevel.Trace,
        LogEventLevel.Debug => HlogLevel.Debug,
        LogEventLevel.Information => HlogLevel.Info,
        LogEventLevel.Warning => HlogLevel.Warn,
        LogEventLevel.Error => HlogLevel.Error,
        LogEventLevel.Fatal => HlogLevel.Fatal,
        _ => HlogLevel.Info,
    };
}

/* ─── Fluent API ──────────────────────────────────────────────────────────── */

/// <summary>
/// Rozszerzenia <see cref="LoggerSinkConfiguration"/> dla HobiaryLogDB.
/// </summary>
public static class HobiaryLoggerConfigurationExtensions
{
    /// <summary>
    /// Zapisuje zdarzenia do istniejącego <see cref="HobiaryLogClient"/>.
    /// </summary>
    public static LoggerConfiguration HobiaryLog(
        this LoggerSinkConfiguration sinkConfig,
        HobiaryLogClient client,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        IFormatProvider? formatProvider = null)
    {
        return sinkConfig.Sink(
            new HobiaryLogSink(client, formatProvider),
            restrictedToMinimumLevel);
    }

    /// <summary>
    /// Otwiera nowy <see cref="HobiaryLogClient"/> dla <paramref name="logDir"/>.
    /// Sink zarządza cyklem życia klienta.
    /// </summary>
    public static LoggerConfiguration HobiaryLog(
        this LoggerSinkConfiguration sinkConfig,
        string logDir,
        uint maxSegmentBytes = 0,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        IFormatProvider? formatProvider = null)
    {
        return sinkConfig.Sink(
            new HobiaryLogSink(logDir, maxSegmentBytes, formatProvider),
            restrictedToMinimumLevel);
    }
}
