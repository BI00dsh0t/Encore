namespace Encore.Core.Library;

/// <summary>One file whose length the worker reads: its folder and key as queued, its path, and the size it had then.</summary>
internal sealed record LengthJob(string Folder, string Key, string Path, long Size);
