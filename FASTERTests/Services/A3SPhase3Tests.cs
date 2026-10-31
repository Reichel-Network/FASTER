using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SPhase3Tests
    {
        private static string FixturePath(string name)
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name);

        [Test]
        public async Task JsonStore_ReadsEventsFromFixture()
        {
            var store = new JsonA3SMetadataStore(FixturePath("a3s-tiny-all.json"));
            var events = await store.ReadEventsAsync("ignored");

            Assert.That(events.List.Count, Is.EqualTo(2));
            Assert.That(events.List[0].Name, Is.EqualTo("Weekend ops"));
            Assert.That(events.List[0].SelectedAddonNames, Does.Contain("1779063631"));
            Assert.That(events.List[0].SelectedAddonNames, Does.Contain("@ACE"));
            Assert.That(events.List[1].Name, Is.EqualTo("Training"));
        }

        [Test]
        public async Task JsonStore_ReadAll_IncludesEventsAndFavoriteServers()
        {
            var store = new JsonA3SMetadataStore(FixturePath("a3s-tiny-all.json"));
            var bundle = await store.ReadAllAsync("ignored");

            Assert.That(bundle.Events, Is.Not.Null);
            Assert.That(bundle.Events!.List.Count, Is.EqualTo(2));
            Assert.That(bundle.Autoconfig.FavoriteServers, Is.Not.Null);
        }

        [Test]
        public async Task EventService_ListEvents_FromFixture()
        {
            var store = new JsonA3SMetadataStore(FixturePath("a3s-tiny-all.json"));
            var service = new A3SEventService(store, new A3SRepositoryService());
            var repo = new A3SRepository("test", "https://example.test/.a3s/autoconfig")
            {
                LocalPath = Path.GetTempPath()
            };

            var list = await service.ListEventsAsync(repo);
            Assert.That(list.Select(e => e.Name), Does.Contain("Weekend ops"));
            Assert.That(list.Select(e => e.Name), Does.Contain("Training"));
        }

        [Test]
        public void FindMatchingProfileMod_MatchesNameAtPrefixWorkshopIdAndSafeName()
        {
            var mods = new List<ProfileMod>
            {
                new() { Name = "ACE", Id = 1 },
                new() { Name = "CBA_A3", Id = 450814997 },
                new() { Name = "Zenith", Id = 1779063631 }
            };

            Assert.That(A3SEventService.FindMatchingProfileMod(mods, "@ACE")?.Name, Is.EqualTo("ACE"));
            Assert.That(A3SEventService.FindMatchingProfileMod(mods, "CBA A3")?.Name, Is.EqualTo("CBA_A3"));
            Assert.That(A3SEventService.FindMatchingProfileMod(mods, "1779063631")?.Name, Is.EqualTo("Zenith"));
            Assert.That(A3SEventService.FindMatchingProfileMod(mods, "@missing"), Is.Null);
        }

        [Test]
        public void ApplyEventToProfile_MapsClientServerHcAndReportsMissing()
        {
            var profile = new ServerProfile("Ops", false)
            {
                ProfileMods = new List<ProfileMod>
                {
                    new() { Name = "ACE", Id = 1 },
                    new() { Name = "CBA", Id = 2, ClientSideChecked = true },
                    new() { Name = "Other", Id = 3, ServerSideChecked = true, HeadlessChecked = true }
                }
            };

            var evt = new A3SEventInfo
            {
                Name = "Weekend ops",
                AddonNames = new Dictionary<string, bool>
                {
                    ["@ACE"] = true,
                    ["@Missing"] = true,
                    ["CBA"] = true
                },
                DlcNames = new Dictionary<string, bool> { ["gm"] = true }
            };

            var service = new A3SEventService(
                new JsonA3SMetadataStore(FixturePath("a3s-tiny-all.json")),
                new A3SRepositoryService());

            var result = service.ApplyEventToProfile(evt, profile, new A3SEventApplyOptions
            {
                ApplyClient = true,
                ApplyServer = true,
                ApplyHeadless = true,
                ClearExistingOnTargetLists = true
            });

            Assert.That(result.MatchedMods.Count, Is.EqualTo(2));
            Assert.That(result.MissingAddons, Does.Contain("@Missing"));
            Assert.That(profile.ProfileMods.First(m => m.Name == "ACE").ClientSideChecked, Is.True);
            Assert.That(profile.ProfileMods.First(m => m.Name == "ACE").ServerSideChecked, Is.True);
            Assert.That(profile.ProfileMods.First(m => m.Name == "ACE").HeadlessChecked, Is.True);
            Assert.That(profile.ProfileMods.First(m => m.Name == "Other").ClientSideChecked, Is.False);
            Assert.That(profile.ProfileMods.First(m => m.Name == "Other").ServerSideChecked, Is.False);
            Assert.That(profile.GMDLCChecked, Is.True);
            Assert.That(profile.A3SEventName, Is.EqualTo("Weekend ops"));
        }

        [Test]
        public void BuildFavoriteFromProfile_UsesHostnamePortPasswordAndPublicIp()
        {
            var profile = new ServerProfile("Box", false)
            {
                Port = 2402,
                A3SPublicIp = "203.0.113.10",
                A3SEventName = "Weekend ops"
            };
            profile.ServerCfg.Hostname = "Reichel Ops";
            profile.ServerCfg.Password = "secret";

            var fav = A3SEventService.BuildFavoriteFromProfile(profile, "reichel-arma");
            Assert.That(fav.Name, Is.EqualTo("Reichel Ops"));
            Assert.That(fav.IpAddress, Is.EqualTo("203.0.113.10"));
            Assert.That(fav.Port, Is.EqualTo(2402));
            Assert.That(fav.Password, Is.EqualTo("secret"));
            Assert.That(fav.ModsetName, Is.EqualTo("Weekend ops"));
            Assert.That(fav.RepositoryName, Is.EqualTo("reichel-arma"));
        }

        [Test]
        public async Task WriteFavoriteServer_UpsertsAutoconfigViaJsonStore()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-fav-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new JsonA3SMetadataStore(FixturePath("a3s-tiny-all.json"), root);
                var service = new A3SEventService(store, new A3SRepositoryService());
                var repo = new A3SRepository("reichel", "https://example.test/.a3s/autoconfig")
                {
                    LocalPath = root,
                    PublishRoot = root
                };
                var profile = new ServerProfile("Box", false)
                {
                    Port = 2302,
                    A3SPublicIp = "198.51.100.7",
                    A3SEventName = "Training"
                };
                profile.ServerCfg.Hostname = "Test Server";
                profile.ServerCfg.Password = "pw";

                var fav = await service.WriteFavoriteServerFromProfileAsync(repo, profile, root);
                Assert.That(fav.IpAddress, Is.EqualTo("198.51.100.7"));

                var written = await store.ReadAutoconfigAsync(root);
                Assert.That(written.FavoriteServers.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(written.FavoriteServers.Any(f =>
                    f.Name == "Test Server" && f.IpAddress == "198.51.100.7" && f.Port == 2302), Is.True);

                // Upsert same profile again — still one logical entry for this name/ip.
                profile.ServerCfg.Password = "pw2";
                await service.WriteFavoriteServerFromProfileAsync(repo, profile, root);
                written = await store.ReadAutoconfigAsync(root);
                var matches = written.FavoriteServers.Where(f =>
                    f.Name == "Test Server" && f.IpAddress == "198.51.100.7").ToList();
                Assert.That(matches.Count, Is.EqualTo(1));
                Assert.That(matches[0].Password, Is.EqualTo("pw2"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void A3SEventRef_RoundTripsRepositoryId()
        {
            var link = new A3SEventRef("Weekend ops", "profile-1", "repo-1")
            {
                Description = "desc"
            };
            Assert.That(link.Name, Is.EqualTo("Weekend ops"));
            Assert.That(link.ProfileId, Is.EqualTo("profile-1"));
            Assert.That(link.RepositoryId, Is.EqualTo("repo-1"));
            Assert.That(link.Description, Is.EqualTo("desc"));
        }

    }
}
