using System.Buffers.Binary;
using System.Text;

namespace Astra.Api.Office;

/// <summary>
/// Read-only reader for the OLE compound file format ([MS-CFB]): the container
/// a VBA project (and a legacy .xls/.doc) is stored in. Enough to walk storages
/// and read streams — no writing, no property sets. Every chain walk is bounded
/// and cycle-checked, because the input is an untrusted upload.
/// </summary>
public sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint NoStream = 0xFFFFFFFF;
    private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public sealed class Entry
    {
        public required string Name { get; init; }
        /// <summary>1 storage, 2 stream, 5 root.</summary>
        public required byte Type { get; init; }
        internal uint Left, Right, Child, StartSector;
        public long Size { get; internal set; }
        public List<Entry> Children { get; } = new();
        public bool IsStorage => Type is 1 or 5;
    }

    private readonly byte[] _d;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly List<Entry> _entries;

    public Entry Root => _entries[0];

    public static bool LooksLikeCompoundFile(ReadOnlySpan<byte> data) =>
        data.Length >= 512 && data[..8].SequenceEqual(Signature);

    /// <exception cref="InvalidDataException">The bytes are not a readable compound file.</exception>
    public CompoundFile(byte[] data)
    {
        if (!LooksLikeCompoundFile(data)) throw new InvalidDataException("not an OLE compound file");
        _d = data;
        var sectorShift = U16(0x1E);
        if (sectorShift is not (9 or 12)) throw new InvalidDataException($"unsupported sector size 2^{sectorShift}");
        _sectorSize = 1 << sectorShift;
        var miniShift = U16(0x20);
        if (miniShift is < 1 or > 12) throw new InvalidDataException("bad mini sector size");
        _miniSectorSize = 1 << miniShift;
        _miniCutoff = U32(0x38);

        _fat = ReadFat(numFatSectors: U32(0x2C), firstDifat: U32(0x44), numDifat: U32(0x48));
        var dir = ReadChain(U32(0x30), _fat, _sectorSize, maxBytes: 64L << 20);
        _entries = ReadDirectory(dir);
        if (_entries.Count == 0 || _entries[0].Type != 5) throw new InvalidDataException("no root entry");

        _miniStream = ReadChain(Root.StartSector, _fat, _sectorSize, maxBytes: Root.Size);
        var miniFatBytes = U32(0x40) == 0 ? Array.Empty<byte>() : ReadChain(U32(0x3C), _fat, _sectorSize, maxBytes: 64L << 20);
        _miniFat = ToUInts(miniFatBytes);
        LinkTree();
    }

    /// <summary>Case-insensitive lookup of a storage or stream by path from the root.</summary>
    public Entry? Find(params string[] path)
    {
        var cur = Root;
        foreach (var part in path)
        {
            cur = cur.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cur is null) return null;
        }
        return cur;
    }

    public Entry? Child(Entry storage, string name) =>
        storage.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public byte[] ReadStream(Entry e)
    {
        if (e.Type != 2) throw new InvalidOperationException($"{e.Name} is not a stream");
        if (e.Size == 0) return Array.Empty<byte>();
        return e.Size < _miniCutoff
            ? ReadMiniChain(e.StartSector, e.Size)
            : ReadChain(e.StartSector, _fat, _sectorSize, e.Size);
    }

    // ── internals ───────────────────────────────────────────────────────

    private ushort U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(off, 2));
    private uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(off, 4));

    private static uint[] ToUInts(byte[] b)
    {
        var r = new uint[b.Length / 4];
        for (var i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4, 4));
        return r;
    }

    private ReadOnlySpan<byte> Sector(uint n)
    {
        var off = (long)(n + 1) * _sectorSize;
        if (off >= _d.Length) throw new InvalidDataException($"sector {n} is past the end of the file");
        var len = (int)Math.Min(_sectorSize, _d.Length - off);
        return _d.AsSpan((int)off, len);
    }

    private uint[] ReadFat(uint numFatSectors, uint firstDifat, uint numDifat)
    {
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109 && fatSectors.Count < numFatSectors; i++)
        {
            var s = U32(0x4C + i * 4);
            if (s >= 0xFFFFFFFA) break;
            fatSectors.Add(s);
        }
        var difat = firstDifat;
        var perSector = _sectorSize / 4 - 1;
        for (var guard = 0; difat < 0xFFFFFFFA && guard < numDifat + 1 && fatSectors.Count < numFatSectors; guard++)
        {
            var sec = Sector(difat);
            for (var i = 0; i < perSector && fatSectors.Count < numFatSectors; i++)
            {
                var s = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(i * 4, 4));
                if (s < 0xFFFFFFFA) fatSectors.Add(s);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(perSector * 4, 4));
        }
        var fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            var sec = Sector(fatSectors[i]);
            for (var j = 0; j < sec.Length / 4; j++)
                fat[i * (_sectorSize / 4) + j] = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(j * 4, 4));
        }
        return fat;
    }

    private byte[] ReadChain(uint start, uint[] fat, int sectorSize, long maxBytes)
    {
        using var ms = new MemoryStream();
        var seen = new HashSet<uint>();
        var cur = start;
        while (cur != EndOfChain && cur < 0xFFFFFFFA && ms.Length < maxBytes)
        {
            if (!seen.Add(cur)) throw new InvalidDataException("cycle in sector chain");
            var sec = Sector(cur);
            ms.Write(sec[..(int)Math.Min(sec.Length, maxBytes - ms.Length)]);
            if (cur >= fat.Length) break;
            cur = fat[cur];
        }
        return ms.ToArray();
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        var seen = new HashSet<uint>();
        var cur = start;
        while (cur != EndOfChain && cur < 0xFFFFFFFA && ms.Length < size)
        {
            if (!seen.Add(cur)) throw new InvalidDataException("cycle in mini sector chain");
            var off = (long)cur * _miniSectorSize;
            if (off >= _miniStream.Length) throw new InvalidDataException("mini sector past the mini stream");
            var len = (int)Math.Min(Math.Min(_miniSectorSize, _miniStream.Length - off), size - ms.Length);
            ms.Write(_miniStream, (int)off, len);
            if (cur >= _miniFat.Length) break;
            cur = _miniFat[cur];
        }
        return ms.ToArray();
    }

    private static List<Entry> ReadDirectory(byte[] dir)
    {
        var list = new List<Entry>();
        for (var off = 0; off + 128 <= dir.Length; off += 128)
        {
            var e = dir.AsSpan(off, 128);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(64, 2));
            var type = e[66];
            var name = nameLen >= 2 && nameLen <= 64 ? Encoding.Unicode.GetString(e[..(nameLen - 2)]) : "";
            list.Add(new Entry
            {
                Name = name,
                Type = type,
                Left = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(68, 4)),
                Right = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(72, 4)),
                Child = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(76, 4)),
                StartSector = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(116, 4)),
                // Version 3 files may leave garbage in the high half; a stream over 4 GB is not a VBA module.
                Size = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(120, 4)),
            });
        }
        return list;
    }

    private void LinkTree()
    {
        foreach (var storage in _entries.Where(x => x.IsStorage))
        {
            // The children of a storage form a red-black tree hung off Child; walk it fully.
            var stack = new Stack<uint>();
            var seen = new HashSet<uint>();
            if (storage.Child != NoStream) stack.Push(storage.Child);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id >= _entries.Count || !seen.Add(id)) continue;
                var c = _entries[(int)id];
                if (c.Type is 1 or 2) storage.Children.Add(c);
                if (c.Left != NoStream) stack.Push(c.Left);
                if (c.Right != NoStream) stack.Push(c.Right);
            }
        }
    }
}
