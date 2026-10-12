using System.Runtime.InteropServices;

namespace HobiaryLogDB.Tests;

internal static class NativeHLogIndex
{
    private const string Dll = "hobiarylogdb";

    /* ── Stałe ───────────────────────────────────────────────────────────── */
    public const uint HLOG_IDX_TIME_MAGIC = 0x48494454u;
    public const uint HLOG_IDX_ERROR_MAGIC = 0x48494445u;
    public const uint HLOG_IDX_LEVEL_MAGIC = 0x4849444Cu; /* etap 5 */
    public const uint HLOG_IDX_SRC_MAGIC = 0x48494453u; /* etap 5 */
    public const uint HLOG_IDX_STRIDE = 64u;
    public const uint HLOG_IDX_HEADER_SIZE = 32u;

    /* ── HlogIndexEntry (on-disk, packed, 12B) ───────────────────────────── */
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HlogIndexEntry
    {
        public long timestamp;
        public uint offset;
    }

    /* ── Etap 5: rozszerzone struktury wpisów (on-disk, packed, 16B) ─────── */

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HlogLevelIndexEntry
    {
        public long timestamp;
        public uint offset;
        public byte level;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] _pad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HlogSrcIndexEntry
    {
        public long timestamp;
        public uint offset;
        public ushort source_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] _pad;
    }

    /* ── Visitor delegates (etap 5) ─────────────────────────────────────── */

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int HlogLevelVisitFn(long ts, uint offset, byte level, IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int HlogSrcVisitFn(long ts, uint offset, ushort source_id, IntPtr ctx);

    /* ── create / open / close ───────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hlog_index_create(string path, uint magic, ulong segSeq);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hlog_index_open(string path, uint magic);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_index_close(IntPtr idx);

    /* ── append ──────────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_index_append(IntPtr idx, long timestamp, uint offset);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_level_index_append(IntPtr idx, long ts,
        uint offset, byte level);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_src_index_append(IntPtr idx, long ts,
        uint offset, ushort source_id);

    /* ── read ────────────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong hlog_index_entry_count(IntPtr idx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_index_read_at(IntPtr idx, ulong pos,
        out HlogIndexEntry out_entry);

    /* ── find ────────────────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_index_find_ge(IntPtr idx, long ts,
        out uint out_offset, out long out_ts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_index_find_range(IntPtr idx,
        long from_ts, long to_ts,
        [Out] HlogIndexEntry[] out_entries, int max_entries);

    /* ── scan (etap 5) ───────────────────────────────────────────────────── */

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_level_index_scan(IntPtr idx, byte level,
        HlogLevelVisitFn fn, IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hlog_src_index_scan(IntPtr idx, ushort source_id,
        HlogSrcVisitFn fn, IntPtr ctx);
}
