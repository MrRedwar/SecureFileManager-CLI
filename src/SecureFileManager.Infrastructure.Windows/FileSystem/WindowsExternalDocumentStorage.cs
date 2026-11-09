using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

// In-place writing preserves the original Windows ACL and identity. A durable backup
// and plan precede any write; SQL uncertainty preserves both versions for recovery.
public sealed class WindowsExternalDocumentStorage(PersonalStorage personal) : IExternalDocumentStorage
{
    public IExternalDocumentFile Open(string path, bool writable, bool recovery = false) => new Document(personal, path, writable, recovery);
    private sealed record Plan(Guid Correlation, string Path, string Identity, string BeforeHash, string AfterHash, DateTime BeforeModifiedUtc);
    private sealed class Document : IExternalDocumentFile
    {
        private readonly List<SafeFileHandle> _pins = [];
        private readonly SafeFileHandle _file;
        private readonly FileStream _lock;
        private readonly string _area;
        private readonly bool _writable;
        private Plan? _plan;
        private bool _writeAttempted;
        public string Path { get; }
        public Guid? Pending => _plan?.Correlation;
        private string Part(string name) => System.IO.Path.Combine(_area, name);
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public Document(PersonalStorage personal, string path, bool writable, bool recovery)
        {
            Path = new WindowsComputerDirectoryReader(personal).Normalize(path);
            DocumentValidator.Format(Path);
            _writable = writable;
            try
            {
                _pins.AddRange(SafeNativeFile.Pin(System.IO.Path.GetDirectoryName(Path)!));
                _file = SafeNativeFile.Open(Path, false, write: writable);
                if (writable && (SafeNativeFile.Information(_file).Attributes & 1) != 0) throw new FileCommandException(FileStatus.Denied);
                _pins.AddRange(SafeNativeFile.Pin(personal.StorageRoot));
                var operations = System.IO.Path.Combine(personal.StorageRoot, ".external-operations");
                if (Directory.Exists(operations))
                {
                    _pins.AddRange(SafeNativeFile.Pin(operations));
                    if (Directory.EnumerateDirectories(operations).Any() || Directory.EnumerateFiles(operations).Any(x => System.IO.Path.GetFileName(x) != "lock"))
                        throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                var root = System.IO.Path.Combine(personal.StorageRoot, ".external-editor");
                Directory.CreateDirectory(root); _pins.AddRange(SafeNativeFile.Pin(root));
                _area = System.IO.Path.Combine(root, Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Path.ToUpperInvariant()))));
                Directory.CreateDirectory(_area); _pins.AddRange(SafeNativeFile.Pin(_area));
                if (File.Exists(Part("lock"))) using (SafeNativeFile.Open(Part("lock"), false)) { }
                _lock = new FileStream(Part("lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var files = Directory.EnumerateFiles(_area).Select(System.IO.Path.GetFileName).ToHashSet();
                if (Directory.EnumerateDirectories(_area).Any() || files.Any(x => x is not ("lock" or "before.bin" or "after.bin" or "plan.json")))
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                if (files.Contains("plan.json"))
                {
                    using var manifest = SafeNativeFile.Open(Part("plan.json"), false);
                    if (SafeNativeFile.Information(manifest).Length > 16384) throw new FileCommandException(FileStatus.RecoveryRequired);
                    try { _plan = JsonSerializer.Deserialize<Plan>(ReadBytes(manifest, 16384)); }
                    catch (JsonException) { throw new FileCommandException(FileStatus.RecoveryRequired); }
                    if (_plan is null || _plan.Correlation == Guid.Empty || _plan.BeforeModifiedUtc.Kind != DateTimeKind.Utc ||
                        !string.Equals(_plan.Path, Path, StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                if (!recovery && files.Count > 1 || recovery && files.Count > 1 && _plan is null)
                    throw new FileCommandException(FileStatus.RecoveryRequired);
            }
            catch { Dispose(); throw; }
        }
        private static byte[] ReadBytes(SafeFileHandle handle, int limit = FileRules.TextLimit)
        {
            if (SafeNativeFile.Information(handle).Length > limit) throw new FileCommandException(FileStatus.LimitExceeded);
            using var data = new MemoryStream(); var buffer = new byte[65536]; long offset = 0;
            int count;
            while ((count = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                if (offset + count > limit) throw new FileCommandException(FileStatus.LimitExceeded);
                data.Write(buffer, 0, count); offset += count;
            }
            return data.ToArray();
        }
        public ExternalText Read()
        {
            var bytes = ReadBytes(_file);
            var skip = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
            var content = Utf8.GetString(bytes, skip, bytes.Length - skip);
            if (content.Contains('\0')) throw new FileCommandException(FileStatus.Unsupported);
            var info = SafeNativeFile.Information(_file);
            return new(Path, content, SafeNativeFile.Fingerprint(_file), bytes.Length, info.ModifiedUtc);
        }
        private void WritePart(string name, byte[] bytes)
        {
            using var stream = new FileStream(Part(name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            stream.Write(bytes); stream.Flush(true);
        }
        public void Prepare(Guid correlation, string text, string expectedVersion)
        {
            if (!_writable || _plan is not null) throw new FileCommandException(FileStatus.RecoveryRequired);
            if (SafeNativeFile.Fingerprint(_file) != expectedVersion) throw new FileCommandException(FileStatus.Conflict);
            var before = ReadBytes(_file); var after = Utf8.GetBytes(text);
            if (before.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) after = new byte[] { 239, 187, 191 }.Concat(after).ToArray();
            if (after.Length > FileRules.TextLimit) throw new FileCommandException(FileStatus.LimitExceeded);
            var info = SafeNativeFile.Information(_file);
            var plan = new Plan(correlation, Path, info.Identity, Hash(before), Hash(after), info.ModifiedUtc);
            WritePart("before.bin", before); WritePart("after.bin", after);
            WritePart("plan.json", JsonSerializer.SerializeToUtf8Bytes(plan)); _plan = plan;
        }
        private byte[] VerifiedPart(string name, string hash)
        {
            using var handle = SafeNativeFile.Open(Part(name), false);
            var bytes = ReadBytes(handle);
            if (Hash(bytes) != hash) throw new FileCommandException(FileStatus.RecoveryRequired);
            return bytes;
        }
        private void WriteOriginal(byte[] bytes)
        {
            if (!_writable) throw new FileCommandException(FileStatus.Denied);
            RandomAccess.Write(_file, bytes, 0); RandomAccess.SetLength(_file, bytes.Length); RandomAccess.FlushToDisk(_file);
        }
        public void Publish()
        {
            if (_plan is null) throw new FileCommandException(FileStatus.RecoveryRequired);
            _writeAttempted = true;
            WriteOriginal(VerifiedPart("after.bin", _plan.AfterHash));
        }
        public void Settle(bool committed)
        {
            if (_plan is null) return;
            if (SafeNativeFile.Information(_file).Identity != _plan.Identity) throw new FileCommandException(FileStatus.RecoveryRequired);
            var currentHash = Hash(ReadBytes(_file));
            if (currentHash != _plan.BeforeHash && currentHash != _plan.AfterHash && !( !committed && _writeAttempted)) throw new FileCommandException(FileStatus.RecoveryRequired);
            // Verify both backups before modifying or removing any recovery data.
            var before = VerifiedPart("before.bin", _plan.BeforeHash); _ = VerifiedPart("after.bin", _plan.AfterHash);
            if (committed && currentHash != _plan.AfterHash) throw new FileCommandException(FileStatus.RecoveryRequired);
            if (!committed)
            {
                WriteOriginal(before);
                SafeNativeFile.SetModifiedUtc(_file, _plan.BeforeModifiedUtc);
                RandomAccess.FlushToDisk(_file);
            }
            foreach (var name in new[] { "before.bin", "after.bin", "plan.json" }) File.Delete(Part(name));
            _plan = null;
        }
        public void Dispose()
        {
            _file?.Dispose(); _lock?.Dispose(); foreach (var pin in _pins) pin.Dispose();
        }
    }
}
