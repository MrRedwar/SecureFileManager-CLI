using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Security;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed partial class WindowsExternalOperationStorage(PersonalStorage personal) : IExternalOperationStorage
{
    public IExternalOperationWorkspace Open() => new Workspace(personal);
    private sealed partial class Workspace : IExternalOperationWorkspace
    {
        private readonly WindowsComputerDirectoryReader _paths;
        private readonly PersonalStorage _personal;
        private readonly List<SafeFileHandle> _pins = [];
        private readonly string _root;
        private readonly FileStream _lock;
        private SafeFileHandle? _source;
        private long _copied;
        public Workspace(PersonalStorage personal)
        {
            _personal = personal; _paths = new(personal);
            _root = Path.Combine(personal.StorageRoot, ".external-operations");
            try
            {
                Pin(personal.StorageRoot); Directory.CreateDirectory(_root); Pin(_root);
                if (File.Exists(Path.Combine(_root, "lock"))) using (SafeNativeFile.Open(Path.Combine(_root, "lock"), false)) { }
                _lock = new FileStream(Path.Combine(_root, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch { Dispose(); throw; }
        }
        private void Pin(string directory) => _pins.AddRange(SafeNativeFile.Pin(directory));
        private string PlanPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");
        private static bool Exists(string path)
        {
            try { _ = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
        private static void FreeName(string target)
        {
            var directory = Path.GetDirectoryName(target)!; var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > FileRules.ListLimit) throw new FileCommandException(FileStatus.LimitExceeded);
                if (Path.GetFileName(entry).Equals(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Conflict);
            }
        }
        private void NoEditorPlan(string path)
        {
            var area = Path.Combine(_personal.StorageRoot, ".external-editor", ExternalOperationRules.Key(path));
            if (Directory.Exists(area))
            {
                var pins = SafeNativeFile.Pin(area);
                try
                {
                    if (Directory.EnumerateDirectories(area).Any() || Directory.EnumerateFiles(area).Any(x => Path.GetFileName(x) != "lock"))
                        throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                finally { foreach (var pin in pins) pin.Dispose(); }
            }
        }
        private List<ExternalOperationEntry> Snapshot(string root, CancellationToken ct, bool internalStage = false, SafeFileHandle? held = null)
        {
            var result = new List<ExternalOperationEntry>(); long total = 0;
            void Walk(string path, string relative, SafeFileHandle? existing = null)
            {
                ct.ThrowIfCancellationRequested();
                if (!internalStage) { _paths.Normalize(path); NoEditorPlan(path); }
                if (result.Count >= ExternalOperationRules.Nodes) throw new FileCommandException(FileStatus.LimitExceeded);
                var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                var handle = existing ?? SafeNativeFile.Open(path, directory);
                try
                {
                    var info = SafeNativeFile.Information(handle);
                    if (!directory && (info.Length > ExternalOperationRules.FileBytes || (total += info.Length) > ExternalOperationRules.TotalBytes))
                        throw new FileCommandException(FileStatus.LimitExceeded);
                    result.Add(new(relative, directory, SafeNativeFile.Fingerprint(handle), directory ? 0 : info.Length, info.ModifiedUtc));
                    if (directory)
                    {
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var child in Directory.EnumerateFileSystemEntries(path))
                        {
                            var name = Path.GetFileName(child);
                            if (!RelativePathSyntax.IsNameValid(name) || name.StartsWith(".sfm-", StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                                throw new FileCommandException(FileStatus.Invalid);
                            Walk(child, relative == "" ? name : relative + "\\" + name);
                        }
                    }
                }
                finally { if (existing is null) handle.Dispose(); }
            }
            Walk(root, "", held); return result;
        }
        private static void Match(IReadOnlyList<ExternalOperationEntry> expected, IReadOnlyList<ExternalOperationEntry> actual, bool identities = true)
        {
            if (expected.Count != actual.Count) throw new FileCommandException(FileStatus.RecoveryRequired);
            var rows = actual.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var item in expected)
            {
                if (!rows.TryGetValue(item.RelativePath, out var other) || item.Directory != other.Directory || item.Size != other.Size ||
                    identities && item.Version != other.Version || !identities && !item.Directory && item.Version.Split(':')[^1] != other.Version.Split(':')[^1])
                    throw new FileCommandException(FileStatus.RecoveryRequired);
            }
        }
        private static void DeleteOwned(string path, IReadOnlyList<ExternalOperationEntry> expected)
        {
            // Acquire and verify every object before deleting any part. Never follow links.
            var opened = new List<(SafeFileHandle Handle, ExternalOperationEntry Entry)>();
            try
            {
                foreach (var entry in expected)
                {
                    var full = entry.RelativePath == "" ? path : Path.Combine(path, entry.RelativePath);
                    var handle = SafeNativeFile.Open(full, entry.Directory, delete: true);
                    opened.Add((handle, entry));
                    if (SafeNativeFile.Fingerprint(handle) != entry.Version) throw new FileCommandException(FileStatus.RecoveryRequired);
                }
                foreach (var item in opened.AsEnumerable().Reverse()) { SafeNativeFile.Delete(item.Handle); item.Handle.Dispose(); }
            }
            finally { foreach (var item in opened) item.Handle.Dispose(); }
        }
        private void Persist(ExternalOperationPlan plan)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
            if (bytes.Length > 1024 * 1024) throw new FileCommandException(FileStatus.LimitExceeded);
            using var file = new FileStream(PlanPath(plan.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            file.Write(bytes); file.Flush(true);
        }
        public IReadOnlyList<ExternalOperationPlan> Pending()
        {
            if (Directory.EnumerateDirectories(_root).Any()) throw new FileCommandException(FileStatus.RecoveryRequired);
            var result = new List<ExternalOperationPlan>();
            foreach (var path in Directory.EnumerateFiles(_root))
            {
                if (Path.GetFileName(path) == "lock") continue;
                if (result.Count >= 100 || Path.GetExtension(path) != ".json") throw new FileCommandException(FileStatus.RecoveryRequired);
                using var handle = SafeNativeFile.Open(path, false);
                var length = SafeNativeFile.Information(handle).Length;
                if (length > 1024 * 1024 || length == 0) throw new FileCommandException(FileStatus.RecoveryRequired);
                var bytes = new byte[(int)length]; var read = 0;
                while (read < bytes.Length) { var count = RandomAccess.Read(handle, bytes.AsSpan(read), read); if (count == 0) throw new FileCommandException(FileStatus.RecoveryRequired); read += count; }
                ExternalOperationPlan? plan;
                try { plan = JsonSerializer.Deserialize<ExternalOperationPlan>(bytes); }
                catch (JsonException) { throw new FileCommandException(FileStatus.RecoveryRequired); }
                if (plan is null || plan.Id == Guid.Empty || PlanPath(plan.Id) != path || !Enum.IsDefined(plan.Kind) || plan.Entries is not { Count: > 0 and <= ExternalOperationRules.Nodes })
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                ValidatePaths(plan);
                if (plan.Stage is not null && plan.Stage != Path.Combine(Path.GetDirectoryName(plan.Target)!, ".sfm-op-" + plan.Id.ToString("N")))
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                if (plan.Entries[0].RelativePath != "" || plan.Entries.Any(x => x.RelativePath != "" && !x.RelativePath.Split('\\').All(RelativePathSyntax.IsNameValid)))
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                result.Add(plan);
            }
            return result;
        }
        public ExternalOperationPlan Prepare(ExternalChangeRequest request, CancellationToken ct)
        {
            if (request.Kind is ExternalChange.Delete or ExternalChange.Restore) return PrepareTrash(request, ct);
            if (!RelativePathSyntax.IsNameValid(request.Name) || request.Name.StartsWith(".sfm-", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Invalid);
            if (request.Kind == ExternalChange.CreateZip && !request.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Invalid);
            var parent = _paths.Normalize(request.Directory); Pin(parent);
            var target = _paths.Normalize(Path.Combine(parent, request.Name)); FreeName(target);
            string? source = null; List<ExternalOperationEntry>? original = null;
            if (request.Kind is ExternalChange.Copy or ExternalChange.Move or ExternalChange.CreateZip or ExternalChange.ExtractZip)
            {
                source = _paths.Normalize(request.Source ?? "");
                if (source.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase) || target.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Invalid);
                if (request.Kind == ExternalChange.Move && !Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                    throw new FileCommandException(FileStatus.Unsupported);
                Pin(Path.GetDirectoryName(source)!);
                var directory = (File.GetAttributes(source) & FileAttributes.Directory) != 0;
                _source = SafeNativeFile.Open(source, directory, delete: request.Kind == ExternalChange.Move);
                original = Snapshot(source, ct, held: _source);
                if (request.Kind == ExternalChange.ExtractZip && request.ExpectedVersion is { } expected && original[0].Version != expected)
                    throw new FileCommandException(FileStatus.Conflict);
            }
            var id = Guid.NewGuid();
            if (request.Kind == ExternalChange.Move)
            {
                var move = new ExternalOperationPlan(id, request.Kind, source, target, null, original!); Persist(move); return move;
            }
            var stage = Path.Combine(parent, ".sfm-op-" + id.ToString("N"));
            var owned = new Dictionary<string, ExternalOperationEntry>(StringComparer.OrdinalIgnoreCase);
            void DirectoryNew(string path, string relative)
            {
                SafeNativeFile.CreateDirectoryNew(path);
                using var handle = SafeNativeFile.Open(path, true);
                var info = SafeNativeFile.Information(handle); owned.Add(relative, new(relative, true, info.Identity, 0, info.ModifiedUtc));
            }
            void FileNew(string path, string relative, Action<FileStream> write)
            {
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough);
                var identity = SafeNativeFile.Information(file.SafeFileHandle).Identity;
                try { write(file); file.Flush(true); }
                finally
                {
                    try { file.Dispose(); }
                    finally
                    {
                        using var closed = SafeNativeFile.Open(path, false);
                        var info = SafeNativeFile.Information(closed);
                        if (info.Identity != identity) throw new FileCommandException(FileStatus.RecoveryRequired);
                        owned[relative] = new(relative, false, SafeNativeFile.Fingerprint(closed), info.Length, info.ModifiedUtc);
                    }
                }
            }
            try
            {
                if (request.Kind == ExternalChange.CreateDirectory) DirectoryNew(stage, "");
                else if (request.Kind == ExternalChange.CreateFile)
                    FileNew(stage, "", stream => stream.Write(Encoding.UTF8.GetBytes(Path.GetExtension(target).ToLowerInvariant() switch { ".json" => "{}", ".xml" => "<document />", _ => "" })));
                else if (request.Kind is ExternalChange.CreateZip or ExternalChange.ExtractZip)
                {
                    GenerateZip(request.Kind, source!, stage, original!, DirectoryNew, FileNew, owned, ct);
                    Match(original!, Snapshot(source!, ct, held: _source));
                }
                else
                {
                    foreach (var entry in original!)
                    {
                        ct.ThrowIfCancellationRequested();
                        var destination = entry.RelativePath == "" ? stage : Path.Combine(stage, entry.RelativePath);
                        if (entry.Directory) { DirectoryNew(destination, entry.RelativePath); continue; }
                        var inputPath = entry.RelativePath == "" ? source! : Path.Combine(source!, entry.RelativePath);
                        var input = entry.RelativePath == "" ? _source! : SafeNativeFile.Open(inputPath, false);
                        try
                        {
                            FileNew(destination, entry.RelativePath, output =>
                            {
                                var buffer = new byte[65536]; long offset = 0; int count;
                                while ((count = RandomAccess.Read(input, buffer, offset)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if (offset + count > ExternalOperationRules.FileBytes || (_copied += count) > ExternalOperationRules.TotalBytes)
                                        throw new FileCommandException(FileStatus.LimitExceeded);
                                    output.Write(buffer, 0, count); offset += count;
                                }
                            });
                            if (SafeNativeFile.Fingerprint(input) != entry.Version) throw new FileCommandException(FileStatus.Conflict);
                        }
                        finally { if (input != _source) input.Dispose(); }
                    }
                    Match(original!, Snapshot(source!, ct, held: _source));
                }
                var entries = Snapshot(stage, ct, true);
                Match(owned.Values.ToArray(), entries);
                if (original is not null && request.Kind == ExternalChange.Copy) Match(original, entries, false);
                var plan = new ExternalOperationPlan(id, request.Kind, source, target, stage, entries); Persist(plan); return plan;
            }
            catch
            {
                if (Exists(stage))
                {
                    var current = Snapshot(stage, CancellationToken.None, true); Match(owned.Values.ToArray(), current);
                    DeleteOwned(stage, current);
                }
                throw;
            }
        }
        public IReadOnlyList<ExternalOperationEntry> Publish(ExternalOperationPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); FreeName(plan.Target);
            if (Renames(plan.Kind))
            {
                Match(plan.Entries, Snapshot(plan.Source!, ct, plan.Kind == ExternalChange.Restore, held: _source));
                SafeNativeFile.Rename(_source!, plan.Target);
                return Snapshot(plan.Target, ct, plan.Kind == ExternalChange.Delete, held: _source);
            }
            Match(plan.Entries, Snapshot(plan.Stage!, ct, true));
            using (var root = SafeNativeFile.Open(plan.Stage!, plan.Entries[0].Directory, delete: true)) SafeNativeFile.Rename(root, plan.Target);
            return Snapshot(plan.Target, ct);
        }
        public void Settle(ExternalOperationPlan plan, bool committed)
        {
            Pin(Path.GetDirectoryName(plan.Target)!);
            if (plan.Source is not null) Pin(Path.GetDirectoryName(plan.Source)!);
            var target = Exists(plan.Target);
            if (Renames(plan.Kind))
            {
                if (target)
                {
                    var rows = Snapshot(plan.Target, CancellationToken.None, plan.Kind == ExternalChange.Delete, held: _source);
                    Match(plan.Entries, rows);
                    if (!committed)
                    {
                        FreeName(plan.Source!);
                        if (_source is not null) SafeNativeFile.Rename(_source, plan.Source!);
                        else { using var root = SafeNativeFile.Open(plan.Target, rows[0].Directory, delete: true); SafeNativeFile.Rename(root, plan.Source!); }
                    }
                }
                else
                {
                    if (committed) throw new FileCommandException(FileStatus.RecoveryRequired);
                    Match(plan.Entries, Snapshot(plan.Source!, CancellationToken.None, plan.Kind == ExternalChange.Restore, held: _source));
                }
            }
            else
            {
                if (target)
                {
                    var rows = Snapshot(plan.Target, CancellationToken.None); Match(plan.Entries, rows);
                    if (!committed) DeleteOwned(plan.Target, rows);
                }
                else if (committed) throw new FileCommandException(FileStatus.RecoveryRequired);
                if (plan.Stage is not null && Exists(plan.Stage))
                {
                    var rows = Snapshot(plan.Stage, CancellationToken.None, true); Match(plan.Entries, rows);
                    DeleteOwned(plan.Stage, rows);
                }
            }
            if (committed && plan.Kind == ExternalChange.Delete) SaveReceipt(plan);
            if (committed && plan.Kind == ExternalChange.Restore) RemoveReceipt(plan);
            File.Delete(PlanPath(plan.Id));
        }
        public void Dispose() { _source?.Dispose(); _lock?.Dispose(); foreach (var pin in _pins) pin.Dispose(); }
    }
}
