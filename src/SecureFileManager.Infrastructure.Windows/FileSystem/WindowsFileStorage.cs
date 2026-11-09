using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed partial class WindowsFileStorage(PersonalStorage roots) : IPhysicalFileStorage
{
    public IFileWorkspace Open(Guid owner)
    {
        var root = roots.Resolve(owner, "");
        if (!root.Succeeded || root.PhysicalPath is null) throw new FileCommandException(FileStatus.Unavailable);
        return new Workspace(root.PhysicalPath, roots.StorageRoot);
    }
    private sealed partial class Workspace : IFileWorkspace
    {
        private readonly string _root, _storage, _internal;
        private readonly List<SafeFileHandle> _pins = [];
        private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SafeFileHandle> _objects = new(StringComparer.OrdinalIgnoreCase);
        private FileStream? _lock;
        public Workspace(string root, string storage)
        {
            _root = root; _storage = storage; _internal = Path.Combine(root, ".sfm-internal");
            try
            {
                PinDirectory(root);
                if (!Directory.Exists(_internal))
                {
                    Directory.CreateDirectory(_internal);
                    PinDirectory(_internal);
                    using var marker = new FileStream(Path.Combine(_internal, "owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    marker.Write(Encoding.UTF8.GetBytes("SecureFileManager-v1")); marker.Flush(true);
                }
                else
                {
                    PinDirectory(_internal);
                    using var marker = SafeNativeFile.Open(Path.Combine(_internal, "owner"), false);
                    var data = new byte[64]; var count = RandomAccess.Read(marker, data, 0);
                    if (Encoding.UTF8.GetString(data, 0, count) != "SecureFileManager-v1")
                        throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                var lockPath = Path.Combine(_internal, "lock");
                if (File.Exists(lockPath)) using (SafeNativeFile.Open(lockPath, false)) { }
                _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                File.SetAttributes(_internal, File.GetAttributes(_internal) | FileAttributes.Hidden);
            }
            catch { Dispose(); throw; }
        }
        private string Target(string path, bool root = false)
        {
            FileRules.Path(path, root);
            var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(_root, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new FileCommandException(FileStatus.Denied);
            return full;
        }
        private void PinDirectory(string path)
        {
            if (_pinned.Add(Path.GetFullPath(path)))
            {
                try { _pins.AddRange(SafeNativeFile.Pin(path, _objects)); }
                catch { _pinned.Remove(Path.GetFullPath(path)); throw; }
            }
        }
        private void ParentPin(string full) => PinDirectory(Path.GetDirectoryName(full)!);
        private SafeFileHandle Object(string full, bool directory, bool delete)
        {
            if (_objects.TryGetValue(full, out var existing) && !existing.IsClosed) return existing;
            var handle = SafeNativeFile.Open(full, directory, delete); _objects[full] = handle; return handle;
        }
        public IReadOnlyList<PhysicalEntry> List(string directory)
        {
            var full = Target(directory, true);
            PinDirectory(full);
            var rows = new List<PhysicalEntry>();
            foreach (var child in Directory.EnumerateFileSystemEntries(full))
            {
                var name = Path.GetFileName(child);
                if (name.Equals(".sfm-internal", StringComparison.OrdinalIgnoreCase)) continue;
                if (rows.Count >= FileRules.ListLimit) throw new FileCommandException(FileStatus.LimitExceeded);
                var relative = directory == "" ? name : directory + "/" + name;
                // Never follow an unsafe entry just to render a list.
                try { rows.Add(Inspect(relative)); }
                catch (FileCommandException ex) when (ex.Status is FileStatus.Denied or FileStatus.Invalid)
                { rows.Add(new(relative, name, FileKind.File, 0, DateTime.UnixEpoch, DateTime.UnixEpoch, false)); }
                catch (FileCommandException ex) when (ex.Status == FileStatus.NotFound) { }
            }
            return rows;
        }
        public PhysicalEntry Inspect(string path)
        {
            var full = Target(path); ParentPin(full);
            bool directory;
            try { directory = (File.GetAttributes(full) & FileAttributes.Directory) != 0; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { throw new FileCommandException(FileStatus.NotFound); }
            SafeNativeFile.Info info;
            if (_objects.TryGetValue(full, out var held) && !held.IsClosed) info = SafeNativeFile.Information(held);
            else { using var handle = SafeNativeFile.Open(full, directory); info = SafeNativeFile.Information(handle); }
            return new(path, Path.GetFileName(full), directory ? FileKind.Directory : FileKind.File,
                directory ? 0 : info.Length, info.CreatedUtc, info.ModifiedUtc);
        }
        public PhysicalText Read(string path)
        {
            var full = Target(path); ParentPin(full);
            using var handle = SafeNativeFile.Open(full, false);
            var info = SafeNativeFile.Information(handle);
            if (info.Length > FileRules.TextLimit) throw new FileCommandException(FileStatus.LimitExceeded);
            using var data = new MemoryStream();
            var buffer = new byte[65536]; long offset = 0; int count;
            while ((count = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                if (offset + count > FileRules.TextLimit) throw new FileCommandException(FileStatus.LimitExceeded);
                data.Write(buffer, 0, count); offset += count;
            }
            var bytes = data.ToArray();
            var skip = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
            if (text.Contains('\0')) throw new FileCommandException(FileStatus.Unsupported);
            return new(text, SafeNativeFile.Fingerprint(handle));
        }
        private string Transaction(Guid id) => Path.Combine(_internal, id.ToString("N"));
        private string Part(Guid id, string part) => Path.Combine(Transaction(id), part);
        public IReadOnlyList<FileJournal> Pending()
        {
            var items = new List<FileJournal>();
            foreach (var file in Directory.EnumerateFiles(_internal))
                if (Path.GetFileName(file) is not ("owner" or "lock")) throw new FileCommandException(FileStatus.RecoveryRequired);
            foreach (var path in Directory.EnumerateDirectories(_internal))
            {
                if (items.Count >= 250) throw new FileCommandException(FileStatus.RecoveryRequired);
                if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) throw new FileCommandException(FileStatus.RecoveryRequired);
                PinDirectory(path);
                SafeFileHandle manifest;
                try { manifest = SafeNativeFile.Open(Part(id, "plan.json"), false); }
                catch (FileCommandException) { throw new FileCommandException(FileStatus.RecoveryRequired); }
                using var handle = manifest;
                var info = SafeNativeFile.Information(handle);
                if (info.Length > 1024 * 1024) throw new FileCommandException(FileStatus.RecoveryRequired);
                var data = new byte[checked((int)info.Length)]; RandomAccess.Read(handle, data, 0);
                FileJournal? journal;
                try { journal = JsonSerializer.Deserialize<FileJournal>(data); }
                catch (JsonException) { throw new FileCommandException(FileStatus.RecoveryRequired); }
                if (journal is null || journal.Id != id || !Enum.IsDefined(journal.Kind))
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                FileRules.Path(journal.RelativePath); items.Add(journal);
                if (journal.Kind == ChangeKind.Extract && (!journal.IsDirectory || journal.Tree is null || journal.Tree.Count > 250))
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                foreach (var child in journal.Tree ?? [])
                { FileRules.Path(child.Path); if (string.IsNullOrEmpty(child.Version)) throw new FileCommandException(FileStatus.RecoveryRequired); }
            }
            return items;
        }
        public FileJournal Stage(Guid id, ChangeKind kind, string path, string? text = null, string? expected = null, ImportSelection? source = null)
            => StageCore(id, kind, path, text, expected, source, null, default);
        private FileJournal StageCore(Guid id, ChangeKind kind, string path, string? text, string? expected, ImportSelection? source,
            Action<IFilePreparation>? prepare, CancellationToken ct)
        {
            var full = Target(path); ParentPin(full);
            var folder = kind is ChangeKind.Directory or ChangeKind.Extract;
            SafeFileHandle? original = null;
            string? before = null;
            if (kind is ChangeKind.Save or ChangeKind.Delete)
            {
                folder = (File.GetAttributes(full) & FileAttributes.Directory) != 0;
                original = Object(full, folder, true);
                if (folder && Directory.EnumerateFileSystemEntries(full).Any()) throw new FileCommandException(FileStatus.Conflict);
                if (kind == ChangeKind.Save && SafeNativeFile.Information(original).Length > FileRules.TextLimit)
                    throw new FileCommandException(FileStatus.LimitExceeded);
                before = SafeNativeFile.Fingerprint(original);
                if (kind == ChangeKind.Save && (folder || expected != before)) throw new FileCommandException(FileStatus.Conflict);
            }
            else if (File.Exists(full) || Directory.Exists(full)) throw new FileCommandException(FileStatus.Conflict);
            Directory.CreateDirectory(Transaction(id));
            PinDirectory(Transaction(id));
            try
            {
                string? after = null;
                if (kind != ChangeKind.Delete)
                {
                    if (prepare is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (folder) Directory.CreateDirectory(Part(id, "new"));
                        using var area = new Preparation(Part(id, "new"), path, folder);
                        prepare(area);
                        ct.ThrowIfCancellationRequested();
                    }
                    else if (folder) Directory.CreateDirectory(Part(id, "new"));
                    else
                    {
                        using var output = new FileStream(Part(id, "new"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
                        if (kind == ChangeKind.Import)
                        {
                            if (source is null) throw new FileCommandException(FileStatus.Invalid);
                            CopySource(source.SourcePath, output);
                        }
                        else
                        {
                            var encoder = new UTF8Encoding(false, true);
                            var bom = new byte[3];
                            var preserveBom = original is not null && RandomAccess.Read(original, bom, 0) == 3 &&
                                bom.AsSpan().SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF });
                            if (text is { Length: > FileRules.TextLimit } || encoder.GetByteCount(text ?? "") + (preserveBom ? 3 : 0) > FileRules.TextLimit)
                                throw new FileCommandException(FileStatus.LimitExceeded);
                            if (preserveBom) output.Write(bom);
                            output.Write(encoder.GetBytes(text ?? ""));
                        }
                        output.Flush(true);
                    }
                    var next = Object(Part(id, "new"), folder, true);
                    after = SafeNativeFile.Fingerprint(next);
                }
                var journal = new FileJournal(id, kind, path, folder, before, after)
                { Tree = kind == ChangeKind.Extract ? CaptureTree(Part(id, "new")) : null };
                ReleaseChildren(Part(id, "new"));
                using var manifest = new FileStream(Part(id, "plan.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                manifest.Write(JsonSerializer.SerializeToUtf8Bytes(journal)); manifest.Flush(true);
                return journal;
            }
            catch (Exception error)
            {
                // Only artifacts created under this fresh transaction ID; no original is touched before Publish.
                try
                {
                    if (kind == ChangeKind.Extract && Directory.Exists(Part(id, "new"))) DeleteTree(Part(id, "new"), null);
                    else RemovePart(id, "new", folder, null);
                    RemovePart(id, "plan.json", false, null); RemoveTransaction(id);
                }
                catch (Exception cleanup) { throw new FileCommandException(FileStatus.RecoveryRequired, inner: new AggregateException(error, cleanup)); }
                throw;
            }
        }
        private void CopySource(string source, FileStream output)
        {
            if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal) ||
                source.IndexOf(':', 2) >= 0) throw new FileCommandException(FileStatus.Denied);
            var full = Path.GetFullPath(source);
            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) throw new FileCommandException(FileStatus.Denied);
            var relative = Path.GetRelativePath(_storage, full);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new FileCommandException(FileStatus.Denied); // source may not be another user's Storage
            var pins = SafeNativeFile.Pin(Path.GetDirectoryName(full)!);
            try
            {
                using var input = SafeNativeFile.Open(full, false);
                if (SafeNativeFile.Information(input).Length > FileRules.ImportLimit) throw new FileCommandException(FileStatus.LimitExceeded);
                var buffer = new byte[65536]; long offset = 0; int count;
                while ((count = RandomAccess.Read(input, buffer, offset)) > 0)
                {
                    if (offset + count > FileRules.ImportLimit) throw new FileCommandException(FileStatus.LimitExceeded);
                    output.Write(buffer, 0, count); offset += count;
                }
            }
            finally { foreach (var pin in pins) pin.Dispose(); }
        }
        public void Publish(FileJournal journal)
        {
            var full = Target(journal.RelativePath); ParentPin(full);
            if (journal.Kind is ChangeKind.Save or ChangeKind.Delete)
            {
                var original = Object(full, journal.IsDirectory, true);
                if (SafeNativeFile.Fingerprint(original) != journal.BeforeVersion) throw new FileCommandException(FileStatus.Conflict);
                SafeNativeFile.Rename(original, Part(journal.Id, "old"));
                _objects.Remove(full); _objects[Part(journal.Id, "old")] = original;
            }
            if (journal.Kind != ChangeKind.Delete)
            {
                var next = Object(Part(journal.Id, "new"), journal.IsDirectory, true);
                SafeNativeFile.Rename(next, full);
                _objects.Remove(Part(journal.Id, "new")); _objects[full] = next;
            }
        }
        public void Settle(FileJournal journal, bool committed)
        {
            if (journal.Kind == ChangeKind.Extract) { SettleTree(journal, committed); return; }
            var full = Target(journal.RelativePath); ParentPin(full);
            if (!committed)
            {
                if (journal.Kind != ChangeKind.Delete && (File.Exists(full) || Directory.Exists(full)))
                {
                    var published = Object(full, journal.IsDirectory, true);
                    var version = SafeNativeFile.Fingerprint(published);
                    if (version == journal.AfterVersion)
                    {
                        SafeNativeFile.Delete(published); published.Dispose(); _objects.Remove(full);
                    }
                    else if (version != journal.BeforeVersion) throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                var backup = Part(journal.Id, "old");
                if (File.Exists(backup) || Directory.Exists(backup))
                {
                    if (File.Exists(full) || Directory.Exists(full)) throw new FileCommandException(FileStatus.RecoveryRequired);
                    var old = Object(backup, journal.IsDirectory, true);
                    if (SafeNativeFile.Fingerprint(old) != journal.BeforeVersion) throw new FileCommandException(FileStatus.RecoveryRequired);
                    SafeNativeFile.Rename(old, full); _objects.Remove(backup); _objects[full] = old;
                }
                else if (journal.BeforeVersion is not null)
                {
                    if (!File.Exists(full) && !Directory.Exists(full)) throw new FileCommandException(FileStatus.RecoveryRequired);
                    if (SafeNativeFile.Fingerprint(Object(full, journal.IsDirectory, true)) != journal.BeforeVersion)
                        throw new FileCommandException(FileStatus.RecoveryRequired);
                }
            }
            else if (journal.Kind != ChangeKind.Delete)
            {
                if (SafeNativeFile.Fingerprint(Object(full, journal.IsDirectory, true)) != journal.AfterVersion)
                    throw new FileCommandException(FileStatus.RecoveryRequired);
            }
            RemovePart(journal.Id, "old", journal.IsDirectory, journal.BeforeVersion);
            RemovePart(journal.Id, "new", journal.IsDirectory, journal.AfterVersion);
            RemovePart(journal.Id, "plan.json", false, null);
            RemoveTransaction(journal.Id);
        }
        private void RemovePart(Guid id, string name, bool folder, string? expected)
        {
            var path = Part(id, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var handle = Object(path, folder, true);
            if (expected is not null && SafeNativeFile.Fingerprint(handle) != expected) throw new FileCommandException(FileStatus.RecoveryRequired);
            SafeNativeFile.Delete(handle); handle.Dispose(); _objects.Remove(path);
        }
        private void RemoveTransaction(Guid id)
        {
            // Release directory pins before deleting the now-empty private transaction directory.
            foreach (var pin in _pins) pin.Dispose(); _pins.Clear(); _pinned.Clear();
            PinDirectory(_root); PinDirectory(_internal);
            using var directory = SafeNativeFile.Open(Transaction(id), true, true);
            SafeNativeFile.Delete(directory);
        }
        public void Dispose()
        {
            foreach (var handle in _objects.Values) handle.Dispose(); _objects.Clear();
            _lock?.Dispose(); _lock = null;
            foreach (var pin in _pins) pin.Dispose(); _pins.Clear();
        }
    }
}
