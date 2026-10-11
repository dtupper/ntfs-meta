using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static System.Buffers.Binary.BinaryPrimitives;
using static Mft;

delegate void RecordHandler(int index, Span<byte> record);

/// One parsed MFT file record. Base != 0 marks an extension record belonging to record Base.
record struct MftRecord(bool InUse, bool Dir, int Base, bool HasList, string? Name, int Parent,
                        bool HasData, long Size, long Alloc, long Modified, long Accessed);

/// Read-only access to an NTFS volume: raw $MFT scan, single-record lookup, USN journal.
sealed class Volume
{
    const uint QueryJournalCode = 0x000900F4, ReadJournalCode = 0x000900BB, GetRecordCode = 0x00090068;

    public readonly string Drive;
    public readonly int RecordSize;
    public readonly long RecordCount;
    readonly long cluster;
    readonly List<(long Lcn, long Len)> runs;
    readonly SafeFileHandle h;
    SafeFileHandle? journal; // separate handle: blocking journal reads would serialize other I/O

    public Volume(string drive)
    {
        Drive = drive;
        h = Open();
        var boot = new byte[4096];
        RandomAccess.Read(h, boot, 0);
        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") throw new InvalidOperationException($"{drive}: is not NTFS");

        int spc = boot[0x0D];
        cluster = (long)U16(boot, 0x0B) * (spc <= 0x80 ? spc : 1 << (256 - spc));
        sbyte c = (sbyte)boot[0x40];
        RecordSize = c > 0 ? (int)(c * cluster) : 1 << -c;

        // Record 0 is $MFT itself; its $DATA runs tell us where every other record lives.
        var buf = new byte[Math.Max(cluster, RecordSize)];
        RandomAccess.Read(h, buf, I64(boot, 0x30) * cluster);
        var rec = buf.AsSpan(0, RecordSize);
        if (!Mft.Fixup(rec)) throw new InvalidOperationException("Unreadable $MFT record");
        (runs, long bytes) = Mft.MftDataRuns(rec);
        if (runs.Sum(r => r.Len) * cluster < bytes) throw new InvalidOperationException("$MFT is too fragmented (needs an attribute list); not supported");
        RecordCount = bytes / RecordSize;
    }

    SafeFileHandle Open() => File.OpenHandle($@"\\.\{Drive}:", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// Streams every MFT record straight off the disk in large sequential reads.
    public void Scan(RecordHandler f)
    {
        var buf = new byte[4 << 20];
        long idx = 0;
        foreach (var (lcn, len) in runs)
        {
            long bytes = len * cluster;
            if (lcn < 0) { idx += bytes / RecordSize; continue; }
            for (long pos = 0; pos < bytes && idx < RecordCount; pos += buf.Length)
            {
                int want = (int)Math.Min(buf.Length, bytes - pos);
                int n = RandomAccess.Read(h, buf.AsSpan(0, want), lcn * cluster + pos);
                if (n != want) throw new IOException("Short read from $MFT");
                for (int o = 0; o + RecordSize <= n && idx < RecordCount; o += RecordSize)
                    f((int)idx++, buf.AsSpan(o, RecordSize));
            }
        }
    }

    /// Current record via the file system (cache-coherent, unlike raw reads). Null if not in use; throws on I/O errors.
    public byte[]? ReadRecord(long frn)
    {
        var o = new byte[12 + RecordSize];
        if (!DeviceIoControl(h, GetRecordCode, BitConverter.GetBytes(frn), 8, o, o.Length, out _, 0))
            return Marshal.GetLastWin32Error() is 2 or 87 ? null : throw new Win32Exception();
        // NTFS returns the nearest in-use record at or below frn, so a mismatch means frn was deleted.
        return (I64(o, 0) & M48) == frn ? o.AsSpan(12, (int)Math.Min(U32(o, 8), (uint)RecordSize)).ToArray() : null;
    }

    public (long Id, long Next) QueryJournal()
    {
        var o = new byte[80];
        Ioctl(h, QueryJournalCode, null, o);
        return (I64(o, 0), I64(o, 16));
    }

    /// Blocks until the journal has entries past usn; returns the record numbers that changed.
    public HashSet<long> ReadJournal(long id, ref long usn)
    {
        journal ??= Open();
        var i = new byte[40];
        WriteInt64LittleEndian(i, usn);
        WriteUInt32LittleEndian(i.AsSpan(8), 0xFFFFFFFF); // all reasons
        WriteInt64LittleEndian(i.AsSpan(24), 1);          // wait for at least one byte
        WriteInt64LittleEndian(i.AsSpan(32), id);
        var o = new byte[1 << 16];
        int n = Ioctl(journal, ReadJournalCode, i, o);
        usn = I64(o, 0);
        var changed = new HashSet<long>();
        for (int p = 8; p + 16 <= n;)
        {
            int len = (int)U32(o, p);
            if (len <= 0) break;
            if (U16(o, p + 4) == 2) changed.Add(I64(o, p + 8) & M48);
            p += len;
        }
        return changed;
    }

    static int Ioctl(SafeFileHandle h, uint code, byte[]? i, byte[] o) =>
        DeviceIoControl(h, code, i, i?.Length ?? 0, o, o.Length, out int n, 0) ? n : throw new Win32Exception();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inLen, byte[] outBuf, int outLen, out int returned, nint overlapped);

}

/// Parsing of raw MFT file records.
static class Mft
{
    internal const long M48 = 0xFFFF_FFFF_FFFF;
    const uint StandardInfo = 0x10, AttrList = 0x20, FileName = 0x30, Data = 0x80, End = 0xFFFFFFFF;

