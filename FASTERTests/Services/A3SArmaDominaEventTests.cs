using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    /// <summary>
    /// Regression against the live Reichel "Arma Domina" classic events blob:
    /// addonNames keys are workshop folder ids; bool values are isOptional (mostly false).
    /// </summary>
    [TestFixture]
    public class A3SArmaDominaEventTests
    {
        private static string DominaFixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "a3s-arma-domina");

        [Test]
        public async Task Serializer_ReadsArmaDomina_AllRequiredAddonKeys()
        {
            var store = new SerializerA3SMetadataStore();
            var events = await store.ReadEventsAsync(DominaFixtureRoot);

            Assert.That(events.List.Count, Is.EqualTo(1));
            var evt = events.List[0];
            Assert.That(evt.Name, Is.EqualTo("Arma Domina"));
            Assert.That(evt.AddonNames.Count, Is.EqualTo(23));
            Assert.That(evt.SelectedAddonNames.Count, Is.EqualTo(23),
                "SelectedAddonNames must be map keys (membership), not optional=true only.");
            Assert.That(evt.SelectedAddonNames, Does.Contain("450814997")); // CBA_A3
            Assert.That(evt.SelectedAddonNames, Does.Contain("1779063631"));
            // Live Domina marks addons required (isOptional=false).
            Assert.That(evt.OptionalAddonNames.Count, Is.EqualTo(0));
            Assert.That(evt.AddonNames["450814997"], Is.False);
        }

        [Test]
        public async Task ApplyEvent_ArmaDomina_TicksClientAndHcByWorkshopId()
        {
            var store = new SerializerA3SMetadataStore();
            var events = await store.ReadEventsAsync(DominaFixtureRoot);
            var evt = events.List.Single(e => e.Name == "Arma Domina");

            var profile = new ServerProfile("test", false)
            {
                ProfileMods = new List<ProfileMod>
                {
                    new() { Name = "CBA_A3", Id = 450814997 },
                    new() { Name = "ACE", Id = 463939057 },
                    new() { Name = "Zenith", Id = 1779063631 },
                    new() { Name = "Antistasi Ultimate", Id = 3020755032 },
                    new() { Name = "Unrelated", Id = 1 }
                }
            };

            // Fill remaining Domina workshop ids so we can assert a high match rate.
            foreach (var key in evt.SelectedAddonNames)
            {
                if (!uint.TryParse(key, out var id))
                    continue;
                if (profile.ProfileMods.Any(m => m.Id == id))
                    continue;
                profile.ProfileMods.Add(new ProfileMod { Name = $"Mod_{id}", Id = id });
            }

            var service = new A3SEventService(store, new A3SRepositoryService());
            var result = service.ApplyEventToProfile(evt, profile); // defaults: Client+HC on, Server off

            Assert.That(result.EventAddonCount, Is.EqualTo(23));
            Assert.That(result.MatchedMods.Count, Is.EqualTo(23));
            Assert.That(result.MissingAddons, Is.Empty);

            var cba = profile.ProfileMods.Single(m => m.Id == 450814997);
            Assert.That(cba.ClientSideChecked, Is.True);
            Assert.That(cba.HeadlessChecked, Is.True);
            Assert.That(cba.ServerSideChecked, Is.False);

            var unrelated = profile.ProfileMods.Single(m => m.Name == "Unrelated");
            Assert.That(unrelated.ClientSideChecked, Is.False);
            Assert.That(unrelated.HeadlessChecked, Is.False);
        }

        [Test]
        public void FindMatchingProfileMod_NormalizesPathBasenameAtAndWorkshopId()
        {
            var mods = new List<ProfileMod>
            {
                new() { Name = "CBA_A3", Id = 450814997 },
                new() { Name = "ACE3", Id = 463939057 },
                new() { Name = "450814997", Id = 99 } // name-as-id fallback should not steal numeric key match
            };

            Assert.That(
                A3SEventService.FindMatchingProfileMod(mods, "450814997")?.Name,
                Is.EqualTo("CBA_A3"));
            Assert.That(
                A3SEventService.FindMatchingProfileMod(mods, "@CBA_A3")?.Name,
                Is.EqualTo("CBA_A3"));
            Assert.That(
                A3SEventService.FindMatchingProfileMod(mods, "mods/450814997")?.Name,
                Is.EqualTo("CBA_A3"));
            Assert.That(
                A3SEventService.FindMatchingProfileMod(mods, "CBA A3")?.Name,
                Is.EqualTo("CBA_A3"));
            Assert.That(
                A3SEventService.FindMatchingProfileMod(mods, "@ACE3")?.Name,
                Is.EqualTo("ACE3"));
        }

        [Test]
        public void SelectedAddonNames_IncludesRequiredAndOptionalKeys()
        {
            var evt = new A3SEventInfo
            {
                Name = "Mixed",
                AddonNames = new Dictionary<string, bool>
                {
                    ["@ACE"] = false,   // required
                    ["@TFAR"] = true,   // optional
                    ["450814997"] = false
                }
            };

            Assert.That(evt.SelectedAddonNames, Is.EquivalentTo(new[] { "@ACE", "@TFAR", "450814997" }));
            Assert.That(evt.OptionalAddonNames, Is.EquivalentTo(new[] { "@TFAR" }));
        }

        [Test]
        public async Task Serializer_RoundTripsOptionalTrueViaBooleanReferences()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-opt-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SerializerA3SMetadataStore();
                var events = new A3SEvents
                {
                    List =
                    {
                        new A3SEventInfo
                        {
                            Name = "Opts",
                            AddonNames = new Dictionary<string, bool>
                            {
                                ["@req"] = false,
                                ["@opt1"] = true,
                                ["@opt2"] = true
                            }
                        }
                    }
                };

                await store.WriteMetadataAsync(root, "events", events);
                var round = await store.ReadEventsAsync(root);
                Assert.That(round.List[0].SelectedAddonNames.Count, Is.EqualTo(3));
                Assert.That(round.List[0].OptionalAddonNames, Is.EquivalentTo(new[] { "@opt1", "@opt2" }));
                Assert.That(round.List[0].AddonNames["@req"], Is.False);
                Assert.That(round.List[0].AddonNames["@opt1"], Is.True);
                Assert.That(round.List[0].AddonNames["@opt2"], Is.True);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }
}
