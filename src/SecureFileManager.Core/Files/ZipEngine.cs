using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SecureFileManager.Core.Files;

// Stream-only ZIP processing. All disk access is supplied by the existing checked workspace.
public static class ZipEngine
{
    private static FileCommandException Reject(FileStatus status) => new(status);
    private static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static byte[] ReadAt(Stream stream, long at, int size)
    {
        if (at < 0 || size < 0 || at > stream.Length - size) throw Reject(FileStatus.ArchiveInvalid);
        stream.Position = at; var data = new byte[size]; stream.ReadExactly(data); return data;
    }
    // Bound entry allocation BEFORE ZipArchive builds its in-memory central directory.
    public static void CheckEnvelope(Stream stream, ZipLimits limits)
    {
        limits.Check();
        if (!stream.CanSeek || stream.Length < 22) throw Reject(FileStatus.ArchiveInvalid);
        if (stream.Length > ZipLimits.ArchiveBytes) throw Reject(FileStatus.ArchiveLimit);
        var tail = ReadAt(stream, Math.Max(0, stream.Length - 65557), (int)Math.Min(stream.Length, 65557));
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0) throw Reject(FileStatus.ArchiveInvalid);
        var count = U16(tail, end + 10); var centralSize = U32(tail, end + 12); var central = U32(tail, end + 16);
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != count ||
            count == ushort.MaxValue || centralSize == uint.MaxValue || central == uint.MaxValue)
            throw Reject(FileStatus.ArchiveUnsupported);
        if (count > limits.Entries || centralSize > 1024 * 1024) throw Reject(FileStatus.ArchiveLimit);
        var endOffset = stream.Length - tail.Length + end;
        if ((long)central + centralSize != endOffset) throw Reject(FileStatus.ArchiveInvalid);
        var cursor = (long)central; var ranges = new List<(long Start, long End)>();
        for (var index = 0; index < count; index++)
        {
            var header = ReadAt(stream, cursor, 46);
            if (U32(header, 0) != 0x02014b50) throw Reject(FileStatus.ArchiveInvalid);
            var flags = U16(header, 8); var method = U16(header, 10);
            if ((flags & 0x41) != 0) throw Reject(FileStatus.ArchiveEncrypted);
            if (method is not (0 or 8) || (flags & 0x20) != 0 || U16(header, 34) != 0) throw Reject(FileStatus.ArchiveUnsupported);
            var packed = U32(header, 20); var unpacked = U32(header, 24); var localAt = U32(header, 42);
            if (packed == uint.MaxValue || unpacked == uint.MaxValue || localAt == uint.MaxValue) throw Reject(FileStatus.ArchiveUnsupported);
            var nameSize = U16(header, 28); var extraSize = U16(header, 30); var commentSize = U16(header, 32);
            if (nameSize == 0 || nameSize > 1600) throw Reject(FileStatus.ArchiveUnsafe);
            var name = ReadAt(stream, cursor + 46, nameSize);
            CheckExtra(ReadAt(stream, cursor + 46 + nameSize, extraSize));
            cursor += 46L + nameSize + extraSize + commentSize;
            if (cursor > central + (long)centralSize) throw Reject(FileStatus.ArchiveInvalid);
            var local = ReadAt(stream, localAt, 30);
            if (U32(local, 0) != 0x04034b50 || U16(local, 6) != flags || U16(local, 8) != method || U16(local, 26) != nameSize ||
                !name.AsSpan().SequenceEqual(ReadAt(stream, localAt + 30L, nameSize))) throw Reject(FileStatus.ArchiveInvalid);
            var localExtra = U16(local, 28); CheckExtra(ReadAt(stream, localAt + 30L + nameSize, localExtra));
            var dataEnd = localAt + 30L + nameSize + localExtra + packed;
            if ((flags & 8) == 0)
            {
                if (U32(local, 14) != U32(header, 16) || U32(local, 18) != packed || U32(local, 22) != unpacked)
                    throw Reject(FileStatus.ArchiveInvalid);
            }
            else
            {
                var descriptor = ReadAt(stream, dataEnd, 12);
                var skip = U32(descriptor, 0) == 0x08074b50 ? 4 : 0;
                descriptor = ReadAt(stream, dataEnd + skip, 12);
                if (U32(descriptor, 0) != U32(header, 16) || U32(descriptor, 4) != packed || U32(descriptor, 8) != unpacked)
                    throw Reject(FileStatus.ArchiveInvalid);
                dataEnd += 12 + skip;
            }
            if (dataEnd > central || ranges.Any(r => localAt < r.End && dataEnd > r.Start)) throw Reject(FileStatus.ArchiveInvalid);
            ranges.Add((localAt, dataEnd));
        }
        if (cursor != central + (long)centralSize) throw Reject(FileStatus.ArchiveInvalid);
        stream.Position = 0;
    }
    private static void CheckExtra(byte[] extra)
    {
        var cursor = 0;
        while (cursor < extra.Length)
        {
            if (extra.Length - cursor < 4) throw Reject(FileStatus.ArchiveInvalid);
            var id = U16(extra, cursor); var size = U16(extra, cursor + 2); cursor += 4;
            if (id is 1 or 0x9901) throw Reject(id == 1 ? FileStatus.ArchiveUnsupported : FileStatus.ArchiveEncrypted);
            if (cursor + size > extra.Length) throw Reject(FileStatus.ArchiveInvalid);
            cursor += size;
        }
    }
    public static string EntryPath(string raw)
    {
        if (raw.Contains('\\') || raw.StartsWith('/') || raw.EndsWith("//", StringComparison.Ordinal)) throw Reject(FileStatus.ArchiveUnsafe);
        var path = raw.EndsWith('/') ? raw[..^1] : raw;
        try
        {
            FileRules.Path(path);
            foreach (var part in path.Split('/')) FileRules.Child("", part);
        }
        catch (FileCommandException) { throw Reject(FileStatus.ArchiveUnsafe); }
        return path;
    }
    private static string Display(string raw) => new(raw.Take(400).Select(c => char.IsControl(c) ||
        char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '�' : c).ToArray());
    public static ArchivePreview Inspect(Stream input, ZipLimits limits, string version)
    {
        CheckEnvelope(input, limits);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        var rows = new List<ArchiveItem>(); var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); long total = 0; var global = "";
        foreach (var entry in zip.Entries)
        {
            var warning = ""; var directory = entry.FullName.EndsWith('/');
            try
            {
                var path = EntryPath(entry.FullName); var mode = ((uint)entry.ExternalAttributes >> 16) & 0xF000;
                if (entry.IsEncrypted) throw Reject(FileStatus.ArchiveEncrypted);
                if (mode != 0 && mode != (directory ? 0x4000 : 0x8000) || (entry.ExternalAttributes & 0x440) != 0)
                    throw Reject(FileStatus.ArchiveUnsafe);
                if (!explicitPaths.Add(path) || nodes.TryGetValue(path, out var existingDirectory) && (!directory || !existingDirectory))
                    throw Reject(FileStatus.ArchiveUnsafe);
                nodes[path] = directory;
                for (var parent = FileRules.Parent(path); parent != ""; parent = FileRules.Parent(parent))
                {
                    if (nodes.TryGetValue(parent, out var isDirectory) && !isDirectory) throw Reject(FileStatus.ArchiveUnsafe);
                    nodes[parent] = true;
                }
                if (directory && entry.Length != 0) throw Reject(FileStatus.ArchiveUnsafe);
                if (entry.Length > limits.EntryBytes || entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length > entry.CompressedLength * (long)limits.Ratio))
                    throw Reject(FileStatus.ArchiveLimit);
                using var testHeader = entry.Open(); // no decompressed bytes read during preview
            }
            catch (FileCommandException error) { warning = new FileResult<bool>(error.Status).Message; }
            total += entry.Length;
            rows.Add(new(Display(entry.FullName), directory, entry.Length, entry.CompressedLength, warning));
        }
        if (total > limits.TotalBytes || nodes.Count > limits.Entries) global = new FileResult<bool>(FileStatus.ArchiveLimit).Message;
        return new(rows, version, global);
    }
    public static void Extract(Stream input, IFilePreparation target, ZipLimits limits,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var preview = Inspect(input, limits, "");
        if (!preview.CanExtract) throw Reject(preview.Warning != "" || preview.Entries.Any(e => e.Warning == new FileResult<bool>(FileStatus.ArchiveLimit).Message)
            ? FileStatus.ArchiveLimit : FileStatus.ArchiveUnsafe);
        input.Position = 0; using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = EntryPath(entry.FullName);
            if (entry.FullName.EndsWith('/')) folders.Add(path);
            for (var parent = FileRules.Parent(path); parent != ""; parent = FileRules.Parent(parent)) folders.Add(parent);
        }
        foreach (var folder in folders.OrderBy(x => x.Count(c => c == '/')).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        { ct.ThrowIfCancellationRequested(); target.CreateDirectory(folder); }
        long total = 0; var done = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith('/'))
            {
                using var source = entry.Open(); using var output = target.CreateFile(EntryPath(entry.FullName));
                CopyChecked(source, output, entry.Length, entry.CompressedLength, entry.Crc32, limits, ref total, ct);
            }
            progress?.Report(new(++done, zip.Entries.Count, total));
        }
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var value = (uint)i;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();
    public static long CopyChecked(Stream input, Stream output, long declared, long compressed, uint expectedCrc,
        ZipLimits limits, ref long total, CancellationToken ct)
    {
        var buffer = new byte[65536]; long bytes = 0; var crc = uint.MaxValue; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            ct.ThrowIfCancellationRequested(); bytes += count; total += count;
            if (bytes > limits.EntryBytes || total > limits.TotalBytes || compressed == 0 || bytes > compressed * (long)limits.Ratio)
                throw Reject(FileStatus.ArchiveLimit);
            if (bytes > declared) throw Reject(FileStatus.ArchiveInvalid);
            for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            output.Write(buffer, 0, count);
        }
        if (bytes != declared || ~crc != expectedCrc) throw Reject(FileStatus.ArchiveInvalid);
        return bytes;
    }
    public static void Create(Stream output, IReadOnlyList<ArchiveSource> sources, Func<string, BinaryDocument> open,
        ZipLimits limits, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        if (sources.Count > limits.Entries) throw Reject(FileStatus.ArchiveLimit);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
        long total = 0; var done = 0; var buffer = new byte[65536];
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested(); EntryPath(source.EntryName);
            // Store avoids producing an archive that our own anti-bomb ratio policy would reject.
            var entry = zip.CreateEntry(source.EntryName + (source.Directory ? "/" : ""), CompressionLevel.NoCompression);
            entry.ExternalAttributes = source.Directory ? 0x10 : 0;
            if (!source.Directory)
            {
                using var input = open(source.Path); using var destination = entry.Open();
                long bytes = 0; int count;
                while ((count = input.Stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); bytes += count; total += count;
                    if (bytes > limits.EntryBytes || total > limits.TotalBytes) throw Reject(FileStatus.ArchiveLimit);
                    destination.Write(buffer, 0, count);
                }
                if (bytes != input.Stream.Length) throw Reject(FileStatus.Conflict);
            }
            progress?.Report(new(++done, sources.Count, total));
        }
    }
}
