using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>SHA-1 helpers matching Arma3Sync repository integrity checks (not for security).</summary>
    public static class A3SHash
    {
        public static async Task<string> ComputeSha1HexAsync(string filePath, CancellationToken cancellationToken = default)
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

#pragma warning disable CA5350 // SHA-1 required for classic A3S wire compatibility
            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static bool EqualsSha1(string? expected, string? actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
                return false;
            return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
