using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTERTests
{
    [TestFixture]
    public class A3SPhase4bWriteTests
    {
        private static string ClassicFixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "a3s-classic");

        [Test]
        public async Task Serializer_RoundTrip_AllKinds_FromGoldenFixture()
        {
            var store = new SerializerA3SMetadataStore();
            var bundle = await store.ReadAllAsync(ClassicFixtureRoot);
            var events = await store.ReadEventsAsync(ClassicFixtureRoot);

            var temp = Path.Combine(Path.GetTempPath(), "a3s-phase4b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                await store.WriteAllAsync(temp, new A3SMetadataWriteSet
                {
                    Autoconfig = bundle.Autoconfig,
                    Sync = bundle.SyncRoot,
                    ServerInfo = bundle.ServerInfo,
                    Changelogs = bundle.Changelogs,
                    Events = events
                });

                var round = await store.ReadAllAsync(temp);
                var roundEvents = await store.ReadEventsAsync(temp);

                Assert.That(round.ServerInfo.Revision, Is.EqualTo(bundle.ServerInfo.Revision));
                Assert.That(round.ServerInfo.NumberOfFiles, Is.EqualTo(bundle.ServerInfo.NumberOfFiles));
                Assert.That(round.ServerInfo.TotalFilesSize, Is.EqualTo(bundle.ServerInfo.TotalFilesSize));
                Assert.That(round.ServerInfo.NumberOfConnections, Is.EqualTo(bundle.ServerInfo.NumberOfConnections));
                Assert.That(round.Autoconfig.RepositoryName, Is.EqualTo(bundle.Autoconfig.RepositoryName));
                Assert.That(round.Autoconfig.Protocol.Type, Is.EqualTo(bundle.Autoconfig.Protocol.Type));
                Assert.That(round.Autoconfig.Protocol.Url, Is.EqualTo(bundle.Autoconfig.Protocol.Url));
                Assert.That(round.Autoconfig.FavoriteServers.Count, Is.EqualTo(bundle.Autoconfig.FavoriteServers.Count));
                Assert.That(round.SyncRoot.EnumerateLeaves().Count(), Is.EqualTo(bundle.SyncRoot.EnumerateLeaves().Count()));
                Assert.That(round.Changelogs?.List.Count, Is.EqualTo(bundle.Changelogs?.List.Count));
                Assert.That(roundEvents.List.Count, Is.EqualTo(events.List.Count));

                var authors = round.SyncRoot.EnumerateLeaves()
                    .First(l => l.Name.Equals("AUTHORS.txt", StringComparison.OrdinalIgnoreCase));
                Assert.That(authors.Sha1, Is.EqualTo("ad920f24340ee9586686f50c8db62ab28aaf9c7a"));
                Assert.That(authors.Size, Is.EqualTo(830));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task Serializer_Write_Events_WithAddonMap_RoundTrip()
        {
            var store = new SerializerA3SMetadataStore();
            var temp = Path.Combine(Path.GetTempPath(), "a3s-phase4b-ev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var events = new A3SEvents
                {
                    List =
                    {
                        new A3SEventInfo
                        {
                            Name = "Weekend",
                            Description = "ops",
                            AddonNames = new Dictionary<string, bool>
                            {
                                ["@ace"] = true,
                                ["@cba"] = false
                            }
                        }
                    }
                };

                await store.WriteMetadataAsync(temp, "events", events);
                var round = await store.ReadEventsAsync(temp);
                Assert.That(round.List.Count, Is.EqualTo(1));
                Assert.That(round.List[0].Name, Is.EqualTo("Weekend"));
                Assert.That(round.List[0].AddonNames["@ace"], Is.True);
                Assert.That(round.List[0].AddonNames["@cba"], Is.False);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task PreferNative_Write_UsesSerializer()
        {
            var native = new SerializerA3SMetadataStore();
            var bridge = new BridgeA3SMetadataStore(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jar"));
            var prefer = new PreferNativeA3SMetadataStore(
                native, bridge, preferNativeReads: true, fallbackToBridgeOnReadError: false, preferNativeWrites: true);

            var temp = Path.Combine(Path.GetTempPath(), "a3s-phase4b-pref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                await prefer.WriteAllAsync(temp, new A3SMetadataWriteSet
                {
                    ServerInfo = new A3SServerInfo
                    {
                        Revision = 9,
                        NumberOfFiles = 3,
                        TotalFilesSize = 42,
                        NumberOfConnections = 2,
                        BuildDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                    }
                });

                Assert.That(prefer.LastWriteBackend, Is.EqualTo("serializer"));
                var info = await prefer.ReadServerInfoAsync(temp);
                Assert.That(info.Revision, Is.EqualTo(9));
                Assert.That(info.NumberOfFiles, Is.EqualTo(3));
                Assert.That(prefer.LastReadBackend, Is.EqualTo("serializer"));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
