using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using FASTER.Models;

namespace FASTER.Services
{
    /// <summary>
    /// Idempotent inbound Windows Firewall rules for an Arma server profile's UDP ports.
    /// Requires elevation (FASTER already runs as Administrator).
    /// </summary>
    public static class WindowsFirewallProfileRules
    {
        private const int NetFwIpProtocolUdp = 17;
        private const int NetFwRuleDirIn = 1;
        private const int NetFwActionAllow = 1;
        private const int NetFwProfile2All = 0x7FFFFFFF;

        /// <summary>
        /// Removes any prior rules for this profile id, then creates one inbound UDP allow
        /// rule per Arma port named <c>FASTER-{ProfileName}-{port}</c>.
        /// </summary>
        public static void EnsureForProfile(ServerProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.Id))
                throw new InvalidOperationException("Cannot update firewall rules: profile id is empty.");

            var ports = ArmaServerPorts.GetInboundUdpPorts(profile.Port <= 0 ? 2302 : profile.Port);
            EnsureForProfile(profile.Id, profile.Name ?? string.Empty, ports);
        }

        public static void EnsureForProfile(string profileId, string profileName, IReadOnlyList<int> udpPorts)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile id is required.", nameof(profileId));
            ArgumentNullException.ThrowIfNull(udpPorts);
            if (udpPorts.Count == 0)
                throw new ArgumentException("At least one UDP port is required.", nameof(udpPorts));

            try
            {
                var policy = CreatePolicy();
                RemoveRulesForProfile(policy, profileId);

                var description = ArmaServerPorts.BuildFirewallDescription(profileId);
                foreach (var port in udpPorts)
                {
                    var name = ArmaServerPorts.BuildFirewallRuleName(profileName, port);
                    // Avoid rare name collisions with unrelated rules: drop same name first.
                    TryRemoveRuleByName(policy, name);
                    AddInboundUdpAllow(policy, name, description, port);
                }
            }
            catch (Exception ex) when (ex is not ArgumentException and not ArgumentOutOfRangeException and not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Windows Firewall update failed for profile \"{profileName}\" (id {profileId}): {ex.Message}",
                    ex);
            }
        }

        /// <summary>
        /// Removes all inbound rules previously created for this profile id.
        /// </summary>
        public static void RemoveForProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            try
            {
                var policy = CreatePolicy();
                RemoveRulesForProfile(policy, profileId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Windows Firewall rule removal failed for profile id {profileId}: {ex.Message}",
                    ex);
            }
        }

        public static void RemoveForProfile(ServerProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            RemoveForProfile(profile.Id);
        }

        private static object CreatePolicy()
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                       ?? throw new InvalidOperationException(
                           "Windows Firewall COM API (HNetCfg.FwPolicy2) is unavailable on this system.");
            return Activator.CreateInstance(type)
                   ?? throw new InvalidOperationException("Failed to create HNetCfg.FwPolicy2 instance.");
        }

        private static void RemoveRulesForProfile(object policy, string profileId)
        {
            var marker = ArmaServerPorts.BuildFirewallDescription(profileId);
            var rules = ((dynamic)policy).Rules;
            var toRemove = new List<string>();

            foreach (var ruleObj in rules)
            {
                try
                {
                    string? description = ruleObj.Description;
                    if (string.Equals(description, marker, StringComparison.Ordinal))
                        toRemove.Add((string)ruleObj.Name);
                }
                catch (COMException)
                {
                    // Skip rules that cannot be inspected.
                }
            }

            foreach (var name in toRemove)
                TryRemoveRuleByName(policy, name);
        }

        private static void TryRemoveRuleByName(object policy, string name)
        {
            try
            {
                ((dynamic)policy).Rules.Remove(name);
            }
            catch (COMException)
            {
                // Rule absent or locked — ignore for idempotent ensure.
            }
            catch (ArgumentException)
            {
                // Some hosts throw ArgumentException when the name is missing.
            }
        }

        private static void AddInboundUdpAllow(object policy, string name, string description, int port)
        {
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                           ?? throw new InvalidOperationException(
                               "Windows Firewall COM API (HNetCfg.FWRule) is unavailable on this system.");
            dynamic rule = Activator.CreateInstance(ruleType)
                           ?? throw new InvalidOperationException("Failed to create HNetCfg.FWRule instance.");

            rule.Name = name;
            rule.Description = description;
            rule.Grouping = "FASTER";
            rule.Protocol = NetFwIpProtocolUdp;
            rule.LocalPorts = port.ToString();
            rule.Direction = NetFwRuleDirIn;
            rule.Action = NetFwActionAllow;
            rule.Enabled = true;
            rule.Profiles = NetFwProfile2All;

            try
            {
                ((dynamic)policy).Rules.Add(rule);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to add firewall rule \"{name}\" for UDP {port}: {ex.Message} (0x{ex.ErrorCode:X8}). " +
                    "Confirm FASTER is running as Administrator and Windows Firewall is available.",
                    ex);
            }
        }
    }
}
