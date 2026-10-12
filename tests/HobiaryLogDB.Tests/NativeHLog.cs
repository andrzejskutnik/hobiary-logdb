using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

/// <summary>
/// P/Invoke wrapper dla hobiarylogdb.dll (rdzeń C).
/// DLL musi być zbudowana CMake i skopiowana do katalogu wyjściowego.
/// </summary>
internal static class NativeHLog
{
    private const string Dll = "hobiarylogdb";

    /* ── Kody błędów ─────────────────────────────────────────────────────── */
    public const int HLOG_OK = 0;
    public const int HLOG_ERR_IO = -1;
    public const int HLOG_ERR_ARGS = -2;
    public const int HLOG_ERR_CORRUPT = -3;
    public const int HLOG_ERR_FULL = -4;
    public const int HLOG_ERR_SEALED = -5; public const int HLOG_ERR_NOT_FOUND = -6;
    /* ── Stałe ───────────────────────────────────────────────────────────── */
    public const uint HLOG_FILE_MAGIC = 0x484C4F47u;
    public const byte HLOG_RECORD_MAGIC = 0xA5;
    public const uint HLOG_FIRST_RECORD_OFFSET = 4160u;
    public const uint HLOG_MAX_PAYLOAD = 65536u;
    public const uint HLOG_MAX_SEGMENT_SIZE = 64u * 1024u * 1024u;

    /* ── Poziomy logowania ───────────────────────────────────────────────── */
    public const byte HLOG_LEVEL_TRACE = 0;
    public const byte HLOG_LEVEL_DEBUG = 1;
    public const byte HLOG_LEVEL_INFO = 2;
    public const byte HLOG_LEVEL_WARN = 3;
    public const byte HLOG_LEVEL_ERROR = 4;
    public const byte HLOG_LEVEL_FATAL = 5;

    /* ── Flagi rekordu ───────────────────────────────────────────────────── */
    public const ushort HLOG_RFLAG_PAYLOAD_JSON = 1 << 3;
    public const ushort HLOG_RFLAG_PAYLOAD_TRUNC = 1 << 4;

    /*
     * HlogRecordInput — struct wejściowy (nie packed w C, naturalne wyrównanie).
     * Musi mieć LayoutKind.Sequential bez Pack aby pasować do C struct.
     * trace_id / correlation_id: ByValArray = inline 16 bajtów.
     */
    [StructLayout(LayoutKind.Sequential)]
    public struct HlogRecordInput
    {
        public byte level;
        public ushort flags;
        public long timestamp;
        public long ingested_at;
        public IntPtr source;        /* const char* */
        public IntPtr service;
        public IntPtr operation;
        public IntPtr entity_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] trace_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] correlation_id;
        public ulong span_id;
        public IntPtr payload;       /* const uint8_t* */
        public uint payload_len;
    }

    /*
     * HlogRecordHeader — on-disk (packed, #pragma pack(1) w C).
     * Pack = 1 wymagany aby bajty pasowały.
     */
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HlogRecordHeader
    {
        public byte magic;
        public uint record_len;
        public byte level;
        public ushort flags;
        public uint payload_len;
        public long timestamp;
        public long ingested_at;
        public ushort source_id;
        public ushort service_id;
        public ushort operation_id;
        public ushort entity_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] trace_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] correlation_id;
        public ulong span_id;
        public uint _padding;
    }

    /* ── hlog_segment_create / open / close ──────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hlog_segment_create(string path, ulong seq);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hlog_segment_open(string path);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_close(IntPtr seg, int seal);

    /* ── hlog_segment_append ─────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_append(IntPtr seg, ref HlogRecordInput rec);

    /* ── hlog_segment_read_at ────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_read_at(IntPtr seg, uint offset,
        out HlogRecordHeader out_header, out IntPtr out_payload);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hlog_free(IntPtr ptr);

    /* ── Gettery ─────────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong hlog_segment_record_count(IntPtr seg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern long hlog_segment_first_ts(IntPtr seg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern long hlog_segment_last_ts(IntPtr seg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint hlog_segment_file_size(IntPtr seg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr hlog_segment_str(IntPtr seg, ushort id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_is_sealed(IntPtr seg);

    /* ── Etap 6: crash recovery ──────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_scan_valid(IntPtr seg,
        out uint out_end_offset, out long out_last_ts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_segment_truncate(IntPtr seg,
        uint valid_end_offset, ulong valid_count, long last_valid_ts);
}

/// <summary>P/Invoke wrapper dla fasady hlog (etap 3).</summary>
internal static class NativeHLogFacade
{
    private const string Dll = "hobiarylogdb";

    /* ── Visitor delegate ────────────────────────────────────────────────── */

    /// <summary>
    /// Callback wywoływany przez hlog_visit_range dla każdego rekordu.
    /// seg:     segment (do hlog_segment_str), hdr: nagłówek, payload: dane (tylko podczas wywołania).
    /// Zwróć 0 aby kontynuować, !=0 aby przerwać.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int HlogVisitFn(IntPtr seg,
                                     ref NativeHLog.HlogRecordHeader hdr,
                                     IntPtr payload,
                                     IntPtr ctx);

    /* ── hlog_open / hlog_close ──────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr hlog_open(string dir, uint maxSegmentBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_close(IntPtr log);

    /* ── hlog_append ─────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_append(IntPtr log, ref NativeHLog.HlogRecordInput rec);

    /* ── Gettery ─────────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint hlog_segment_count(IntPtr log);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong hlog_active_seq(IntPtr log);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong hlog_active_record_count(IntPtr log);

    /* ── hlog_visit_range ────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_visit_range(IntPtr log,
                                               long from_ts, long to_ts,
                                               HlogVisitFn fn, IntPtr ctx);

    /* ── hlog_purge_before ───────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_purge_before(IntPtr log, long cutoff_ts);

    /* ── Etap 5: visit_level / visit_source ──────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_visit_level(IntPtr log, byte level,
                                               HlogVisitFn fn, IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_visit_source(IntPtr log, ushort source_id,
                                                HlogVisitFn fn, IntPtr ctx);
}
