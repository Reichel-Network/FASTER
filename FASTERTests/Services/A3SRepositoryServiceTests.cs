using System;
using System.IO;
using System.Linq;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SRepositoryServiceTests
    {
        [Test]
        public void ImportByAutoconfigUrl_RequiresNameAndUrl()
        {
            var service = new A3SRepositoryService();
            Assert.Throws<ArgumentException>(() => service.ImportByAutoconfigUrl("", "https://example/.a3s/autoconfig"));
            Assert.Throws<ArgumentException>(() => service.ImportByAutoconfigUrl("Name", ""));
        }

        [Test]
        public void ImportAddRemove_PersistsAgainstSettingsCollection()
        {
            var service = new A3SRepositoryService();
            var before = service.List().Count;

            var repo = service.ImportByAutoconfigUrl(
                $"Phase0Test-{Guid.NewGuid():N}".Substring(0, 20),
                "https://example.test/phase0/.a3s/autoconfig");

            Assert.That(service.List().Any(r => r.Id == repo.Id), Is.True);
            Assert.That(service.List().Count, Is.EqualTo(before + 1));
            Assert.That(repo.AutoconfigUrl, Does.Contain(".a3s/autoconfig"));

            Assert.That(service.Remove(repo.Id), Is.True);
            Assert.That(service.List().Any(r => r.Id == repo.Id), Is.False);
            Assert.That(service.List().Count, Is.EqualTo(before));
        }

        [Test]
        public void BridgeA3SMetadataStore_MissingJar_ThrowsGracefully()
        {
            IA3SMetadataStore store = new BridgeA3SMetadataStore(
                Path.Combine(Path.GetTempPath(), $"no-bridge-{Guid.NewGuid():N}.jar"));
            Assert.ThrowsAsync<A3SBridgeNotFoundException>(async () =>
                await store.ReadAllAsync("https://example/.a3s/autoconfig"));
            Assert.ThrowsAsync<NotImplementedException>(async () =>
                await store.WriteMetadataAsync("/tmp", new object()));
        }
    }
}
