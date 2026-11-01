using System;
using System.Collections.Generic;

namespace FASTER.Models
{
    /// <summary>
    /// Arma 3 dedicated-server inbound UDP ports relative to the profile <c>-port</c> value
    /// (BI wiki: game, Steam query +1, Steam +2, VON reserved +3, BattlEye +4).
    /// </summary>
    public static class ArmaServerPorts
    {
        public const int PortSpan = 5; // game .. game+4 inclusive

        /// <summary>
        /// Returns the five inbound UDP ports for <paramref name="gamePort"/> (game through BattlEye).
        /// </summary>
        public static IReadOnlyList<int> GetInboundUdpPorts(int gamePort)
        {
            if (gamePort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(gamePort), gamePort, "Game port must be between 1 and 65535.");

            if (gamePort > 65535 - (PortSpan - 1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gamePort),
                    gamePort,
                    $"Game port {gamePort} leaves no room for Arma ports +1..+4 (need up to {gamePort + PortSpan - 1}).");
            }

            return new[]
            {
                gamePort,     // game + VON
                gamePort + 1, // Steam query
                gamePort + 2, // Steam master
                gamePort + 3, // VON reserved
                gamePort + 4  // BattlEye
            };
        }

        /// <summary>
        /// Stable Windows Firewall rule name: <c>FASTER-{safeProfileName}-{port}</c>.
        /// </summary>
        public static string BuildFirewallRuleName(string profileName, int port)
        {
            var safe = SanitizeProfileNameForFirewall(profileName);
            return $"FASTER-{safe}-{port}";
        }

        /// <summary>
        /// Marker stored in the firewall rule description so rename/port-change can find prior rules.
        /// </summary>
        public static string BuildFirewallDescription(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile id is required.", nameof(profileId));
            return $"FASTER:{profileId.Trim()}";
        }

        public static bool TryParseFirewallDescription(string? description, out string profileId)
        {
            profileId = string.Empty;
            if (string.IsNullOrWhiteSpace(description))
                return false;
            const string prefix = "FASTER:";
            if (!description.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            profileId = description[prefix.Length..].Trim();
            return profileId.Length > 0;
        }

        public static string SanitizeProfileNameForFirewall(string? profileName)
        {
            var raw = string.IsNullOrWhiteSpace(profileName) ? "Profile" : profileName.Trim();
            // Keep letters, digits, space, dash, underscore; collapse other runs to underscore.
            var safe = Functions.SafeName(raw, ignoreWhiteSpace: true, replacement: "_");
            safe = safe.Trim().Trim('_');
            if (string.IsNullOrWhiteSpace(safe))
                safe = "Profile";
            // Firewall UI / netsh are happier with shorter names.
            const int maxLen = 64;
            if (safe.Length > maxLen)
                safe = safe[..maxLen].TrimEnd('_', ' ');
            return safe;
        }
    }
}
