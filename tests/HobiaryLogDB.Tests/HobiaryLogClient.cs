using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

/*
 * Etap 7 — zarządzany wrapper C# nad natywnym silnikiem HobiaryLogDB.
 * Eliminuje surowy P/Invoke z kodu użytkownika — zamiast IntPtr / ref-structów
 * eksponuje idiomatyczne C# (IDisposable, rekordy, Func<,>, List<T>).
 *
 * Klasy publiczne w tej przestrzeni nazw:
 *   HlogLevel         — enum poziomów logowania
 *   LogRecord         — rekord logu (sealed record)
 *   HobiaryLogClient  — główny klient logu (IDisposable)
 */

/* ─── Poziom logowania ────────────────────────────────────────────────────── */

public enum HlogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

/* ─── Rekord (dane jednego wpisu) ─────────────────────────────────────────── */

/// <param name="Level">Poziom logowania.</param>
/// <param name="Timestamp">Unix epoch [µs].</param>
/// <param name="Source">Nazwa źródła (null = brak).</param>
/// <param name="Service">Nazwa serwisu (null = brak).</param>
/// <param name="Payload">Surowy payload (null = brak).</param>
public sealed record LogRecord(
    HlogLevel Level,
    long Timestamp,
    string? Source = null,
    string? Service = null,
    byte[]? Payload = null);

/* ─── Klient ──────────────────────────────────────────────────────────────── */

/// <summary>
/// Bezpieczny wrapper nad <c>Hlog*</c>.
/// Zarządza czasem życia uchwytu natywnego; thread-unsafe (jak silnik C).
/// </summary>
public sealed class HobiaryLogClient : IDisposable
{
    private IntPtr _log;
    private bool _disposed;

    /* Przechowujemy delegate jako pole — zapobiega GC podczas natywnego wywołania. */
    private readonly NativeHLogFacade.HlogVisitFn _visitFn;
    private Func<LogRecord, bool>? _activeCallback;

    /* ── Konstruktor / destruktor ─────────────────────────────────────────── */

    /// <param name="dir">Katalog z plikami segmentów (.hlog).</param>
    /// <param name="maxSegmentBytes">0 = domyślny limit (64 MB).</param>
    public HobiaryLogClient(string dir, uint maxSegmentBytes = 0)
    {
        _log = NativeHLogFacade.hlog_open(dir, maxSegmentBytes);
        if (_log == IntPtr.Zero)
            throw new InvalidOperationException($"Nie można otworzyć logu: {dir}");
        _visitFn = VisitCallback;
    }

    /* ── Właściwości ─────────────────────────────────────────────────────── */

    public uint SegmentCount => NativeHLogFacade.hlog_segment_count(_log);
    public ulong ActiveSeq => NativeHLogFacade.hlog_active_seq(_log);
    public ulong ActiveRecordCount => NativeHLogFacade.hlog_active_record_count(_log);

    /* ── Zapis ───────────────────────────────────────────────────────────── */

    /// <summary>
    /// Dołącza rekord do aktywnego segmentu.
    /// Zwraca HLOG_OK (0) lub kod błędu.
    /// </summary>
    public int Append(LogRecord rec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr srcPtr = rec.Source != null ? Marshal.StringToHGlobalAnsi(rec.Source) : IntPtr.Zero;
        IntPtr svcPtr = rec.Service != null ? Marshal.StringToHGlobalAnsi(rec.Service) : IntPtr.Zero;
        IntPtr payPtr = IntPtr.Zero;

        try
        {
            uint payLen = 0;
            if (rec.Payload is { Length: > 0 })
            {
                payLen = (uint)rec.Payload.Length;
                payPtr = Marshal.AllocHGlobal((int)payLen);
                Marshal.Copy(rec.Payload, 0, payPtr, (int)payLen);
            }

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

            var input = new NativeHLog.HlogRecordInput
            {
                level = (byte)rec.Level,
                flags = 0,
                timestamp = rec.Timestamp,
                ingested_at = nowUs,
                source = srcPtr,
                service = svcPtr,
                operation = IntPtr.Zero,
                entity_id = IntPtr.Zero,
                span_id = 0,
                trace_id = new byte[16],
                correlation_id = new byte[16],
                payload = payPtr,
                payload_len = payLen,
            };

            return NativeHLogFacade.hlog_append(_log, ref input);
        }
        finally
        {
            if (srcPtr != IntPtr.Zero) Marshal.FreeHGlobal(srcPtr);
            if (svcPtr != IntPtr.Zero) Marshal.FreeHGlobal(svcPtr);
            if (payPtr != IntPtr.Zero) Marshal.FreeHGlobal(payPtr);
        }
    }

