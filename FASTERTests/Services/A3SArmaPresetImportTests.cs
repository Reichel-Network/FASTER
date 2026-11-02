using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SArmaPresetImportTests
    {
        private static string FixturePath(string fileName) =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "Fixtures",
                fileName);

        [Test]
        public void ParseFile_ReadsAntiStasiPresetNameAndWorkshopMods()
        {
            var path = FixturePath("Arma_3_Preset_AntiStasi.html");
            Assert.That(File.Exists(path), Is.True, "Fixture missing: " + path);

            var preset = A3SArmaLauncherPresetParser.ParseFile(path);
            Assert.That(preset.Name, Is.EqualTo("AntiStasi"));
            Assert.That(preset.Mods.Count, Is.EqualTo(47));
            Assert.That(preset.Mods.All(m => !m.IsLocal), Is.True);

            var cba = preset.Mods.FirstOrDefault(m => m.Name == "CBA_A3");
            Assert.That(cba, Is.Not.Null);
            Assert.That(cba!.WorkshopId, Is.EqualTo(450814997u));

            var antistasi = preset.Mods.FirstOrDefault(m => m.Name == "Antistasi Ultimate - Mod");
            Assert.That(antistasi, Is.Not.Null);
            Assert.That(antistasi!.WorkshopId, Is.EqualTo(3020755032u));
        }

        [Test]
        public void MatchToAddonKeys_PrefersWorkshopIdThenAtNameThenFuzzy()
        {
            var preset = new A3SArmaLauncherPreset(
                "AntiStasi",
                new List<ArmaMod>
                {
                    new() { Name = "CBA_A3", WorkshopId = 450814997, IsLocal = false },
                    new() { Name = "ace", WorkshopId = 463939057, IsLocal = false },
                    new() { Name = "Enhanced Movement", WorkshopId = 333310405, IsLocal = false },
                    new() { Name = "Missing Mod", WorkshopId = 111, IsLocal = false },
                });

            var known = new[]
            {
                "450814997",          // workshop folder for CBA
                "@ace",               // @name for ace (compare/safe)
                "@Enhanced_Movement", // SafeName of display
                "@other"
            };

            var result = A3SArmaLauncherPresetParser.MatchToAddonKeys(preset, known);

            Assert.That(result.PresetName, Is.EqualTo("AntiStasi"));
            Assert.That(result.MatchedCount, Is.EqualTo(3));
            Assert.That(result.Unmatched.Count, Is.EqualTo(1));
            Assert.That(result.Unmatched[0].DisplayName, Is.EqualTo("Missing Mod"));
            Assert.That(result.Unmatched[0].Summary, Does.Contain("111"));

            Assert.That(result.AddonNames.Keys, Is.EquivalentTo(new[]
            {
                "450814997",
                "@ace",
                "@Enhanced_Movement"
            }));
            Assert.That(result.AddonNames.Values.All(v => v == false), Is.True);
        }

        [Test]
        public async Task ImportLauncherPreset_UpsertsEventWithMatchedKeysOnly()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-preset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "450814997", "addons"));
            Directory.CreateDirectory(Path.Combine(root, "@ace", "addons"));
            Directory.CreateDirectory(Path.Combine(root, "463939057", "addons")); // duplicate style — prefer exact id when present
            await File.WriteAllBytesAsync(Path.Combine(root, "450814997", "addons", "cba.pbo"), Encoding.UTF8.GetBytes("cba"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@ace", "addons", "ace.pbo"), Encoding.UTF8.GetBytes("ace"));
            await File.WriteAllBytesAsync(Path.Combine(root, "463939057", "addons", "ace2.pbo"), Encoding.UTF8.GetBytes("ace2"));

            // Tiny HTML preset (2 steam mods + 1 missing)
            var html = Path.Combine(root, "preset.html");
            await File.WriteAllTextAsync(html, """
                <?xml version="1.0" encoding="utf-8"?>
                <html>
                  <head>
                    <meta name="arma:Type" content="preset" />
                    <meta name="arma:PresetName" content="AntiStasi" />
                  </head>
                  <body>
                    <div class="mod-list">
                      <table>
                        <tr data-type="ModContainer">
                          <td data-type="DisplayName">CBA_A3</td>
                          <td><span class="from-steam">Steam</span></td>
                          <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=450814997" data-type="Link">x</a></td>
                        </tr>
                        <tr data-type="ModContainer">
                          <td data-type="DisplayName">ace</td>
                          <td><span class="from-steam">Steam</span></td>
                          <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=463939057" data-type="Link">x</a></td>
                        </tr>
                        <tr data-type="ModContainer">
                          <td data-type="DisplayName">Ghost Mod</td>
                          <td><span class="from-steam">Steam</span></td>
                          <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=999999" data-type="Link">x</a></td>
                        </tr>
                      </table>
                    </div>
                  </body>
                </html>
                """);

            var store = new SerializerA3SMetadataStore();
            var service = new A3SEventService(store, new A3SRepositoryService());
            var repo = new A3SRepository("preset-repo", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root
            };

            try
            {
                var import = await service.ImportLauncherPresetAsync(repo, html);
                Assert.That(import.PresetName, Is.EqualTo("AntiStasi"));
                Assert.That(import.MatchedCount, Is.EqualTo(2));
                Assert.That(import.Unmatched.Select(u => u.DisplayName), Is.EquivalentTo(new[] { "Ghost Mod" }));
                Assert.That(import.AddonNames.ContainsKey("450814997"), Is.True);
                Assert.That(import.AddonNames.ContainsKey("463939057"), Is.True); // workshop id wins over @ace

                var saved = await service.UpsertEventAsync(
                    repo,
                    import.PresetName,
                    "from preset",
                    import.AddonNames);

                Assert.That(File.Exists(Path.Combine(root, ".a3s", "events")), Is.True);
                var listed = await service.ListEventsAsync(repo);
                Assert.That(listed.Count, Is.EqualTo(1));
                Assert.That(listed[0].Name, Is.EqualTo("AntiStasi"));
                Assert.That(listed[0].AddonNames.Keys, Is.EquivalentTo(saved.AddonNames.Keys));
                Assert.That(listed[0].AddonNames.ContainsKey("999999"), Is.False);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }
}
