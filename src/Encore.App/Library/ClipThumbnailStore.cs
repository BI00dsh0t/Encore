using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>Instance-local, disposable derived data. Only complete, validated filmstrips are published.</summary>
internal sealed class ClipThumbnailStore
{
    private const int MaxEntries = 128;
    private const long MaxBytes = 256L * 1024 * 1024;
    private readonly string _root;

    internal ClipThumbnailStore(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private string Entry(ClipSource source, double duration)
    {
        var identity = string.Join('\n', "filmstrip-v1-160x90-64-1fps", Path.GetFullPath(source.Path).ToUpperInvariant(),
            source.Length.ToString(CultureInfo.InvariantCulture), source.ModifiedUtcTicks.ToString(CultureInfo.InvariantCulture), duration.ToString("R", CultureInfo.InvariantCulture));
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    private static bool MatchesSource(ClipSource source)
    {
        var file = new FileInfo(source.Path);
        return file.Exists && file.Length == source.Length && file.LastWriteTimeUtc.Ticks == source.ModifiedUtcTicks;
    }

    internal Task<ClipThumbnailImage[]?> TryLoadAsync(ClipSource source, double duration, CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var entry = Entry(source, duration);
            var manifest = new FileInfo(Path.Combine(entry, "manifest.json"));
            if (!MatchesSource(source) || !manifest.Exists || manifest.Length is < 1 or > 65536) { return null; }
            using var stream = manifest.OpenRead();
            var response = await JsonSerializer.DeserializeAsync(stream, ClipMediaJsonContext.Default.ClipMediaResponse, token).ConfigureAwait(false);
            if (response?.Thumbnails is not { } descriptor) { return null; }
            var images = await ClipThumbnailCache.LoadAsync(entry, descriptor, source, duration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!MatchesSource(source)) { return null; }
            try { Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return images;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (ArgumentException) { return null; }
        catch (FormatException) { return null; }
        catch (InvalidOperationException) { return null; }
    }, token);

    internal Task SaveAsync(string preparedFolder, ClipThumbnailDescriptor descriptor, CancellationToken token) => Task.Run(() =>
    {
        string? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!MatchesSource(descriptor.Source)) { return; }
            Directory.CreateDirectory(_root);
            staging = Path.Combine(_root, ".pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var frame in descriptor.Frames)
            {
                token.ThrowIfCancellationRequested();
                // The descriptor and image bounds were validated by ClipThumbnailCache before this copy.
                if (frame.FileName != Path.GetFileName(frame.FileName)) { throw new IOException("Invalid thumbnail path."); }
                File.Copy(Path.Combine(preparedFolder, frame.FileName), Path.Combine(staging, frame.FileName));
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new ClipMediaResponse("complete", Thumbnails: descriptor), ClipMediaJsonContext.Default.ClipMediaResponse));
            token.ThrowIfCancellationRequested();
            if (!MatchesSource(descriptor.Source)) { return; }
            var entry = Entry(descriptor.Source, descriptor.Duration);
            // An invalid entry is replaced only after the new filmstrip is complete. Another reader
            // or writer may win this race; cache IO failure never invalidates the in-memory images.
            if (Directory.Exists(entry)) { DeleteEntry(entry); }
            Directory.Move(staging, entry);
            staging = null;
            Prune(entry);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { if (staging is not null) { DeleteStaging(staging); } }
    }, token);

    private void Prune(string retained)
    {
        foreach (var pending in new DirectoryInfo(_root).EnumerateDirectories().Where(directory => IsStaging(directory.Name)
            && directory.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) { DeleteStaging(pending.FullName); }
        var entries = new DirectoryInfo(_root).EnumerateDirectories().Where(directory => IsEntry(directory.Name)
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(directory => (Directory: directory, Bytes: directory.EnumerateFiles().Sum(file => file.Length)))
            .OrderByDescending(entry => entry.Directory.LastWriteTimeUtc).ToArray();
        long size = 0;
        var count = 0;
        foreach (var entry in entries.OrderByDescending(entry => entry.Directory.FullName == retained))
        {
            size += entry.Bytes; count++;
            if (count > MaxEntries || size > MaxBytes) { DeleteEntry(entry.Directory.FullName); }
        }
    }

    private static bool IsEntry(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigit);
    private static bool IsStaging(string name) => name.Length == 41 && name.StartsWith(".pending-", StringComparison.Ordinal) && name.AsSpan(9).ToString().All(char.IsAsciiHexDigit);
    private void DeleteEntry(string path) => DeleteOwned(path, staging: false);
    private void DeleteStaging(string path) => DeleteOwned(path, staging: true);
    private void DeleteOwned(string path, bool staging)
    {
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(target), _root, StringComparison.OrdinalIgnoreCase)
            || !(staging ? IsStaging(Path.GetFileName(target)) : IsEntry(Path.GetFileName(target)))) { return; }
        try
        {
            if (!Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0
                || Directory.EnumerateDirectories(target).Any()) { return; }
            Directory.Delete(target, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
