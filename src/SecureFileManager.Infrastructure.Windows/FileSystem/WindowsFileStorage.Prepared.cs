using System.IO;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed partial class WindowsFileStorage
{
    private sealed partial class Workspace
    {
        public BinaryDocument OpenBinary(string path, long limit)
        {
            var full = Target(path); ParentPin(full);
            var handle = SafeNativeFile.Open(full, false);
            try
            {
                if (SafeNativeFile.Information(handle).Length > limit) throw new FileCommandException(FileStatus.ArchiveLimit);
                var version = SafeNativeFile.Fingerprint(handle);
                var stream = new FileStream(handle, FileAccess.Read, 65536, false);
                stream.Position = 0;
                return new(stream, version);
            }
            catch { handle.Dispose(); throw; }
        }
        public FileJournal StageGenerated(Guid id, string path, bool directory, Action<IFilePreparation> prepare, CancellationToken ct)
            => StageCore(id, directory ? ChangeKind.Extract : ChangeKind.Archive, path, null, null, null, prepare, ct);

        private sealed class Preparation(string root, string destination, bool directory) : IFilePreparation, IDisposable
        {
            private readonly List<Stream> _outputs = [];
            private string Resolve(string path)
            {
                if (!directory)
                {
                    if (path != "") throw new FileCommandException(FileStatus.Invalid);
                    return root;
                }
                FileRules.Path(path);
                foreach (var part in path.Split('/')) FileRules.Child("", part);
                FileRules.Path(destination + "/" + path);
                var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(root, full);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new FileCommandException(FileStatus.Denied);
                return full;
            }
            public void CreateDirectory(string path)
            {
                var full = Resolve(path); var pins = SafeNativeFile.Pin(Path.GetDirectoryName(full)!);
                try
                {
                    if (File.Exists(full) || Directory.Exists(full)) throw new FileCommandException(FileStatus.Conflict);
                    Directory.CreateDirectory(full);
                    using var checkedDirectory = SafeNativeFile.Open(full, true);
                }
                finally { foreach (var pin in pins) pin.Dispose(); }
            }
            public Stream CreateFile(string path)
            {
                var full = Resolve(path); var pins = SafeNativeFile.Pin(Path.GetDirectoryName(full)!);
                try
                {
                    var file = new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough);
                    var result = new PreparedStream(file, pins); _outputs.Add(result); return result;
                }
                catch { foreach (var pin in pins) pin.Dispose(); throw; }
            }
            public void Dispose() { foreach (var stream in _outputs) stream.Dispose(); }
        }
        private sealed class PreparedStream(FileStream stream, List<SafeFileHandle> pins) : Stream
        {
            private bool _disposed;
            public override bool CanRead => stream.CanRead;
            public override bool CanSeek => stream.CanSeek;
            public override bool CanWrite => stream.CanWrite;
            public override long Length => stream.Length;
            public override long Position { get => stream.Position; set => stream.Position = value; }
            public override void Flush() => stream.Flush(true);
            public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
            public override void SetLength(long value) => stream.SetLength(value);
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_disposed)
                {
                    _disposed = true;
                    try { stream.Flush(true); }
                    finally { stream.Dispose(); foreach (var pin in pins) pin.Dispose(); }
                }
                base.Dispose(disposing);
            }
        }
        // Holds every checked child handle until verification finishes; never follows reparse points.
        private IReadOnlyList<PreparedEntry> CaptureTree(string root)
        {
            var result = new List<PreparedEntry>(); var queue = new Queue<string>(); queue.Enqueue("");
            _ = Object(root, true, true);
            while (queue.TryDequeue(out var parent))
            {
                var fullParent = parent == "" ? root : Path.Combine(root, parent.Replace('/', Path.DirectorySeparatorChar));
                foreach (var child in Directory.EnumerateFileSystemEntries(fullParent))
                {
                    var relative = parent == "" ? Path.GetFileName(child) : parent + "/" + Path.GetFileName(child);
                    FileRules.Path(relative); FileRules.Child(parent, Path.GetFileName(child));
                    if (result.Count >= 250) throw new FileCommandException(FileStatus.ArchiveLimit);
                    var directory = (File.GetAttributes(child) & FileAttributes.Directory) != 0;
                    var handle = Object(child, directory, true);
                    if (!directory && SafeNativeFile.Information(handle).Length > 25L * 1024 * 1024)
                        throw new FileCommandException(FileStatus.ArchiveLimit);
                    result.Add(new(relative, directory, SafeNativeFile.Fingerprint(handle)));
                    if (directory) queue.Enqueue(relative);
                }
            }
            return result.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        }
        private void ReleaseChildren(string root)
        {
            foreach (var key in _objects.Keys.Where(k => k.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            { _objects[key].Dispose(); _objects.Remove(key); }
        }
        private void VerifyTree(string root, FileJournal journal)
        {
            if (SafeNativeFile.Fingerprint(Object(root, true, true)) != journal.AfterVersion || journal.Tree is null ||
                !CaptureTree(root).SequenceEqual(journal.Tree)) throw new FileCommandException(FileStatus.RecoveryRequired);
        }
        private void DeleteTree(string root, FileJournal? journal)
        {
            if (journal is not null) VerifyTree(root, journal);
            var entries = CaptureTree(root);
            // First verify everything, then delete through the held handles, deepest entries first.
            foreach (var entry in entries.OrderByDescending(e => e.Path.Count(c => c == '/')).ThenByDescending(e => e.Path.Length))
            {
                var full = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var handle = _objects[full]; SafeNativeFile.Delete(handle); handle.Dispose(); _objects.Remove(full);
            }
            var top = Object(root, true, true); SafeNativeFile.Delete(top); top.Dispose(); _objects.Remove(root);
        }
        private void SettleTree(FileJournal journal, bool committed)
        {
            // Metadata inspection may have pinned children. Replace those read-only pins
            // with the verified tree handles before requesting DELETE access for recovery.
            foreach (var pin in _pins) pin.Dispose(); _pins.Clear(); _pinned.Clear();
            PinDirectory(_root); PinDirectory(_internal);
            var full = Target(journal.RelativePath); ParentPin(full); var prepared = Part(journal.Id, "new");
            if (committed) { VerifyTree(full, journal); ReleaseChildren(full); }
            else if (Directory.Exists(full))
            {
                // An unrelated competing destination is never removed.
                if (SafeNativeFile.Fingerprint(Object(full, true, true)) == journal.AfterVersion) DeleteTree(full, journal);
                else if (!Directory.Exists(prepared)) throw new FileCommandException(FileStatus.RecoveryRequired);
            }
            if (Directory.Exists(prepared)) DeleteTree(prepared, journal);
            RemovePart(journal.Id, "plan.json", false, null); RemoveTransaction(journal.Id);
        }
    }
}
