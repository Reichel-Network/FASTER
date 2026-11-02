using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>Parsed Arma 3 Launcher HTML preset (export from Mods → Preset → Export).</summary>
    public sealed class A3SArmaLauncherPreset
    {
        public A3SArmaLauncherPreset(string name, IReadOnlyList<ArmaMod> mods)
        {
            Name = name ?? string.Empty;
            Mods = mods ?? Array.Empty<ArmaMod>();
        }

        /// <summary><c>arma:PresetName</c> meta, or file stem when missing.</summary>
        public string Name { get; }

        public IReadOnlyList<ArmaMod> Mods { get; }
    }

    /// <summary>One preset mod after matching against A3S repo addon folder keys.</summary>
    public sealed class A3SPresetModMatch
    {
        public A3SPresetModMatch(string displayName, uint workshopId, bool isLocal, string? matchedAddonKey)
        {
            DisplayName = displayName ?? string.Empty;
            WorkshopId = workshopId;
            IsLocal = isLocal;
            MatchedAddonKey = matchedAddonKey;
        }

        public string DisplayName { get; }
        public uint WorkshopId { get; }
        public bool IsLocal { get; }
        public string? MatchedAddonKey { get; }
        public bool IsMatched => !string.IsNullOrWhiteSpace(MatchedAddonKey);

        public string Summary =>
            WorkshopId > 0 && !IsLocal
                ? $"{DisplayName} ({WorkshopId})"
                : DisplayName;
    }

    /// <summary>Result of mapping a launcher preset onto known A3S addon keys.</summary>
    public sealed class A3SPresetImportResult
    {
        public A3SPresetImportResult(
            string presetName,
            IReadOnlyList<A3SPresetModMatch> matches,
            IReadOnlyDictionary<string, bool> addonNames)
        {
            PresetName = presetName ?? string.Empty;
            Matches = matches ?? Array.Empty<A3SPresetModMatch>();
            AddonNames = addonNames ?? new Dictionary<string, bool>();
        }

        public string PresetName { get; }
        public IReadOnlyList<A3SPresetModMatch> Matches { get; }
        /// <summary>Matched addon keys only (<c>false</c> = required). Ready for <see cref="A3SEventService.UpsertEventAsync"/>.</summary>
        public IReadOnlyDictionary<string, bool> AddonNames { get; }
        public IReadOnlyList<A3SPresetModMatch> Unmatched =>
            Matches.Where(m => !m.IsMatched).ToList();
        public int MatchedCount => Matches.Count(m => m.IsMatched);
    }

    /// <summary>
    /// Parse Arma 3 Launcher HTML presets and match Steam/local mods onto A3S event addon keys
    /// (workshop-id folders or <c>@mod</c> names in the publish/sync tree).
    /// </summary>
    public static class A3SArmaLauncherPresetParser
    {
        /// <summary>Read preset name + mod list from a launcher HTML export.</summary>
        public static A3SArmaLauncherPreset ParseFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Preset file path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Arma 3 Launcher preset file was not found.", filePath);

            var mods = ModUtilities.ParseModsFromArmaProfileFile(filePath);
            var name = ReadPresetName(filePath);
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(filePath);

            return new A3SArmaLauncherPreset(name.Trim(), mods);
        }

        /// <summary>
        /// Map preset mods onto known repo addon folder names.
        /// Matching order per mod:
        /// 1) exact workshop-id folder (<c>450814997</c>);
        /// 2) exact <c>@SafeName(display)</c> / SafeName folder;
        /// 3) exact display name or <c>@DisplayName</c> (ordinal ignore-case);
        /// 4) <see cref="ModUtilities.GetCompareString"/> fuzzy match against folder basename (strip leading <c>@</c>).
        /// Unmatched mods are returned with <see cref="A3SPresetModMatch.MatchedAddonKey"/> null.
        /// </summary>
        public static A3SPresetImportResult MatchToAddonKeys(
            A3SArmaLauncherPreset preset,
            IEnumerable<string> knownAddonKeys)
        {
            ArgumentNullException.ThrowIfNull(preset);
            var known = (knownAddonKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => A3SEventService.NormalizeAddonEventKey(k))
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var byWorkshopId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byExact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byCompare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in known)
            {
                byExact[key] = key;
                var noAt = key.StartsWith("@", StringComparison.Ordinal) ? key[1..] : key;
                byExact[noAt] = key;
                byExact["@" + noAt] = key;

                if (uint.TryParse(noAt, out _))
                    byWorkshopId[noAt] = key;

                var compare = ModUtilities.GetCompareString(noAt);
                if (!string.IsNullOrEmpty(compare) && !byCompare.ContainsKey(compare))
                    byCompare[compare] = key;

                var safe = Functions.SafeName(noAt);
                if (!string.IsNullOrEmpty(safe))
                {
                    byExact[safe] = key;
                    byExact["@" + safe] = key;
                }
            }

            var matches = new List<A3SPresetModMatch>();
            var addonMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in preset.Mods)
            {
                if (mod == null || string.IsNullOrWhiteSpace(mod.Name))
                    continue;

                var matched = TryMatchMod(mod, byWorkshopId, byExact, byCompare);
                matches.Add(new A3SPresetModMatch(mod.Name, mod.WorkshopId, mod.IsLocal, matched));
                if (!string.IsNullOrWhiteSpace(matched) && !addonMap.ContainsKey(matched))
                    addonMap[matched] = false; // required
            }

            return new A3SPresetImportResult(preset.Name, matches, addonMap);
        }

        private static string? TryMatchMod(
            ArmaMod mod,
            IReadOnlyDictionary<string, string> byWorkshopId,
            IReadOnlyDictionary<string, string> byExact,
            IReadOnlyDictionary<string, string> byCompare)
        {
            if (!mod.IsLocal && mod.WorkshopId > 0
                && byWorkshopId.TryGetValue(mod.WorkshopId.ToString(), out var byId))
                return byId;

            var name = mod.Name.Trim();
            var noAt = name.StartsWith("@", StringComparison.Ordinal) ? name[1..] : name;

            if (byExact.TryGetValue(name, out var exact1))
                return exact1;
            if (byExact.TryGetValue(noAt, out var exact2))
                return exact2;
            if (byExact.TryGetValue("@" + noAt, out var exact3))
                return exact3;

            var safe = Functions.SafeName(noAt);
            if (!string.IsNullOrEmpty(safe))
            {
                if (byExact.TryGetValue(safe, out var safe1))
                    return safe1;
                if (byExact.TryGetValue("@" + safe, out var safe2))
                    return safe2;
            }

            var compare = ModUtilities.GetCompareString(noAt);
            if (!string.IsNullOrEmpty(compare) && byCompare.TryGetValue(compare, out var fuzzy))
                return fuzzy;

            return null;
        }

        private static string ReadPresetName(string filePath)
        {
            try
            {
                var xml = File.ReadAllText(filePath);
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var meta = doc.SelectSingleNode("//meta[@name='arma:PresetName']")
                    ?? doc.SelectSingleNode("//meta[translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='arma:presetname']");
                var content = meta?.Attributes?["content"]?.Value;
                if (!string.IsNullOrWhiteSpace(content))
                    return content.Trim();

                // Fallback: <h1>…Preset <strong>Name</strong>
                var strong = doc.SelectSingleNode("//h1/strong");
                if (!string.IsNullOrWhiteSpace(strong?.InnerText))
                    return strong!.InnerText.Trim();
            }
            catch
            {
                // ParseModsFromArmaProfileFile already validated XML shape for mods.
            }

            return string.Empty;
        }
    }
}