    /* ── Odczyt ──────────────────────────────────────────────────────────── */

    /// <summary>
    /// Odwiedza rekordy z zakresu czasu [fromTs, toTs] (Unix µs, włącznie).
    /// <paramref name="callback"/> zwraca <c>true</c> = kontynuuj,
    /// <c>false</c> = przerwij iterację.
    /// Zwraca łączną liczbę odwiedzonychrekordów (lub kod błędu &lt; 0).
    /// </summary>
    public int VisitRange(long fromTs, long toTs, Func<LogRecord, bool> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeCallback = callback;
        try
        {
            int rc = NativeHLogFacade.hlog_visit_range(_log, fromTs, toTs, _visitFn, IntPtr.Zero);
            GC.KeepAlive(_visitFn);
            return rc;
        }
        finally { _activeCallback = null; }
    }

    /// <summary>Odwiedza wszystkie rekordy o danym <paramref name="level"/>.</summary>
    public int VisitLevel(HlogLevel level, Func<LogRecord, bool> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeCallback = callback;
        try
        {
            int rc = NativeHLogFacade.hlog_visit_level(_log, (byte)level, _visitFn, IntPtr.Zero);
            GC.KeepAlive(_visitFn);
            return rc;
        }
        finally { _activeCallback = null; }
    }

    /// <summary>Odwiedza rekordy o danym identyfikatorze źródła.</summary>
    public int VisitSource(ushort sourceId, Func<LogRecord, bool> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeCallback = callback;
        try
        {
            int rc = NativeHLogFacade.hlog_visit_source(_log, sourceId, _visitFn, IntPtr.Zero);
            GC.KeepAlive(_visitFn);
            return rc;
        }
        finally { _activeCallback = null; }
    }

    /// <summary>
    /// Wygodna metoda — zbiera rekordy z zakresu [fromTs, toTs] w listę.
    /// </summary>
    public List<LogRecord> ReadRange(long fromTs = long.MinValue, long toTs = long.MaxValue)
    {
        var list = new List<LogRecord>();
        VisitRange(fromTs, toTs, r => { list.Add(r); return true; });
        return list;
    }

    /* ── Konserwacja ─────────────────────────────────────────────────────── */

    /// <summary>
    /// Usuwa zapieczętowane segmenty, których last_ts &lt; cutoffTs.
    /// Zwraca liczbę usuniętych segmentów (lub kod błędu &lt; 0).
    /// </summary>
    public int PurgeBefore(long cutoffTs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeHLogFacade.hlog_purge_before(_log, cutoffTs);
    }

    /* ── IDisposable ─────────────────────────────────────────────────────── */

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_log != IntPtr.Zero)
        {
            NativeHLogFacade.hlog_close(_log);
            _log = IntPtr.Zero;
        }
    }

    /* ── Prywatny callback konwertujący rekord C → LogRecord ─────────────── */

    private int VisitCallback(IntPtr seg,
                               ref NativeHLog.HlogRecordHeader hdr,
                               IntPtr payload,
                               IntPtr ctx)
    {
        if (_activeCallback == null) return 0;

        string? source = null;
        if (hdr.source_id != 0)
        {
            IntPtr p = NativeHLog.hlog_segment_str(seg, hdr.source_id);
            if (p != IntPtr.Zero) source = Marshal.PtrToStringAnsi(p);
        }

        string? service = null;
        if (hdr.service_id != 0)
        {
            IntPtr p = NativeHLog.hlog_segment_str(seg, hdr.service_id);
            if (p != IntPtr.Zero) service = Marshal.PtrToStringAnsi(p);
        }

        byte[]? payloadBytes = null;
        if (payload != IntPtr.Zero && hdr.payload_len > 0)
        {
            payloadBytes = new byte[hdr.payload_len];
            Marshal.Copy(payload, payloadBytes, 0, (int)hdr.payload_len);
        }

        var rec = new LogRecord(
            (HlogLevel)hdr.level,
            hdr.timestamp,
            source,
            service,
            payloadBytes);

        return _activeCallback(rec) ? 0 : 1;
    }
}
