using System.IO;
using System.Security.Cryptography;

namespace Encore.App.Updates;

/// <summary>Checks a downloaded update against the size and SHA-256 its release feed lists, before anything can apply it.</summary>
internal static class PackageCheck
{
    /// <summary>Throws, and deletes the package, unless it has exactly the expected size and SHA-256.</summary>
    public static async Task VerifyAsync(string packagePath, long expectedSize, string? expectedSha256, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        if (string.IsNullOrEmpty(expectedSha256))
        {
            Reject(packagePath, "its release feed lists no SHA-256");
        }

        var file = new FileInfo(packagePath);
        if (!file.Exists)
        {
            throw new InvalidDataException($"The update package {packagePath} doesn't exist.");
        }

        if (file.Length != expectedSize)
        {
            Reject(packagePath, $"it is {file.Length} bytes, not {expectedSize}");
        }

        string actual;
        await using (var stream = file.OpenRead())
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
        }

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Reject(packagePath, $"its SHA-256 is {actual}, not {expectedSha256}");
        }
    }

    private static void Reject(string packagePath, string why)
    {
        File.Delete(packagePath);
        throw new InvalidDataException($"The update package {Path.GetFileName(packagePath)} was deleted unused: {why}.");
    }
}
