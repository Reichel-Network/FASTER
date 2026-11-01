using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;
using FASTER.Services.A3S.Zsync;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SZsyncTests
    {
        [Test]
        public void AnalyzeFile_MatchesJavaReferenceFor84950BytePbo()
        {
            // Live Java Arma3Sync header for zen_ai.pbo (84950 bytes): Hash-Lengths: 2,3,3
            var lengths = A3SZsyncMetaFileMaker.AnalyzeFile(84950, A3SZsyncMetaFileMaker.DefaultBlockSize);
            Assert.That(lengths[0], Is.EqualTo(2));
            Assert.That(lengths[1], Is.EqualTo(3));
            Assert.That(lengths[2], Is.EqualTo(3));
        }

        [Test]
        public async Task WriteAsync_ChecksumSectionMatchesJavaReferenceZsync()
        {
            var refZsync = Path.Combine(Path.GetTempPath(), "ref-zen_ai.pbo.zsync");
            var refPbo = Path.Combine(Path.GetTempPath(), "ref-zen_ai.pbo");
            if (!File.Exists(refZsync) || !File.Exists(refPbo))
            {
                Assert.Ignore("Reference zen_ai.pbo / .zsync not present in TEMP (download during agent setup).");
            }

            var outPath = Path.Combine(Path.GetTempPath(), "faster-zen_ai.pbo.zsync-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sha1 = Convert.ToHexString(SHA1.HashData(await File.ReadAllBytesAsync(refPbo))).ToLowerInvariant();
                await A3SZsyncMetaFileMaker.WriteAsync(
                    refPbo,
                    outPath,
                    "https://arma.game.reichel.network/1779063631/addons/zen_ai.pbo",
                    sha1);

                var javaBytes = await File.ReadAllBytesAsync(refZsync);
                var ours = await File.ReadAllBytesAsync(outPath);

                var javaPayload = SliceAfterHeader(javaBytes);
                var ourPayload = SliceAfterHeader(ours);
                Assert.That(ourPayload, Is.EqualTo(javaPayload), "Block checksum section must match Java MetaFileMaker.");

                var headerText = Encoding.ASCII.GetString(ours, 0, IndexOfHeaderEnd(ours));
                Assert.That(headerText, Does.Contain("zsync: jazsync"));
                Assert.That(headerText, Does.Contain("Blocksize: 8192"));
                Assert.That(headerText, Does.Contain("Hash-Lengths: 2,3,3"));
                Assert.That(headerText, Does.Contain("SHA-1: " + sha1));
                Assert.That(headerText, Does.Not.Contain(" \n"), "No trailing spaces before newlines (breaks Java Long.parseLong).");
            }
            finally
            {
                try { File.Delete(outPath); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task Build_GeneratesSiblingZsyncForHttpRepo()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-zsync-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "450814997", "addons"));
            var pbo = Path.Combine(root, "450814997", "addons", "cba.pbo");
            await File.WriteAllBytesAsync(pbo, Encoding.UTF8.GetBytes(new string('x', 9000)));
            var bisign = Path.Combine(root, "450814997", "addons", "cba.pbo.bisign");
            await File.WriteAllBytesAsync(bisign, Encoding.UTF8.GetBytes("sig"));

            var store = new JsonA3SMetadataStore(fixedEnvelopePath: null, writeRoot: root);
            var repo = new A3SRepository("Shield-GameBox", "http://game.shield-community.eu/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root,
                UploadProtocol = "LOCAL"
            };

            try
            {
                var build = new A3SBuildService(store);
                var result = await build.BuildAsync(repo);

                Assert.That(result.FileCount, Is.EqualTo(2));
                Assert.That(result.ZsyncGenerated, Is.EqualTo(2));
                Assert.That(File.Exists(pbo + ".zsync"));
                Assert.That(File.Exists(bisign + ".zsync"));
                Assert.That(new FileInfo(pbo + ".zsync").Length, Is.GreaterThan(50));

                // .zsync must not appear as sync leaves
                Assert.That(result.SyncRoot.EnumerateLeaves().All(l => !l.RelativePath.EndsWith(".zsync", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void ShouldGenerateZsync_HttpLocalYes_SftpNo()
        {
            var http = new A3SRepository("r", "http://game.shield-community.eu/.a3s/autoconfig")
            {
                UploadProtocol = "LOCAL"
            };
            Assert.That(A3SZsyncService.ShouldGenerateZsync(http), Is.True);

            var sftp = new A3SRepository("r", "ftp://files.example/.a3s/autoconfig")
            {
                UploadProtocol = "SFTP",
                UploadUrl = "files.example",
                ProtocolHint = "FTP"
            };
            Assert.That(A3SZsyncService.ShouldGenerateZsync(sftp), Is.False);
            Assert.That(A3SZsyncService.ShouldGenerateZsync(sftp, new A3SBuildOptions { ForceZsync = true }), Is.True);
        }

        private static int IndexOfHeaderEnd(byte[] bytes)
        {
            for (var i = 1; i < bytes.Length; i++)
            {
                if (bytes[i - 1] == 0x0A && bytes[i] == 0x0A)
                    return i + 1;
            }

            throw new InvalidOperationException("zsync header terminator not found");
        }

        private static byte[] SliceAfterHeader(byte[] bytes)
        {
            var start = IndexOfHeaderEnd(bytes);
            var slice = new byte[bytes.Length - start];
            Buffer.BlockCopy(bytes, start, slice, 0, slice.Length);
            return slice;
        }
    }
}
