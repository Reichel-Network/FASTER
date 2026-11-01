using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SPhase2Tests
    {
        [Test]
        public void ResolvePublishRoot_PrefersExplicitThenDeploymentFlagThenLocal()
        {
            var repo = new A3SRepository("pub", "https://example.test/.a3s/autoconfig")
            {
                LocalPath = @"C:\local",
                PublishRoot = @"C:\publish"
            };
            Assert.That(A3SBuildService.ResolvePublishRoot(repo), Is.EqualTo(@"C:\publish"));

            repo.PublishRoot = string.Empty;
            Assert.That(A3SBuildService.ResolvePublishRoot(repo), Is.EqualTo(@"C:\local"));
        }

        [Test]
        public void ResolveUploadEndpoint_HttpsFromAutoconfig()
        {
            var repo = new A3SRepository("pub", "https://arma.game.reichel.network/mods/.a3s/autoconfig")
            {
                UploadProtocol = "HTTPS"
            };
            var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repo);
            Assert.That(endpoint.Protocol, Is.EqualTo("HTTPS"));
            Assert.That(endpoint.ContentRoot.ToString(), Is.EqualTo("https://arma.game.reichel.network/mods/"));
        }

        [Test]
        public void ResolveUploadEndpoint_SftpRequiresUrl()
        {
            var repo = new A3SRepository("pub", "https://example.test/.a3s/autoconfig")
            {
                UploadProtocol = "SFTP"
            };
            Assert.Throws<InvalidOperationException>(() => A3SUrlHelpers.ResolveUploadEndpoint(repo));

            repo.UploadUrl = "files.example.test/repo";
            repo.UploadPort = "22";
            repo.UploadLogin = "user";
            var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repo);
            Assert.That(endpoint.Protocol, Is.EqualTo("SFTP"));
            Assert.That(endpoint.ContentRoot.Scheme, Is.EqualTo("sftp"));
            Assert.That(endpoint.ContentRoot.Host, Is.EqualTo("files.example.test"));
            Assert.That(endpoint.SftpRelativeToHome, Is.False);
            Assert.That(
                A3SUrlHelpers.GetSftpRemotePath(A3SUrlHelpers.GetMetadataUri(endpoint.ContentRoot, "sync"), endpoint),
                Is.EqualTo("/repo/.a3s/sync"));
        }

        [Test]
        public void ResolveUploadEndpoint_Local_SkipsRemoteCredentials()
        {
            var repo = new A3SRepository("local", "http://game.shield-community.eu/.a3s/autoconfig")
            {
                UploadProtocol = "LOCAL"
            };
            var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repo);
            Assert.That(endpoint.Protocol, Is.EqualTo("LOCAL"));
            Assert.That(A3SUrlHelpers.IsLocalOnlyUpload(endpoint), Is.True);
            Assert.That(endpoint.ContentRoot.Host, Is.EqualTo("game.shield-community.eu"));
        }

        [Test]
        public void ResolveUploadEndpoint_SftpHostOnly_UsesHomeRelativePaths()
        {
            var repo = new A3SRepository("pub", "https://arma.game.reichel.network/.a3s/autoconfig")
            {
                UploadProtocol = "SFTP",
                UploadUrl = "85.190.116.165",
                UploadPort = "22",
                UploadLogin = "armasync"
            };
            var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repo);
            Assert.That(endpoint.SftpRelativeToHome, Is.True);
            Assert.That(endpoint.ContentRoot.AbsolutePath, Is.EqualTo("/"));

            var meta = A3SUrlHelpers.GetMetadataUri(endpoint.ContentRoot, "sync");
            Assert.That(meta.AbsolutePath, Is.EqualTo("/.a3s/sync"));
            Assert.That(A3SUrlHelpers.GetSftpRemotePath(meta, endpoint), Is.EqualTo(".a3s/sync"));

            var addon = A3SUrlHelpers.GetAddonFileUri(endpoint.ContentRoot, "@mod/addons/x.pbo");
            Assert.That(A3SUrlHelpers.GetSftpRemotePath(addon, endpoint), Is.EqualTo("@mod/addons/x.pbo"));
        }

        [Test]
        public void GetMetadataUri_RequiresTrailingSlashOnContentRoot()
        {
            // Without normalization, Uri treats the last segment as a file and replaces it.
            var broken = new Uri("sftp://files.example.test/repository");
            Assert.That(new Uri(broken, ".a3s/sync").AbsolutePath, Is.EqualTo("/.a3s/sync"));

            var fixedPath = A3SUrlHelpers.GetMetadataUri(broken, "sync").AbsolutePath;
            Assert.That(fixedPath, Is.EqualTo("/repository/.a3s/sync"));
        }

        [Test]
        public async Task WalkContentTree_HashesFilesAndMarksAddons()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-walk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@mod", "addons"));
            var file = Path.Combine(root, "@mod", "addons", "mod.pbo");
            var payload = Encoding.UTF8.GetBytes("pbo-bytes");
            await File.WriteAllBytesAsync(file, payload);
            Directory.CreateDirectory(Path.Combine(root, ".a3s"));
            await File.WriteAllTextAsync(Path.Combine(root, ".a3s", "sync"), "ignore-me");
            Directory.CreateDirectory(Path.Combine(root, ".a3s-json"));
            await File.WriteAllTextAsync(Path.Combine(root, ".a3s-json", "all.json"), "{\"bridgeVersion\":1}");
            await File.WriteAllTextAsync(Path.Combine(root, "@mod", "addons", "mod.pbo.zsync"), "skip");
            await File.WriteAllTextAsync(Path.Combine(root, "web.config"), "<configuration />");

            try
            {
                var tree = await A3SBuildService.WalkContentTreeAsync(root);
                Assert.That(tree.Name, Is.EqualTo("racine"));
                var leaves = tree.EnumerateLeaves().ToList();
                Assert.That(leaves.Count, Is.EqualTo(1));
                Assert.That(leaves[0].RelativePath, Is.EqualTo("@mod/addons/mod.pbo"));
                Assert.That(leaves[0].Sha1, Is.EqualTo(Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant()));
                Assert.That(tree.AddonFolderRelativePaths(), Does.Contain("@mod"));
                Assert.That(leaves.Any(l => l.RelativePath.Equals("web.config", StringComparison.OrdinalIgnoreCase)), Is.False);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void IsMetadataSidecarDirectory_SkipsA3sAndJsonStore()
        {
            Assert.That(A3SUrlHelpers.IsMetadataSidecarDirectory(".a3s"), Is.True);
            Assert.That(A3SUrlHelpers.IsMetadataSidecarDirectory(".a3s-json"), Is.True);
            Assert.That(A3SUrlHelpers.IsUnderMetadataSidecar(".a3s-json/all.json"), Is.True);
            Assert.That(A3SUrlHelpers.IsUnderMetadataSidecar("@mod/addons/x.pbo"), Is.False);
        }

        [Test]
        public async Task BuildService_WriteAllEnvelope_BumpsRevisionAndChangelogs()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@alpha", "addons"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@alpha", "addons", "a.pbo"), Encoding.UTF8.GetBytes("a1"));

            var store = new JsonA3SMetadataStore(fixedEnvelopePath: null, writeRoot: root);
            // Seed previous revision via WriteAll so next build bumps to 2.
            await store.WriteAllAsync(root, new A3SMetadataWriteSet
            {
                Sync = new A3SSyncTreeNode { Type = "directory", Name = "racine" },
                ServerInfo = new A3SServerInfo { Revision = 3, NumberOfFiles = 0 },
                Changelogs = new A3SChangelogs
                {
                    List = { new A3SChangelogEntry { Revision = 3, Addons = { "@alpha" } } }
                },
                Autoconfig = new A3SAutoconfigInfo { RepositoryName = "pub" }
            });

            var repo = new A3SRepository("pub", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root,
                NumberOfConnections = 2
            };

            try
            {
                var build = new A3SBuildService(store);
                var result = await build.BuildAsync(repo);

                Assert.That(result.Revision, Is.EqualTo(4));
                Assert.That(result.FileCount, Is.EqualTo(1));
                Assert.That(result.ServerInfo.NumberOfConnections, Is.EqualTo(2));
                Assert.That(store.LastWriteSet, Is.Not.Null);
                Assert.That(store.LastWriteSet!.Sync, Is.Not.Null);
                Assert.That(store.LastWriteSet.ServerInfo!.Revision, Is.EqualTo(4));
                Assert.That(store.LastWriteSet.Changelogs!.List.Any(c => c.Revision == 4), Is.True);
                Assert.That(store.LastWriteSet.Autoconfig!.RepositoryName, Is.EqualTo("pub"));
                Assert.That(repo.LastRevision, Is.EqualTo(4));
                Assert.That(repo.LastBuildUtc, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(root, ".a3s-json", "all.json")));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task ProtocolClient_HttpPutUpload_SendsFile()
        {
            byte[]? received = null;
            Uri? putUri = null;
            var handler = new StubHttpHandler((request, _) =>
            {
                if (request.Method != HttpMethod.Put)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
                putUri = request.RequestUri;
                return request.Content!.ReadAsByteArrayAsync().ContinueWith(t =>
                {
                    received = t.Result;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                });
            });

            var path = Path.Combine(Path.GetTempPath(), "faster-up-" + Guid.NewGuid().ToString("N") + ".bin");
            var payload = Encoding.UTF8.GetBytes("upload-me");
            await File.WriteAllBytesAsync(path, payload);
            try
            {
                using var client = new A3SProtocolClient(new HttpClient(handler), ownsHttp: true);
                await client.UploadAsync(path, new Uri("https://example.test/repo/file.bin"),
                    new A3SUploadEndpoint { Protocol = "HTTPS", Login = "anonymous" });
                Assert.That(received, Is.EqualTo(payload));
                Assert.That(putUri!.ToString(), Is.EqualTo("https://example.test/repo/file.bin"));
            }
            finally
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task PublishService_UploadsChangedFilesAndMetadata()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-pub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@mod", "addons"));
            var payload = Encoding.UTF8.GetBytes("content-v1");
            await File.WriteAllBytesAsync(Path.Combine(root, "@mod", "addons", "x.pbo"), payload);

            var store = new JsonA3SMetadataStore(fixedEnvelopePath: null, writeRoot: root);
            var uploaded = new List<string>();
            var handler = new StubHttpHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Put)
                {
                    uploaded.Add(request.RequestUri!.AbsolutePath);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var repo = new A3SRepository("pub", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root,
                UploadProtocol = "HTTPS"
            };

            try
            {
                var protocol = new A3SProtocolClient(new HttpClient(handler), ownsHttp: true);
                var publish = new A3SPublishService(store, protocol);
                var result = await publish.PublishAsync(repo, new A3SUploadOptions { BuildFirst = true, ForceAllFiles = true });

                Assert.That(result.Failed, Is.EqualTo(0), string.Join("; ", result.Errors));
                Assert.That(result.UploadedFiles, Is.EqualTo(1));
                Assert.That(result.UploadedMetadataFiles, Is.GreaterThanOrEqualTo(3));
                Assert.That(uploaded.Any(p => p.EndsWith("/@mod/addons/x.pbo", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(uploaded.Any(p => p.Contains("/.a3s/sync")), Is.True);
                Assert.That(repo.LastPublishUtc, Is.Not.Null);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void BridgeA3SMetadataStore_WriteAll_MissingJar_ThrowsGracefully()
        {
            var store = new BridgeA3SMetadataStore(Path.Combine(Path.GetTempPath(), $"missing-bridge-{Guid.NewGuid():N}.jar"));
            var ex = Assert.ThrowsAsync<A3SBridgeNotFoundException>(async () =>
                await store.WriteAllAsync("/tmp/repo", new A3SMetadataWriteSet
                {
                    ServerInfo = new A3SServerInfo { Revision = 1 }
                }));
            Assert.That(ex!.Message, Does.Contain("metadata bridge").IgnoreCase);
        }

        [Test]
        [Category("Integration")]
        [Ignore("Requires companion a3s-metadata-bridge JAR with write --kind all (Arma3Sync PR #2) + JDK 25")]
        public async Task Bridge_LiveWriteAll_RoundTrip()
        {
            var jar = Environment.GetEnvironmentVariable("A3S_METADATA_BRIDGE_JAR");
            Assert.That(jar, Is.Not.Null.And.Not.Empty);
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-bridge-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new BridgeA3SMetadataStore(jar);
                await store.WriteAllAsync(root, new A3SMetadataWriteSet
                {
                    ServerInfo = new A3SServerInfo
                    {
                        Revision = 99,
                        BuildDate = DateTime.UtcNow,
                        NumberOfFiles = 0,
                        TotalFilesSize = 0,
                        NumberOfConnections = 1
                    },
                    Sync = new A3SSyncTreeNode { Type = "directory", Name = "racine" },
                    Changelogs = new A3SChangelogs(),
                    Autoconfig = new A3SAutoconfigInfo
                    {
                        RepositoryName = "faster-phase2-test",
                        Protocol = new A3SProtocolInfo
                        {
                            Type = "HTTPS",
                            Url = "example.test",
                            Port = "443",
                            Login = "anonymous",
                            Password = string.Empty
                        }
                    }
                });

                var server = await store.ReadServerInfoAsync(root);
                Assert.That(server.Revision, Is.EqualTo(99));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _handler(request, cancellationToken);
        }
    }
}
