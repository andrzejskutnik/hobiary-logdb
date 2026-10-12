using System.Runtime.InteropServices;
using System.Text;

namespace HobiaryLogDB.Tests;

public class SegmentTests : IDisposable
{
    private readonly string _dir;

    public SegmentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hlog_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string SegPath(string name) => Path.Combine(_dir, name);

    /* ── Helpers ─────────────────────────────────────────────────────────── */

    private static NativeHLog.HlogRecordInput MakeInput(
        byte level = NativeHLog.HLOG_LEVEL_INFO,
        string? source = "test-service",
        string? message = null)
    {
        var rec = new NativeHLog.HlogRecordInput
        {
            level = level,
            flags = 0,
            timestamp = 0,
            ingested_at = 0,
            source = source != null ? Marshal.StringToHGlobalAnsi(source) : IntPtr.Zero,
            service = IntPtr.Zero,
            operation = IntPtr.Zero,
            entity_id = IntPtr.Zero,
            span_id = 0,
            payload = IntPtr.Zero,
            payload_len = 0,
            trace_id = new byte[16],
            correlation_id = new byte[16]
        };

        if (message != null)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            rec.payload = Marshal.AllocHGlobal(bytes.Length);
            rec.payload_len = (uint)bytes.Length;
            Marshal.Copy(bytes, 0, rec.payload, bytes.Length);
            rec.flags = NativeHLog.HLOG_RFLAG_PAYLOAD_JSON;
        }

