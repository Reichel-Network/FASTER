using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S.Zsync
{
    /// <summary>
    /// Classic Arma3Sync <c>.zsync</c> control-file writer (jazsync / MetaFileMaker compatible).
    /// Matches Java <c>fr.soe.a3s.jazsyncmake.MetaFileMaker</c> so ArmA3Sync HTTP clients can patch.
    /// </summary>
    public static class A3SZsyncMetaFileMaker
    {
        public const int DefaultBlockSize = 8192;
        public const int StrongSumMaxLength = 16;
        public const string ZsyncVersionLabel = "jazsync";

        /// <summary>
        /// Write <paramref name="targetZsyncPath"/> for <paramref name="sourceFile"/> using Arma3Sync/jazsync layout.
        /// </summary>
        /// <param name="sourceFile">Addon leaf on disk (e.g. <c>mod.pbo</c> / <c>mod.pbo.bisign</c>).</param>
        /// <param name="targetZsyncPath">Sibling <c>*.zsync</c> path.</param>
        /// <param name="url">HTTP(S) URL of the source file (written into the control header).</param>
        /// <param name="sha1Hex">Lowercase hex SHA-1 of the source (same as sync leaf).</param>
        public static async Task WriteAsync(
            string sourceFile,
            string targetZsyncPath,
            string url,
            string sha1Hex,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetZsyncPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            ArgumentException.ThrowIfNullOrWhiteSpace(sha1Hex);

            var info = new FileInfo(sourceFile);
            if (!info.Exists)
                throw new FileNotFoundException("Source file for zsync generation not found.", sourceFile);

            var fileLength = info.Length;
            var hashLengths = AnalyzeFile(fileLength, DefaultBlockSize);
            var header = BuildHeader(
                fileName: info.Name,
                mtimeUtcOrLocal: info.LastWriteTime,
                blockSize: DefaultBlockSize,
                length: fileLength,
                hashLengths: hashLengths,
                url: url,
                sha1Hex: sha1Hex.Trim().ToLowerInvariant());

            var directory = Path.GetDirectoryName(targetZsyncPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = targetZsyncPath + ".faster.tmp";
            try
            {
                await using (var fs = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var headerBytes = Encoding.ASCII.GetBytes(header);
                    await fs.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

                    await using var input = new FileStream(
                        sourceFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: DefaultBlockSize,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                    var block = new byte[DefaultBlockSize];
                    long remaining = fileLength;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var toRead = (int)Math.Min(DefaultBlockSize, remaining);
                        var read = await input.ReadAsync(block.AsMemory(0, toRead), cancellationToken)
                            .ConfigureAwait(false);
                        if (read <= 0)
                            break;

                        if (read < DefaultBlockSize)
                            Array.Clear(block, read, DefaultBlockSize - read);

                        // Java Generator always checksums a full blocksize buffer (NUL-padded short tail).
                        var weak = ComputeWeakSum(block, 0, DefaultBlockSize);
                        var strongFull = Md4.Hash(block, 0, DefaultBlockSize);
                        var strongLen = hashLengths[2];
                        var strong = new byte[strongLen];
                        Buffer.BlockCopy(strongFull, 0, strong, 0, strongLen);

                        var weakBytes = WeakSumToBytes(weak, hashLengths[1]);
                        await fs.WriteAsync(weakBytes, cancellationToken).ConfigureAwait(false);
                        await fs.WriteAsync(strong, cancellationToken).ConfigureAwait(false);

                        remaining -= read;
                    }
                }

                File.Copy(tempPath, targetZsyncPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>
        /// Same analysis as Java <c>MetaFileMaker.analyzeFile</c> (blocksize fixed at 8192).
        /// Returns [seq_matches, rsum_bytes, strong_bytes].
        /// </summary>
        public static int[] AnalyzeFile(long fileLength, int blockSize)
        {
            // Avoid Math.Log(0) for empty files (addons are never empty in practice).
            var length = Math.Max(fileLength, 1);
            var hashLengths = new int[3];
            hashLengths[0] = fileLength > blockSize ? 2 : 1;
            // Java: Math.ceil(((Math.log(fileLength)+Math.log(blocksize))/Math.log(2)-8.6)/8)
            hashLengths[1] = (int)Math.Ceiling(((Math.Log(length) + Math.Log(blockSize)) / Math.Log(2) - 8.6) / 8);

            if (hashLengths[1] > 4)
                hashLengths[1] = 4;
            if (hashLengths[1] < 2)
                hashLengths[1] = 2;

            hashLengths[2] = (int)Math.Ceiling(
                (20 + (Math.Log(length) + Math.Log(1 + length / (double)blockSize)) / Math.Log(2))
                / hashLengths[0]
                / 8);

            var strongSumLength2 = (int)((7.9 + (20 + Math.Log(1 + length / (double)blockSize) / Math.Log(2))) / 8);
            if (hashLengths[2] < strongSumLength2)
                hashLengths[2] = strongSumLength2;

            if (hashLengths[2] > StrongSumMaxLength)
                hashLengths[2] = StrongSumMaxLength;

            return hashLengths;
        }

        internal static string BuildHeader(
            string fileName,
            DateTime mtimeUtcOrLocal,
            int blockSize,
            long length,
            int[] hashLengths,
            string url,
            string sha1Hex)
        {
            // Java SimpleDateFormat("EEE, dd MMM yyyy HH:mm:ss Z", Locale.US) → "+0100" (no colon).
            var mtime = mtimeUtcOrLocal.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)
                        + mtimeUtcOrLocal.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty);

            var sb = new StringBuilder();
            sb.Append("zsync: ").Append(ZsyncVersionLabel).Append('\n');
            sb.Append("Filename: ").Append(fileName).Append('\n');
            sb.Append("MTime: ").Append(mtime).Append('\n');
            sb.Append("Blocksize: ").Append(blockSize).Append('\n');
            sb.Append("Length: ").Append(length).Append('\n');
            sb.Append("Hash-Lengths: ")
                .Append(hashLengths[0]).Append(',')
                .Append(hashLengths[1]).Append(',')
                .Append(hashLengths[2]).Append('\n');
            sb.Append("URL: ").Append(url).Append('\n');
            sb.Append("SHA-1: ").Append(sha1Hex).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>Java <c>Rsum.check</c> → <c>getValue</c> packing: <c>(a &amp; 0xffff) | (b &lt;&lt; 16)</c>.</summary>
        internal static int ComputeWeakSum(byte[] buf, int offset, int length)
        {
            int a = 0;
            int b = 0;
            var index = offset;
            for (var i = length; i > 0; i--)
            {
                var unsignedB = buf[index] & 0xff;
                a += unsignedB;
                b += i * unsignedB;
                index++;
            }

            a &= 0xffff;
            b &= 0xffff;
            return a | (b << 16);
        }

        /// <summary>Java <c>MetaFileMaker.intToBytes</c> (htons-style packing used by jazsync).</summary>
        internal static byte[] WeakSumToBytes(int number, int rsumBytes)
        {
            return rsumBytes switch
            {
                2 => new[]
                {
                    (byte)(number >> 24),
                    (byte)((number << 8) >> 24)
                },
                3 => new[]
                {
                    (byte)((number << 24) >> 24),
                    (byte)(number >> 24),
                    (byte)((number << 8) >> 24)
                },
                4 => new[]
                {
                    (byte)((number << 16) >> 24),
                    (byte)((number << 24) >> 24),
                    (byte)(number >> 24),
                    (byte)((number << 8) >> 24)
                },
                _ => throw new ArgumentOutOfRangeException(nameof(rsumBytes), rsumBytes, "rsum bytes must be 2–4.")
            };
        }

        /// <summary>RFC 1320 MD4 (GNU Crypto / jarsync port used by Arma3Sync jazsync).</summary>
        internal static class Md4
        {
            private const int BlockLength = 64;
            private const int A0 = unchecked((int)0x67452301);
            private const int B0 = unchecked((int)0xefcdab89);
            private const int C0 = unchecked((int)0x98badcfe);
            private const int D0 = 0x10325476;

            public static byte[] Hash(byte[] input, int offset, int length)
            {
                var a = A0;
                var b = B0;
                var c = C0;
                var d = D0;
                long count = 0;
                var buffer = new byte[BlockLength];
                var x = new int[16];

                void Update(byte[] data, int off, int len)
                {
                    var n = (int)(count % BlockLength);
                    count += len;
                    var partLen = BlockLength - n;
                    var i = 0;

                    if (len >= partLen)
                    {
                        Buffer.BlockCopy(data, off, buffer, n, partLen);
                        Transform(ref a, ref b, ref c, ref d, buffer, 0, x);
                        for (i = partLen; i + BlockLength - 1 < len; i += BlockLength)
                            Transform(ref a, ref b, ref c, ref d, data, off + i, x);
                        n = 0;
                    }

                    if (i < len)
                        Buffer.BlockCopy(data, off + i, buffer, n, len - i);
                }

                Update(input, offset, length);

                var rem = (int)(count % BlockLength);
                var padding = rem < 56 ? 56 - rem : 120 - rem;
                var pad = new byte[padding + 8];
                pad[0] = 0x80;
                var bits = count << 3;
                pad[padding++] = (byte)bits;
                pad[padding++] = (byte)(bits >> 8);
                pad[padding++] = (byte)(bits >> 16);
                pad[padding++] = (byte)(bits >> 24);
                pad[padding++] = (byte)(bits >> 32);
                pad[padding++] = (byte)(bits >> 40);
                pad[padding++] = (byte)(bits >> 48);
                pad[padding] = (byte)(bits >> 56);
                Update(pad, 0, pad.Length);

                return new[]
                {
                    (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24),
                    (byte)b, (byte)(b >> 8), (byte)(b >> 16), (byte)(b >> 24),
                    (byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24),
                    (byte)d, (byte)(d >> 8), (byte)(d >> 16), (byte)(d >> 24)
                };
            }

            private static void Transform(ref int a, ref int b, ref int c, ref int d, byte[] data, int offset, int[] x)
            {
                for (int i = 0; i < 16; i++)
                {
                    x[i] = (data[offset++] & 0xff)
                           | ((data[offset++] & 0xff) << 8)
                           | ((data[offset++] & 0xff) << 16)
                           | ((data[offset++] & 0xff) << 24);
                }

                var aa = a;
                var bb = b;
                var cc = c;
                var dd = d;

                // Round 1
                a += ((b & c) | (~b & d)) + x[0];
                a = a << 3 | a >>> 29;
                d += ((a & b) | (~a & c)) + x[1];
                d = d << 7 | d >>> 25;
                c += ((d & a) | (~d & b)) + x[2];
                c = c << 11 | c >>> 21;
                b += ((c & d) | (~c & a)) + x[3];
                b = b << 19 | b >>> 13;
                a += ((b & c) | (~b & d)) + x[4];
                a = a << 3 | a >>> 29;
                d += ((a & b) | (~a & c)) + x[5];
                d = d << 7 | d >>> 25;
                c += ((d & a) | (~d & b)) + x[6];
                c = c << 11 | c >>> 21;
                b += ((c & d) | (~c & a)) + x[7];
                b = b << 19 | b >>> 13;
                a += ((b & c) | (~b & d)) + x[8];
                a = a << 3 | a >>> 29;
                d += ((a & b) | (~a & c)) + x[9];
                d = d << 7 | d >>> 25;
                c += ((d & a) | (~d & b)) + x[10];
                c = c << 11 | c >>> 21;
                b += ((c & d) | (~c & a)) + x[11];
                b = b << 19 | b >>> 13;
                a += ((b & c) | (~b & d)) + x[12];
                a = a << 3 | a >>> 29;
                d += ((a & b) | (~a & c)) + x[13];
                d = d << 7 | d >>> 25;
                c += ((d & a) | (~d & b)) + x[14];
                c = c << 11 | c >>> 21;
                b += ((c & d) | (~c & a)) + x[15];
                b = b << 19 | b >>> 13;

                // Round 2
                a += ((b & (c | d)) | (c & d)) + x[0] + 0x5a827999;
                a = a << 3 | a >>> 29;
                d += ((a & (b | c)) | (b & c)) + x[4] + 0x5a827999;
                d = d << 5 | d >>> 27;
                c += ((d & (a | b)) | (a & b)) + x[8] + 0x5a827999;
                c = c << 9 | c >>> 23;
                b += ((c & (d | a)) | (d & a)) + x[12] + 0x5a827999;
                b = b << 13 | b >>> 19;
                a += ((b & (c | d)) | (c & d)) + x[1] + 0x5a827999;
                a = a << 3 | a >>> 29;
                d += ((a & (b | c)) | (b & c)) + x[5] + 0x5a827999;
                d = d << 5 | d >>> 27;
                c += ((d & (a | b)) | (a & b)) + x[9] + 0x5a827999;
                c = c << 9 | c >>> 23;
                b += ((c & (d | a)) | (d & a)) + x[13] + 0x5a827999;
                b = b << 13 | b >>> 19;
                a += ((b & (c | d)) | (c & d)) + x[2] + 0x5a827999;
                a = a << 3 | a >>> 29;
                d += ((a & (b | c)) | (b & c)) + x[6] + 0x5a827999;
                d = d << 5 | d >>> 27;
                c += ((d & (a | b)) | (a & b)) + x[10] + 0x5a827999;
                c = c << 9 | c >>> 23;
                b += ((c & (d | a)) | (d & a)) + x[14] + 0x5a827999;
                b = b << 13 | b >>> 19;
                a += ((b & (c | d)) | (c & d)) + x[3] + 0x5a827999;
                a = a << 3 | a >>> 29;
                d += ((a & (b | c)) | (b & c)) + x[7] + 0x5a827999;
                d = d << 5 | d >>> 27;
                c += ((d & (a | b)) | (a & b)) + x[11] + 0x5a827999;
                c = c << 9 | c >>> 23;
                b += ((c & (d | a)) | (d & a)) + x[15] + 0x5a827999;
                b = b << 13 | b >>> 19;

                // Round 3
                a += (b ^ c ^ d) + x[0] + 0x6ed9eba1;
                a = a << 3 | a >>> 29;
                d += (a ^ b ^ c) + x[8] + 0x6ed9eba1;
                d = d << 9 | d >>> 23;
                c += (d ^ a ^ b) + x[4] + 0x6ed9eba1;
                c = c << 11 | c >>> 21;
                b += (c ^ d ^ a) + x[12] + 0x6ed9eba1;
                b = b << 15 | b >>> 17;
                a += (b ^ c ^ d) + x[2] + 0x6ed9eba1;
                a = a << 3 | a >>> 29;
                d += (a ^ b ^ c) + x[10] + 0x6ed9eba1;
                d = d << 9 | d >>> 23;
                c += (d ^ a ^ b) + x[6] + 0x6ed9eba1;
                c = c << 11 | c >>> 21;
                b += (c ^ d ^ a) + x[14] + 0x6ed9eba1;
                b = b << 15 | b >>> 17;
                a += (b ^ c ^ d) + x[1] + 0x6ed9eba1;
                a = a << 3 | a >>> 29;
                d += (a ^ b ^ c) + x[9] + 0x6ed9eba1;
                d = d << 9 | d >>> 23;
                c += (d ^ a ^ b) + x[5] + 0x6ed9eba1;
                c = c << 11 | c >>> 21;
                b += (c ^ d ^ a) + x[13] + 0x6ed9eba1;
                b = b << 15 | b >>> 17;
                a += (b ^ c ^ d) + x[3] + 0x6ed9eba1;
                a = a << 3 | a >>> 29;
                d += (a ^ b ^ c) + x[11] + 0x6ed9eba1;
                d = d << 9 | d >>> 23;
                c += (d ^ a ^ b) + x[7] + 0x6ed9eba1;
                c = c << 11 | c >>> 21;
                b += (c ^ d ^ a) + x[15] + 0x6ed9eba1;
                b = b << 15 | b >>> 17;

                a += aa;
                b += bb;
                c += cc;
                d += dd;
            }
        }
    }
}
