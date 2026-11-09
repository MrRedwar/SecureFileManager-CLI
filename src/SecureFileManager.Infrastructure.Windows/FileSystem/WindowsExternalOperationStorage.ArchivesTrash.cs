using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Security;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed partial class WindowsExternalOperationStorage
{
    private sealed partial class Workspace
    {
        private static bool Renames(ExternalChange kind) => kind is ExternalChange.Move or ExternalChange.Delete or ExternalChange.Restore;
        private string ReceiptPath(Guid id)
        {
            var area = Path.Combine(_personal.StorageRoot, ".external-trash");
            Directory.CreateDirectory(area); Pin(area);
            return Path.Combine(area, id.ToString("N") + ".json");
        }
        private static string TrashPath(string original, Guid id) => Path.Combine(Path.GetDirectoryName(original)!, ".sfm-trash-" + id.ToString("N"));
        private void ValidatePaths(ExternalOperationPlan plan)
        {
            if (plan.Kind == ExternalChange.Delete)
            {
                var source = _paths.Normalize(plan.Source ?? "");
                if (plan.Target != TrashPath(source, plan.Id) || plan.Stage is not null || plan.TrashId != plan.Id)
                    throw new FileCommandException(FileStatus.RecoveryRequired);
            }
            else if (plan.Kind == ExternalChange.Restore)
            {
                _paths.Normalize(plan.Target);
                if (plan.TrashId is not { } id || plan.Source != TrashPath(plan.Target, id) || plan.Stage is not null)
                    throw new FileCommandException(FileStatus.RecoveryRequired);
            }
            else
            {
                _paths.Normalize(plan.Target);
                if (plan.Source is not null) _paths.Normalize(plan.Source);
            }
        }
        private ExternalOperationPlan ReadReceipt(string path)
        {
            using var handle = SafeNativeFile.Open(path, false);
            var length = SafeNativeFile.Information(handle).Length;
            if (length is <= 0 or > 1024 * 1024) throw new FileCommandException(FileStatus.RecoveryRequired);
            using var stream = new NativeReadStream(handle);
            ExternalOperationPlan? plan;
            try { plan = JsonSerializer.Deserialize<ExternalOperationPlan>(stream); }
            catch (JsonException) { throw new FileCommandException(FileStatus.RecoveryRequired); }
            if (plan is null || plan.Kind != ExternalChange.Delete || plan.Id == Guid.Empty ||
                path != ReceiptPath(plan.Id) || plan.Entries is not { Count: > 0 and <= ExternalOperationRules.Nodes } ||
                plan.Entries[0].RelativePath != "" || plan.Entries.Any(x => x.RelativePath != "" && !x.RelativePath.Split('\\').All(RelativePathSyntax.IsNameValid)))
                throw new FileCommandException(FileStatus.RecoveryRequired);
            ValidatePaths(plan); return plan;
        }
        private void SaveReceipt(ExternalOperationPlan plan)
        {
            var path = ReceiptPath(plan.Id);
            if (Exists(path))
            {
                if (JsonSerializer.Serialize(ReadReceipt(path)) != JsonSerializer.Serialize(plan)) throw new FileCommandException(FileStatus.RecoveryRequired);
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            output.Write(bytes); output.Flush(true);
        }
        private void RemoveReceipt(ExternalOperationPlan plan)
        {
            var path = ReceiptPath(plan.TrashId!.Value);
            if (!Exists(path)) return; // idempotent committed recovery
            var receipt = ReadReceipt(path);
            if (receipt.Source != plan.Target || receipt.Target != plan.Source) throw new FileCommandException(FileStatus.RecoveryRequired);
            using var handle = SafeNativeFile.Open(path, false, delete: true); SafeNativeFile.Delete(handle);
        }
        private ExternalOperationPlan PrepareTrash(ExternalChangeRequest request, CancellationToken ct)
        {
            var original = _paths.Normalize(request.Source ?? "");
            if (original.Equals(Path.GetPathRoot(original), StringComparison.OrdinalIgnoreCase) ||
                _personal.StorageRoot.StartsWith(original + "\\", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Denied);
            Pin(Path.GetDirectoryName(original)!);
            var id = Guid.NewGuid(); Guid? trashId = id; string source = original, target = TrashPath(original, id);
            ExternalOperationPlan? receipt = null;
            if (request.Kind == ExternalChange.Restore)
            {
                var area = Path.GetDirectoryName(ReceiptPath(id))!;
                var paths = Directory.EnumerateFileSystemEntries(area).Take(1001).ToArray();
                if (paths.Length > 1000) throw new FileCommandException(FileStatus.LimitExceeded);
                var matches = paths.Select(ReadReceipt).Where(x => x.Source!.Equals(original, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) throw new FileCommandException(FileStatus.NotFound);
                if (matches.Length != 1) throw new FileCommandException(FileStatus.Conflict);
                receipt = matches[0]; source = receipt.Target; target = receipt.Source!; trashId = receipt.Id;
            }
            FreeName(target);
            var directory = (File.GetAttributes(source) & FileAttributes.Directory) != 0;
            _source = SafeNativeFile.Open(source, directory, delete: true);
            var rows = Snapshot(source, ct, request.Kind == ExternalChange.Restore, _source);
            if (receipt is not null) Match(receipt.Entries, rows);
            var plan = new ExternalOperationPlan(id, request.Kind, source, target, null, rows, trashId);
            Persist(plan); return plan;
        }
        public ArchivePreview Preview(string path, CancellationToken ct)
        {
            path = _paths.Normalize(path); Pin(Path.GetDirectoryName(path)!); NoEditorPlan(path); ct.ThrowIfCancellationRequested();
            using var handle = SafeNativeFile.Open(path, false);
            if (SafeNativeFile.Information(handle).Length > ZipLimits.ArchiveBytes) throw new FileCommandException(FileStatus.ArchiveLimit);
            using var input = new NativeReadStream(handle);
            return ZipEngine.Inspect(input, new(), SafeNativeFile.Fingerprint(handle));
        }
        private void GenerateZip(ExternalChange kind, string source, string stage, IReadOnlyList<ExternalOperationEntry> original,
            Action<string, string> directoryNew, Action<string, string, Action<FileStream>> fileNew,
            Dictionary<string, ExternalOperationEntry> owned, CancellationToken ct)
        {
            if (kind == ExternalChange.CreateZip)
            {
                var name = Path.GetFileName(source);
                var sources = original.Select(x => new ArchiveSource(x.RelativePath, x.RelativePath == "" ? name : name + "/" + x.RelativePath.Replace('\\', '/'), x.Directory)).ToArray();
                fileNew(stage, "", output => ZipEngine.Create(output, sources, relative =>
                {
                    var entry = original.Single(x => x.RelativePath == relative);
                    var handle = relative == "" ? _source! : SafeNativeFile.Open(Path.Combine(source, relative), false);
                    try
                    {
                        if (SafeNativeFile.Fingerprint(handle) != entry.Version) throw new FileCommandException(FileStatus.Conflict);
                        return new(new NativeReadStream(handle, relative != ""), entry.Version);
                    }
                    catch { if (relative != "") handle.Dispose(); throw; }
                }, new(), null, ct));
            }
            else
            {
                if (original[0].Directory) throw new FileCommandException(FileStatus.ArchiveInvalid);
                using var input = new NativeReadStream(_source!);
                // Complete structural and declared-budget validation BEFORE creating staging.
                var preview = ZipEngine.Inspect(input, new(), original[0].Version);
                if (!preview.CanExtract) throw new FileCommandException(preview.Warning != "" || preview.Entries.Any(x => x.Warning == new FileResult<bool>(FileStatus.ArchiveLimit).Message) ? FileStatus.ArchiveLimit : FileStatus.ArchiveUnsafe);
                directoryNew(stage, "");
                ZipEngine.Extract(input, new ZipPreparation(stage, directoryNew, owned), new(), null, ct);
            }
        }
        private sealed class ZipPreparation(string stage, Action<string, string> directoryNew, Dictionary<string, ExternalOperationEntry> owned) : IFilePreparation
        {
            private static string Relative(string path) { FileRules.Path(path); return path.Replace('/', '\\'); }
            public void CreateDirectory(string path)
            { var relative = Relative(path); directoryNew(Path.Combine(stage, relative), relative); }
            public Stream CreateFile(string path)
            {
                var relative = Relative(path); var full = Path.Combine(stage, relative);
                return new OwnedOutput(full, relative, owned);
            }
        }
        private sealed class OwnedOutput : FileStream
        {
            private readonly string _path, _relative, _identity;
            private readonly Dictionary<string, ExternalOperationEntry> _owned;
            private bool _closed;
            public OwnedOutput(string path, string relative, Dictionary<string, ExternalOperationEntry> owned)
                : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough)
            { _path = path; _relative = relative; _owned = owned; _identity = SafeNativeFile.Information(SafeFileHandle).Identity; }
            protected override void Dispose(bool disposing)
            {
                if (_closed) return; _closed = true;
                try { if (disposing) Flush(true); }
                finally
                {
                    base.Dispose(disposing);
                    if (disposing)
                    {
                        using var handle = SafeNativeFile.Open(_path, false); var info = SafeNativeFile.Information(handle);
                        if (info.Identity != _identity) throw new FileCommandException(FileStatus.RecoveryRequired);
                        _owned[_relative] = new(_relative, false, SafeNativeFile.Fingerprint(handle), info.Length, info.ModifiedUtc);
                    }
                }
            }
        }
        private sealed class NativeReadStream(SafeFileHandle handle, bool owns = false) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => SafeNativeFile.Information(handle).Length;
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer) { var read = RandomAccess.Read(handle, buffer, Position); Position += read; return read; }
            public override long Seek(long offset, SeekOrigin origin)
            { var value = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset; if (value < 0) throw new IOException(); return Position = value; }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing && owns) handle.Dispose(); base.Dispose(disposing); }
        }
    }
}