        return rec;
    }

    private static void FreeInput(ref NativeHLog.HlogRecordInput rec)
    {
        if (rec.source != IntPtr.Zero) { Marshal.FreeHGlobal(rec.source); rec.source = IntPtr.Zero; }
        if (rec.service != IntPtr.Zero) { Marshal.FreeHGlobal(rec.service); rec.service = IntPtr.Zero; }
        if (rec.operation != IntPtr.Zero) { Marshal.FreeHGlobal(rec.operation); rec.operation = IntPtr.Zero; }
        if (rec.entity_id != IntPtr.Zero) { Marshal.FreeHGlobal(rec.entity_id); rec.entity_id = IntPtr.Zero; }
        if (rec.payload != IntPtr.Zero) { Marshal.FreeHGlobal(rec.payload); rec.payload = IntPtr.Zero; }
    }

    /* ── Testy ───────────────────────────────────────────────────────────── */

    [Fact]
    public void CanCreateSegment()
    {
        var path = SegPath("seg1.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);
        Assert.True(File.Exists(path));

        var fileSize = NativeHLog.hlog_segment_file_size(seg);
        Assert.Equal(NativeHLog.HLOG_FIRST_RECORD_OFFSET, fileSize);
        Assert.Equal(0UL, NativeHLog.hlog_segment_record_count(seg));

        NativeHLog.hlog_segment_close(seg, seal: 0);
    }

    [Fact]
    public void CanReopenCreatedSegment()
    {
        var path = SegPath("seg_reopen.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 42);
        Assert.NotEqual(IntPtr.Zero, seg);
        NativeHLog.hlog_segment_close(seg, seal: 0);

        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);
        Assert.Equal(0UL, NativeHLog.hlog_segment_record_count(seg2));
        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void CanAppendOneRecord()
    {
        var path = SegPath("seg_one.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        var rec = MakeInput();
        int rc = NativeHLog.hlog_segment_append(seg, ref rec);
        FreeInput(ref rec);

        Assert.Equal(NativeHLog.HLOG_OK, rc);
        Assert.Equal(1UL, NativeHLog.hlog_segment_record_count(seg));
        Assert.NotEqual(0L, NativeHLog.hlog_segment_first_ts(seg));
        Assert.NotEqual(0L, NativeHLog.hlog_segment_last_ts(seg));

        NativeHLog.hlog_segment_close(seg, seal: 0);
    }

    [Fact]
    public void CanAppendMultipleRecords()
    {
        var path = SegPath("seg_multi.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        for (int i = 0; i < 10; i++)
        {
            var rec = MakeInput(level: NativeHLog.HLOG_LEVEL_DEBUG);
            int rc = NativeHLog.hlog_segment_append(seg, ref rec);
            FreeInput(ref rec);
            Assert.Equal(NativeHLog.HLOG_OK, rc);
        }

        Assert.Equal(10UL, NativeHLog.hlog_segment_record_count(seg));
        NativeHLog.hlog_segment_close(seg, seal: 0);
    }

    [Fact]
    public void CanReadRecordBack()
    {
        var path = SegPath("seg_read.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        var rec = MakeInput(level: NativeHLog.HLOG_LEVEL_ERROR, message: "{\"msg\":\"test\"}");
        int rc = NativeHLog.hlog_segment_append(seg, ref rec);
        FreeInput(ref rec);
        Assert.Equal(NativeHLog.HLOG_OK, rc);

        int readRc = NativeHLog.hlog_segment_read_at(seg, NativeHLog.HLOG_FIRST_RECORD_OFFSET,
            out var hdr, out var payloadPtr);

        Assert.Equal(NativeHLog.HLOG_OK, readRc);
        Assert.Equal(NativeHLog.HLOG_RECORD_MAGIC, hdr.magic);
        Assert.Equal(NativeHLog.HLOG_LEVEL_ERROR, hdr.level);
        Assert.True(hdr.payload_len > 0);
        Assert.NotEqual(IntPtr.Zero, payloadPtr);

        var payloadBytes = new byte[hdr.payload_len];
        Marshal.Copy(payloadPtr, payloadBytes, 0, (int)hdr.payload_len);
        var payloadStr = Encoding.UTF8.GetString(payloadBytes);
        Assert.Contains("test", payloadStr);

        NativeHLog.hlog_free(payloadPtr);
        NativeHLog.hlog_segment_close(seg, seal: 0);
    }

    [Fact]
    public void CanInternStrings_SameStringGetsSameId()
    {
        var path = SegPath("seg_intern.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        var rec1 = MakeInput(source: "my-service");
        var rec2 = MakeInput(source: "my-service");
        NativeHLog.hlog_segment_append(seg, ref rec1);
        NativeHLog.hlog_segment_append(seg, ref rec2);
        FreeInput(ref rec1);
        FreeInput(ref rec2);

        /* Odczyt obu rekordów — muszą mieć ten sam source_id */
        NativeHLog.hlog_segment_read_at(seg, NativeHLog.HLOG_FIRST_RECORD_OFFSET,
            out var hdr1, out var p1);
        NativeHLog.hlog_free(p1);

        /* Drugi rekord: offset = HLOG_FIRST_RECORD_OFFSET + hdr1.record_len */
        uint offset2 = NativeHLog.HLOG_FIRST_RECORD_OFFSET + hdr1.record_len;
        NativeHLog.hlog_segment_read_at(seg, offset2, out var hdr2, out var p2);
        NativeHLog.hlog_free(p2);

        Assert.Equal(hdr1.source_id, hdr2.source_id);
        Assert.NotEqual(0, hdr1.source_id); /* ID 0 = "" */

        NativeHLog.hlog_segment_close(seg, seal: 0);
    }

    [Fact]
    public void StringTablePersistedAcrossReopen()
    {
        var path = SegPath("seg_strtable.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        var rec = MakeInput(source: "persistent-service");
        NativeHLog.hlog_segment_append(seg, ref rec);
        FreeInput(ref rec);

        NativeHLog.hlog_segment_read_at(seg, NativeHLog.HLOG_FIRST_RECORD_OFFSET,
            out var hdr, out var p1);
        NativeHLog.hlog_free(p1);
        ushort originalId = hdr.source_id;

        NativeHLog.hlog_segment_close(seg, seal: 0);

        /* Reopen */
        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);

        var strPtr = NativeHLog.hlog_segment_str(seg2, originalId);
        var strVal = Marshal.PtrToStringAnsi(strPtr);
        Assert.Equal("persistent-service", strVal);

        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void CanSealSegment()
    {
        var path = SegPath("seg_seal.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);
        Assert.Equal(0, NativeHLog.hlog_segment_is_sealed(seg));

        NativeHLog.hlog_segment_close(seg, seal: 1);

        /* Reopen i sprawdź flagę */
        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);
        Assert.Equal(1, NativeHLog.hlog_segment_is_sealed(seg2));
        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void AppendToSealedSegment_ReturnsError()
    {
        var path = SegPath("seg_sealed_write.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);
        NativeHLog.hlog_segment_close(seg, seal: 1);

        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);

        var rec = MakeInput();
        int rc = NativeHLog.hlog_segment_append(seg2, ref rec);
        FreeInput(ref rec);

        Assert.Equal(NativeHLog.HLOG_ERR_SEALED, rc);
        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void CorruptedRecord_ReadReturnsError()
    {
        var path = SegPath("seg_corrupt.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        var rec = MakeInput();
        NativeHLog.hlog_segment_append(seg, ref rec);
        FreeInput(ref rec);
        NativeHLog.hlog_segment_close(seg, seal: 0);

        /* Nadpisz jeden bajt w trace_id (offset nagłówka +36) — nie wpływa na
           rozmiar payloadu, ale zmienia CRC32 → HLOG_ERR_CORRUPT */
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(NativeHLog.HLOG_FIRST_RECORD_OFFSET + 36, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);

        int rc = NativeHLog.hlog_segment_read_at(seg2, NativeHLog.HLOG_FIRST_RECORD_OFFSET,
            out _, out var p);
        NativeHLog.hlog_free(p);

        Assert.Equal(NativeHLog.HLOG_ERR_CORRUPT, rc);
        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void RecordCountAndTimestampsPersistedAcrossReopen()
    {
        var path = SegPath("seg_persist.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        for (int i = 0; i < 5; i++)
        {
            var rec = MakeInput();
            NativeHLog.hlog_segment_append(seg, ref rec);
            FreeInput(ref rec);
        }

        long firstTs = NativeHLog.hlog_segment_first_ts(seg);
        long lastTs = NativeHLog.hlog_segment_last_ts(seg);
        NativeHLog.hlog_segment_close(seg, seal: 0);

        var seg2 = NativeHLog.hlog_segment_open(path);
        Assert.NotEqual(IntPtr.Zero, seg2);
        Assert.Equal(5UL, NativeHLog.hlog_segment_record_count(seg2));
        Assert.Equal(firstTs, NativeHLog.hlog_segment_first_ts(seg2));
        Assert.Equal(lastTs, NativeHLog.hlog_segment_last_ts(seg2));
        NativeHLog.hlog_segment_close(seg2, seal: 0);
    }

    [Fact]
    public void FileSizeGrowsWithEachRecord()
    {
        var path = SegPath("seg_size.hlog");
        var seg = NativeHLog.hlog_segment_create(path, 1);
        Assert.NotEqual(IntPtr.Zero, seg);

        uint prev = NativeHLog.hlog_segment_file_size(seg);
        for (int i = 0; i < 3; i++)
        {
            var rec = MakeInput();
            NativeHLog.hlog_segment_append(seg, ref rec);
            FreeInput(ref rec);
            uint curr = NativeHLog.hlog_segment_file_size(seg);
            Assert.True(curr > prev);
            prev = curr;
        }

        NativeHLog.hlog_segment_close(seg, seal: 0);
    }
}
