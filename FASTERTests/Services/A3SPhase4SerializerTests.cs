using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTERTests
{
    [TestFixture]
    public class A3SPhase4SerializerTests
    {
        private static string ClassicFixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "a3s-classic");

        private static string ClassicA3sDir => Path.Combine(ClassicFixtureRoot, ".a3s");

        [Test]
        public async Task Serializer_Reads_ServerInfo_FromGoldenFixture()
        {
            var store = new SerializerA3SMetadataStore();
            var info = await store.ReadServerInfoAsync(ClassicFixtureRoot);

            Assert.That(info.Revision, Is.EqualTo(1));
            Assert.That(info.NumberOfFiles, Is.EqualTo(451));
            Assert.That(info.TotalFilesSize, Is.EqualTo(8266615271L));
            Assert.That(info.NumberOfConnections, Is.EqualTo(10));
            Assert.That(info.CompressedPboFilesOnly, Is.False);
            Assert.That(info.BuildDate, Is.Not.Null);
            Assert.That(info.BuildDate!.Value.Year, Is.EqualTo(2026));
        }

        [Test]
        public async Task Serializer_Reads_Autoconfig_FromGoldenFixture()
        {
            var store = new SerializerA3SMetadataStore();
            var autoconfig = await store.ReadAutoconfigAsync(ClassicA3sDir);

            Assert.That(autoconfig.RepositoryName, Is.EqualTo("reichel-arma"));
            Assert.That(autoconfig.Protocol.Type, Is.EqualTo("HTTPS"));
            Assert.That(autoconfig.Protocol.Url, Is.EqualTo("arma.game.reichel.network"));
            Assert.That(autoconfig.Protocol.Port, Is.EqualTo("443"));
            Assert.That(autoconfig.Protocol.Login, Is.EqualTo("anonymous"));
            Assert.That(autoconfig.Protocol.ValidateSslCertificate, Is.True);
            Assert.That(autoconfig.FavoriteServers.Count, Is.EqualTo(1));
            Assert.That(autoconfig.FavoriteServers[0].Name, Is.EqualTo("ARMA Domina - NEU"));
            Assert.That(autoconfig.FavoriteServers[0].Port, Is.EqualTo(2302));
        }

        [Test]
        public async Task Serializer_Reads_SyncTree_LeafCount_MatchesServerInfo()
        {
            var store = new SerializerA3SMetadataStore();
            var sync = await store.ReadSyncTreeAsync(ClassicFixtureRoot);
            var serverInfo = await store.ReadServerInfoAsync(ClassicFixtureRoot);

            Assert.That(sync.Name, Is.EqualTo("racine").IgnoreCase);
            Assert.That(sync.IsDirectory, Is.True);
            var leaves = sync.EnumerateLeaves().ToList();
            Assert.That(leaves.Count, Is.EqualTo(451));
            Assert.That(leaves.Count, Is.EqualTo(serverInfo.NumberOfFiles));

            var firstAddon = sync.Children.FirstOrDefault(c => c.MarkAsAddon);
            Assert.That(firstAddon, Is.Not.Null);
            Assert.That(firstAddon!.Name, Is.EqualTo("1779063631"));

            var authors = leaves.FirstOrDefault(l =>
                l.Name.Equals("AUTHORS.txt", StringComparison.OrdinalIgnoreCase));
            Assert.That(authors, Is.Not.Null);
            Assert.That(authors!.Sha1, Is.EqualTo("ad920f24340ee9586686f50c8db62ab28aaf9c7a"));
            Assert.That(authors.Size, Is.EqualTo(830));
        }

        [Test]
        public async Task Serializer_Reads_Changelogs_And_EmptyEvents()
        {
            var store = new SerializerA3SMetadataStore();
            var changelogs = await store.ReadChangelogsAsync(ClassicFixtureRoot);
            var events = await store.ReadEventsAsync(ClassicFixtureRoot);

            Assert.That(changelogs.List.Count, Is.EqualTo(1));
            Assert.That(changelogs.List[0].Revision, Is.EqualTo(1));
            Assert.That(changelogs.List[0].ContentUpdated, Is.True);
            Assert.That(changelogs.List[0].NewAddons.Count, Is.EqualTo(23));
            Assert.That(changelogs.List[0].Addons.Count, Is.EqualTo(23));
            Assert.That(events.List, Is.Empty);
        }

        [Test]
        public async Task Serializer_ReadAll_BundlesCoreKinds()
        {
            var store = new SerializerA3SMetadataStore();
            var bundle = await store.ReadAllAsync(ClassicFixtureRoot);

            Assert.That(bundle.Autoconfig.RepositoryName, Is.EqualTo("reichel-arma"));
            Assert.That(bundle.ServerInfo.Revision, Is.EqualTo(1));
            Assert.That(bundle.SyncRoot.EnumerateLeaves().Count(), Is.EqualTo(451));
            Assert.That(bundle.Changelogs, Is.Not.Null);
            Assert.That(bundle.Changelogs!.List.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Serializer_ReadSingleFile_Source()
        {
            var store = new SerializerA3SMetadataStore();
            var info = await store.ReadServerInfoAsync(Path.Combine(ClassicA3sDir, "serverinfo"));
            Assert.That(info.Revision, Is.EqualTo(1));
        }

        [Test]
        public void Serializer_Write_NotSupported()
        {
            var store = new SerializerA3SMetadataStore();
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await store.WriteAllAsync(ClassicFixtureRoot, new A3SMetadataWriteSet
                {
                    ServerInfo = new A3SServerInfo { Revision = 2 }
                }));
        }

        [Test]
        public async Task PreferNative_UsesSerializer_ForLocalReads()
        {
            var native = new SerializerA3SMetadataStore();
            var bridge = new BridgeA3SMetadataStore(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jar"));
            var prefer = new PreferNativeA3SMetadataStore(native, bridge, preferNativeReads: true, fallbackToBridgeOnReadError: false);

            var info = await prefer.ReadServerInfoAsync(ClassicFixtureRoot);
            Assert.That(info.Revision, Is.EqualTo(1));
            Assert.That(prefer.LastReadBackend, Is.EqualTo("serializer"));
        }

        [Test]
        public void PreferNative_Write_DelegatesToBridge_MissingJar()
        {
            var native = new SerializerA3SMetadataStore();
            var bridge = new BridgeA3SMetadataStore(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jar"));
            var prefer = new PreferNativeA3SMetadataStore(native, bridge);

            Assert.ThrowsAsync<A3SBridgeNotFoundException>(async () =>
                await prefer.WriteAllAsync(ClassicFixtureRoot, new A3SMetadataWriteSet
                {
                    ServerInfo = new A3SServerInfo { Revision = 2 }
                }));
        }

        [Test]
        public void NormalizeRemoteA3sBase_StripsAutoconfig()
        {
            var result = SerializerA3SMetadataStore.NormalizeRemoteA3sBase(
                "https://arma.game.reichel.network/.a3s/autoconfig");
            Assert.That(result, Is.EqualTo("https://arma.game.reichel.network/.a3s/"));
        }
    }
}
