using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Tests;

public sealed class ZipEngineTests
{
    internal static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
            foreach (var item in entries)
            {
                var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var output = entry.Open(); output.Write(Encoding.UTF8.GetBytes(item.Text));
            }
        return data.ToArray();
    }
    internal static int Header(byte[] data, uint signature, int start = 0)
    {
        for (var i = start; i <= data.Length - 4; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == signature) return i;
        throw new InvalidOperationException("Test header not found.");
    }
    internal static void Number(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
    internal sealed class MemoryPreparation : IFilePreparation
    {
        public Dictionary<string, MemoryStream> Files { get; } = [];
        public HashSet<string> Directories { get; } = [];
        public Stream CreateFile(string path) { var stream = new MemoryStream(); Files.Add(path, stream); return stream; }
        public void CreateDirectory(string path) => Directories.Add(path);
    }
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("//server/share/x")]
    [InlineData("a\\../outside.txt")]
    [InlineData("a/..\\outside.txt")]
    [InlineData("a\\normal.txt")]
    [InlineData("a/../../b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a:ads")]
    [InlineData("NUL.txt")]
    [InlineData("con .txt")]
    [InlineData("COM¹.txt")]
    [InlineData("folder/end.")]
    [InlineData("folder/end ")]
    [InlineData(".sfm-internal/x")]
    public void DangerousWindowsPathsAreRejectedBeforeExtraction(string name)
    {
        using var data = new MemoryStream(Zip((name, "x")));
        var preview = ZipEngine.Inspect(data, new(), "test");
        Assert.False(preview.CanExtract); Assert.NotEmpty(Assert.Single(preview.Entries).Warning);
        var target = new MemoryPreparation();
        Assert.Throws<FileCommandException>(() => ZipEngine.Extract(data, target, new(), null, default));
        Assert.Empty(target.Files); Assert.Empty(target.Directories);
    }
    [Theory]
    [InlineData("same.txt", "same.txt")]
    [InlineData("same.txt", "SAME.TXT")]
    [InlineData("folder", "folder/a.txt")]
    [InlineData("folder/a.txt", "FOLDER")]
    public void DuplicateAndFileDirectoryCollisionsAreRejected(string first, string second)
    {
        using var data = new MemoryStream(Zip((first, "1"), (second, "2")));
        Assert.False(ZipEngine.Inspect(data, new(), "").CanExtract);
    }
    [Fact]
    public void PreviewDoesNotExtractAndDeflateRoundTripPreservesStructureAndEmptyFolders()
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("empty/");
            var entry = zip.CreateEntry("folder/nested/текст.txt", CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write("Тестовый текст");
        }
        var preview = ZipEngine.Inspect(data, new(), "v");
        Assert.True(preview.CanExtract); Assert.Equal(1, preview.Files); Assert.Equal(1, preview.Directories);
        var target = new MemoryPreparation(); Assert.Empty(target.Files);
        ZipEngine.Extract(data, target, new(), null, default);
        Assert.Equal("Тестовый текст", Encoding.UTF8.GetString(target.Files["folder/nested/текст.txt"].ToArray()));
        Assert.Contains("empty", target.Directories); Assert.Contains("folder/nested", target.Directories);
    }
    [Fact]
    public void SymlinkAndReparseAttributesAreRejected()
    {
        foreach (var attrs in new[] { unchecked((int)0xA1FF0000), 0x400, unchecked((int)0x61FF0000) })
        {
            var bytes = Zip(("link", "target")); var central = Header(bytes, 0x02014b50); Number(bytes, central + 38, (uint)attrs);
            using var data = new MemoryStream(bytes); Assert.False(ZipEngine.Inspect(data, new(), "").CanExtract);
        }
    }
    [Fact]
    public void EnvelopeBoundsEntryCountBeforeZipArchiveAllocation()
    {
        using var data = new MemoryStream(Zip(Enumerable.Range(0, 251).Select(i => ($"f{i}", "")).ToArray()));
        Assert.Equal(FileStatus.ArchiveLimit, Assert.Throws<FileCommandException>(() => ZipEngine.Inspect(data, new(), "")).Status);
    }
    [Fact]
    public void DeclaredEntryTotalAndRatioLimitsAreChecked()
    {
        using var one = new MemoryStream(Zip(("one", "12345")));
        Assert.False(ZipEngine.Inspect(one, new(EntryBytes: 4), "").CanExtract);
        using var total = new MemoryStream(Zip(("one", "123"), ("two", "456")));
        Assert.False(ZipEngine.Inspect(total, new(TotalBytes: 5), "").CanExtract);
        using var compressed = new MemoryStream();
        using (var zip = new ZipArchive(compressed, ZipArchiveMode.Create, true))
        { using var output = zip.CreateEntry("zeroes", CompressionLevel.SmallestSize).Open(); output.Write(new byte[20000]); }
        Assert.False(ZipEngine.Inspect(compressed, new(), "").CanExtract);
        var bytes = Zip(("zero", "")); var central = Header(bytes, 0x02014b50); var local = Header(bytes, 0x04034b50);
        Number(bytes, central + 24, 1); Number(bytes, local + 22, 1);
        using var zero = new MemoryStream(bytes); Assert.False(ZipEngine.Inspect(zero, new(), "").CanExtract);
    }
    [Fact]
    public void StreamingGuardsActualEntryTotalRatioAndDeclaredBytesBeforeWriting()
    {
        foreach (var item in new[] { (new ZipLimits(EntryBytes: 4), 10L, 10L), (new ZipLimits(TotalBytes: 4), 10L, 10L),
            (new ZipLimits(Ratio: 1), 10L, 1L), (new ZipLimits(), 10L, 0L), (new ZipLimits(), 1L, 10L) })
        {
            using var source = new MemoryStream(new byte[10]); using var output = new MemoryStream(); long total = 0;
            Assert.Throws<FileCommandException>(() => ZipEngine.CopyChecked(source, output, item.Item2, item.Item3, 0, item.Item1, ref total, default));
            Assert.Equal(0, output.Length);
        }
    }
    [Fact]
    public void CrcAndTruncatedOrForgedDataAreRejected()
    {
        var bytes = Zip(("x.txt", "original")); var local = Header(bytes, 0x04034b50); bytes[local + 30 + 5] ^= 1;
        using var data = new MemoryStream(bytes); var target = new MemoryPreparation();
        Assert.Equal(FileStatus.ArchiveInvalid, Assert.Throws<FileCommandException>(() => ZipEngine.Extract(data, target, new(), null, default)).Status);
        using var shortData = new MemoryStream(bytes[..^5]);
        Assert.Throws<FileCommandException>(() => ZipEngine.CheckEnvelope(shortData, new()));
        bytes = Zip(("x.txt", "original")); var central = Header(bytes, 0x02014b50); Number(bytes, central + 24, 1);
        using var forged = new MemoryStream(bytes); Assert.Throws<FileCommandException>(() => ZipEngine.CheckEnvelope(forged, new()));
    }
    [Theory]
    [InlineData(1, FileStatus.ArchiveEncrypted)]
    [InlineData(2, FileStatus.ArchiveUnsupported)]
    public void EncryptedAndUnsupportedCompressionAreDistinct(int mode, FileStatus expected)
    {
        var bytes = Zip(("x", "data")); var central = Header(bytes, 0x02014b50); var local = Header(bytes, 0x04034b50);
        if (mode == 1) { bytes[central + 8] |= 1; bytes[local + 6] |= 1; }
        else { bytes[central + 10] = 99; bytes[local + 8] = 99; }
        using var data = new MemoryStream(bytes);
        Assert.Equal(expected, Assert.Throws<FileCommandException>(() => ZipEngine.Inspect(data, new(), "")).Status);
    }
    [Fact]
    public void Zip64AndMalformedNonZipAreRejected()
    {
        var bytes = Zip(("x", "data")); Number(bytes, Header(bytes, 0x06054b50) + 12, uint.MaxValue);
        using var data = new MemoryStream(bytes);
        Assert.Equal(FileStatus.ArchiveUnsupported, Assert.Throws<FileCommandException>(() => ZipEngine.CheckEnvelope(data, new())).Status);
        using var other = new MemoryStream(Encoding.ASCII.GetBytes("This is not a valid ZIP file at all."));
        Assert.Equal(FileStatus.ArchiveInvalid, Assert.Throws<FileCommandException>(() => ZipEngine.CheckEnvelope(other, new())).Status);
    }
}