    /// Validates the FILE signature and undoes the update-sequence protection on each 512-byte stride.
    public static bool Fixup(Span<byte> r)
    {
        if (r.Length < 48 || U32(r, 0) != 0x454C4946) return false; // "FILE"
        int usa = U16(r, 4), n = U16(r, 6);
        if (usa + 2 * n > r.Length) return false;
        for (int i = 1; i < n && i * 512 <= r.Length; i++)
        {
            int p = i * 512 - 2;
            if (r[p] == r[usa] && r[p + 1] == r[usa + 1]) { r[p] = r[usa + 2 * i]; r[p + 1] = r[usa + 2 * i + 1]; }
        }
        return true;
    }

    public static bool Parse(Span<byte> r, out MftRecord rec)
    {
        rec = default;
        if (!Fixup(r)) return false;
        int flags = U16(r, 0x16), used = Math.Min((int)U32(r, 0x18), r.Length), parent = 0, ns = -1;
        string? name = null;
        bool hasData = false, hasList = false;
        long size = 0, alloc = 0, modified = 0, accessed = 0;

        for (int off = U16(r, 0x14); off + 16 <= used;)
        {
            uint type = U32(r, off);
            int len = (int)U32(r, off + 4);
            if (type == End || len < 24 || len > used - off) break;
            var a = r.Slice(off, len);
            off += len;

            bool resident = a[8] == 0;
            Span<byte> v = default;
            if (resident)
            {
                int vo = U16(a, 0x14), vl = (int)U32(a, 0x10);
                if (vl < 0 || vo > len - vl) continue;
                v = a.Slice(vo, vl);
            }

            switch (type)
            {
                case StandardInfo when v.Length >= 0x20:
                    modified = I64(v, 0x08);
                    accessed = I64(v, 0x18);
                    break;
                case AttrList:
                    hasList = true;
                    break;
                case FileName when v.Length >= 0x42 && 0x42 + v[0x40] * 2 <= v.Length:
                    // Prefer the long (Win32/POSIX) name over the 8.3 DOS alias (namespace 2).
                    if (name == null || (ns == 2 && v[0x41] != 2))
                    {
                        name = Encoding.Unicode.GetString(v.Slice(0x42, v[0x40] * 2));
                        ns = v[0x41];
                        parent = (int)Math.Min(I64(v, 0) & M48, int.MaxValue);
                    }
                    break;
                case Data when a[9] == 0: // unnamed stream only; ADS are ignored
                    if (resident) { hasData = true; size = alloc = v.Length; }
                    else if (len >= 0x40 && I64(a, 0x10) == 0) // first extent carries the sizes
                    {
                        hasData = true;
                        size = Math.Max(0, I64(a, 0x30));
                        // Compressed/sparse attributes carry the true on-disk size at 0x40.
                        alloc = Math.Max(0, (U16(a, 0x0C) & 0x8001) != 0 && len >= 0x48 ? I64(a, 0x40) : I64(a, 0x28));
                    }
                    break;
            }
        }
        rec = new((flags & 1) != 0, (flags & 2) != 0, (int)Math.Min(I64(r, 0x20) & M48, int.MaxValue), hasList, name, parent,
                  hasData, size, alloc, modified, accessed);
        return true;
    }

    /// Extents (in clusters) and byte length of the unnamed $DATA attribute in a fixed-up record.
    public static (List<(long Lcn, long Len)> Runs, long Bytes) MftDataRuns(Span<byte> r)
    {
        for (int off = U16(r, 0x14); off + 16 <= r.Length;)
        {
            uint type = U32(r, off);
            int len = (int)U32(r, off + 4);
            if (type == End || len < 24 || len > r.Length - off) break;
            var a = r.Slice(off, len);
            off += len;
            if (type != Data || a[8] == 0 || a[9] != 0) continue;
            if (a.Length < 0x40) throw new InvalidOperationException("Corrupt $MFT $DATA attribute");

            var runs = new List<(long, long)>();
            long lcn = 0;
            for (int p = U16(a, 0x20); p < a.Length && a[p] != 0;)
            {
                int ls = a[p] & 0xF, os = a[p] >> 4;
                if (ls > 8 || os > 8 || p + 1 + ls + os > a.Length) throw new InvalidOperationException("Corrupt $MFT run list");
                p++;
                long count = Le(a.Slice(p, ls), false);
                p += ls;
                if (os == 0) { runs.Add((-1, count)); continue; } // sparse
                lcn += Le(a.Slice(p, os), true);
                p += os;
                runs.Add((lcn, count));
            }
            return (runs, I64(a, 0x30));
        }
        throw new InvalidOperationException("$MFT has no $DATA attribute");
    }

    static long Le(ReadOnlySpan<byte> b, bool signed)
    {
        long v = 0;
        for (int i = 0; i < b.Length; i++) v |= (long)b[i] << (8 * i);
        if (signed && b.Length is > 0 and < 8 && (b[^1] & 0x80) != 0) v |= -1L << (8 * b.Length);
        return v;
    }

    internal static ushort U16(ReadOnlySpan<byte> b, int o) => ReadUInt16LittleEndian(b[o..]);
    internal static uint U32(ReadOnlySpan<byte> b, int o) => ReadUInt32LittleEndian(b[o..]);
    internal static long I64(ReadOnlySpan<byte> b, int o) => ReadInt64LittleEndian(b[o..]);
}
