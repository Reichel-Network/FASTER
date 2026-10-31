using System.IO;
using System.Threading.Tasks;

using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTERTests
{
    [TestFixture]
    public class A3SPackagingTests
    {
        private static string ClassicFixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "a3s-classic");

        [Test]
        public async Task NativeOnly_Serializer_SyncsReichelArmaFixture_WithoutBridge()
        {
            // Mirrors A3SMetadataStoreFactory default when bridge fallback is off.
            IA3SMetadataStore store = new SerializerA3SMetadataStore();

            var bundle = await store.ReadAllAsync(ClassicFixtureRoot);
            Assert.That(bundle.Autoconfig.RepositoryName, Is.EqualTo("reichel-arma"));
            Assert.That(bundle.ServerInfo.Revision, Is.EqualTo(1));
            Assert.That(bundle.ServerInfo.NumberOfFiles, Is.EqualTo(451));
            Assert.That(bundle.SyncRoot.EnumerateLeaves(), Has.Exactly(451).Items);

            var temp = Path.Combine(Path.GetTempPath(), "a3s-pkg-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                await store.WriteAllAsync(temp, new A3SMetadataWriteSet
                {
                    Autoconfig = bundle.Autoconfig,
                    Sync = bundle.SyncRoot,
                    ServerInfo = bundle.ServerInfo,
                    Changelogs = bundle.Changelogs,
                    Events = new A3SEvents()
                });

                var round = await store.ReadServerInfoAsync(temp);
                Assert.That(round.Revision, Is.EqualTo(1));
                Assert.That(round.NumberOfFiles, Is.EqualTo(451));
                Assert.That(File.Exists(Path.Combine(temp, ".a3s", "sync")), Is.True);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void PreferNative_WithFallbackDisabled_DoesNotCallMissingBridge()
        {
            var native = new SerializerA3SMetadataStore();
            var bridge = new BridgeA3SMetadataStore(Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.jar"));
            var prefer = new PreferNativeA3SMetadataStore(
                native, bridge, preferNativeReads: true, fallbackToBridgeOnReadError: false, preferNativeWrites: true);

            Assert.DoesNotThrowAsync(async () =>
            {
                var info = await prefer.ReadServerInfoAsync(ClassicFixtureRoot);
                Assert.That(info.Revision, Is.EqualTo(1));
                Assert.That(prefer.LastReadBackend, Is.EqualTo("serializer"));
            });
        }
    }
}
