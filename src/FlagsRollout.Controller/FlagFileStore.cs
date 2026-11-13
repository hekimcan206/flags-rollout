using System.Security.Cryptography;
using System.Text;
using FlagsRollout.Core.Flags;

namespace FlagsRollout.Controller;

/// <summary>A flag file as read, with the content hash it must still have when the controller writes it back.</summary>
public sealed record VersionedFlagDocument(FlagDocument Document, string Version);

/// <summary>Someone else changed the flag file between the controller's read and its write.</summary>
public sealed class FlagFileConflictException(string message) : Exception(message)
{
    public FlagFileConflictException()
        : this("The flag file changed since it was read.")
    {
    }

    public FlagFileConflictException(string message, Exception innerException)
        : this(message + " " + innerException?.Message)
    {
    }
}

/// <summary>
/// The flag file shared with flagd's file sync, written so that flagd never sees a half-written file and a human edit
/// is never silently overwritten.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Atomic replace: write a temporary file in the same directory, flush it to disk, then rename it over the
/// original. flagd (and anything else reading) sees either the old or the new file, never a prefix.</item>
/// <item>Optimistic concurrency: the write carries the SHA-256 of the content it was derived from and refuses if the
/// file changed meanwhile. There is a small window between that check and the rename; closing it would need a lock
/// every editor honours, which humans with a text editor do not. The next tick re-reads, so a lost race costs one
/// tick, never a reverted human change that goes unnoticed (the decision is re-derived from the new content).</item>
/// </list>
/// </remarks>
public sealed class FlagFileStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async Task<VersionedFlagDocument> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(Path, cancellationToken);
        return new VersionedFlagDocument(FlagDocument.Parse(Encoding.UTF8.GetString(bytes)), Hash(bytes));
    }

    /// <summary>Replaces the file if it still has <paramref name="expectedVersion"/>; returns the new version.</summary>
    public async Task<string> WriteAsync(FlagDocument document, string expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = Hash(await File.ReadAllBytesAsync(Path, cancellationToken));
        if (!string.Equals(current, expectedVersion, StringComparison.Ordinal))
        {
            throw new FlagFileConflictException($"Flag file {Path} changed since it was read (expected {expectedVersion[..12]}, found {current[..12]}).");
        }

        var bytes = Encoding.UTF8.GetBytes(document.ToJson());
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                // Flush to the device before the rename, so a crash cannot leave a renamed but empty file.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return Hash(bytes);
    }

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
