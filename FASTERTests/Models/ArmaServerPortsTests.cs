using System;
using System.Linq;

using FASTER.Models;

using NUnit.Framework;

namespace FASTER.Models.Tests
{
    [TestFixture]
    public class ArmaServerPortsTests
    {
        [Test]
        public void GetInboundUdpPorts_DefaultGamePort_ReturnsBiRange()
        {
            var ports = ArmaServerPorts.GetInboundUdpPorts(2302);
            Assert.That(ports, Is.EqualTo(new[] { 2302, 2303, 2304, 2305, 2306 }));
        }

        [Test]
        public void GetInboundUdpPorts_CustomBase_OffsetsByPlusFour()
        {
            var ports = ArmaServerPorts.GetInboundUdpPorts(2402);
            Assert.That(ports, Is.EqualTo(new[] { 2402, 2403, 2404, 2405, 2406 }));
        }

        [Test]
        public void GetInboundUdpPorts_RejectsZeroAndOverflow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ArmaServerPorts.GetInboundUdpPorts(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ArmaServerPorts.GetInboundUdpPorts(65535));
            Assert.Throws<ArgumentOutOfRangeException>(() => ArmaServerPorts.GetInboundUdpPorts(65532));
        }

        [Test]
        public void BuildFirewallRuleName_IsStableAndSanitized()
        {
            Assert.That(
                ArmaServerPorts.BuildFirewallRuleName("Arma Domina", 2302),
                Is.EqualTo("FASTER-Arma Domina-2302"));
            Assert.That(
                ArmaServerPorts.BuildFirewallRuleName("My@Server!", 2402),
                Is.EqualTo("FASTER-MyServer-2402"));
        }

        [Test]
        public void FirewallDescription_RoundTripsProfileId()
        {
            var marker = ArmaServerPorts.BuildFirewallDescription("abc-123");
            Assert.That(marker, Is.EqualTo("FASTER:abc-123"));
            Assert.That(ArmaServerPorts.TryParseFirewallDescription(marker, out var id), Is.True);
            Assert.That(id, Is.EqualTo("abc-123"));
            Assert.That(ArmaServerPorts.TryParseFirewallDescription("other", out _), Is.False);
        }

        [Test]
        public void PortSpan_MatchesReturnedCount()
        {
            Assert.That(ArmaServerPorts.GetInboundUdpPorts(2302).Count, Is.EqualTo(ArmaServerPorts.PortSpan));
            Assert.That(ArmaServerPorts.GetInboundUdpPorts(2302).Last() - ArmaServerPorts.GetInboundUdpPorts(2302).First(),
                Is.EqualTo(ArmaServerPorts.PortSpan - 1));
        }
    }
}
