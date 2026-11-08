using System;
using System.IO;
using System.Net;
using FluentAssertions;
using Xunit;

namespace SimHub.GRebels.Tests
{
    public class SecurityAuditTests
    {
        [Fact]
        public void TryReadNumber_IntegerOverflowOffset_ShouldNotThrowAndReturnZero()
        {
            var map = new FieldMap();
            map.Fields["OverflowField"] = new FieldSpec
            {
                Type = "float",
                Offset = int.MaxValue - 1 // Large offset that would overflow if added to ByteSize (4)
            };
            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] data = new byte[32];

            // Must not throw IndexOutOfRangeException or ArgumentOutOfRangeException
            var act = () => PacketParser.ParseInto(data, compiled, 1, frame);
            act.Should().NotThrow();
            frame.GetNumber(0).Should().Be(0.0);
        }

        [Fact]
        public void TryReadString_IntegerOverflowOffset_ShouldNotThrowAndReturnEmpty()
        {
            var map = new FieldMap();
            map.Fields["OverflowStr"] = new FieldSpec
            {
                Type = "string",
                Offset = int.MaxValue - 10,
                Length = 32
            };
            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] data = new byte[32];

            var act = () => PacketParser.ParseInto(data, compiled, 1, frame);
            act.Should().NotThrow();
            frame.GetString(0).Should().BeEmpty();
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void TryReadNumber_NanAndInfinity_ShouldBeSanitizedToZero(float anomalousValue)
        {
            var map = new FieldMap();
            map.Fields["FloatVal"] = new FieldSpec { Type = "float", Offset = 0 };
            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] data = BitConverter.GetBytes(anomalousValue);
            PacketParser.ParseInto(data, compiled, 1, frame);

            double result = frame.GetNumber(0);
            double.IsNaN(result).Should().BeFalse();
            double.IsInfinity(result).Should().BeFalse();
            result.Should().Be(0.0);
        }

        [Fact]
        public void FieldMap_LargestOffset_WithNullFieldEntries_ShouldNotThrow()
        {
            var map = new FieldMap();
            map.Fields["ValidField"] = new FieldSpec { Type = "float", Offset = 10 };
            map.Fields["NullField"] = null;

            var act = () => map.LargestOffset;
            act.Should().NotThrow();
            map.LargestOffset.Should().Be(14); // 10 + 4
        }

        [Fact]
        public void TelemetryReceiver_PacketLengthFlood_ShouldBeCappedAt64()
        {
            var settings = new PluginSettings();
            var receiver = new TelemetryReceiver(settings);

            // Ingest 150 packets of distinct lengths
            for (int len = 10; len < 160; len++)
            {
                byte[] packet = new byte[len];
                receiver.IngestPacket(packet, len);
            }

            string desc = receiver.DescribeLengths();
            string[] parts = desc.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

            // Must be capped at maximum 64 tracked distinct lengths
            parts.Length.Should().BeLessOrEqualTo(64);
        }

        [Fact]
        public void Settings_Validate_NegativeOffset_ShouldThrow()
        {
            var settings = new PluginSettings();
            settings.Fields.Fields["BadOffset"] = new FieldSpec { Type = "float", Offset = -4 };

            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*negative Offset*");
        }

        [Fact]
        public void Settings_Validate_InvalidDataType_ShouldThrow()
        {
            var settings = new PluginSettings();
            settings.Fields.Fields["BadType"] = new FieldSpec { Type = "pointer32", Offset = 0 };

            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*unrecognized Type*");
        }

        [Fact]
        public void Settings_Validate_StringNonPositiveLength_ShouldThrow()
        {
            var settings = new PluginSettings();
            settings.Fields.Fields["BadStr"] = new FieldSpec { Type = "string", Offset = 0, Length = 0 };

            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*non-positive Length*");
        }

        [Theory]
        [InlineData("999.999.999.999")]
        [InlineData("not_an_ip")]
        [InlineData("192.168.1.300")]
        public void Settings_Validate_InvalidAllowedSenderIp_ShouldThrow(string invalidIp)
        {
            var settings = new PluginSettings { AllowedSenderIp = invalidIp };
            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*not a valid IP address*");
        }

        [Fact]
        public void Settings_Validate_ValidAllowedSenderIp_ShouldSucceed()
        {
            var settings = new PluginSettings { AllowedSenderIp = "192.168.1.100" };
            var act = () => settings.Validate();
            act.Should().NotThrow();
        }

        [Fact]
        public void Settings_Validate_NanScaleOrBias_ShouldThrow()
        {
            var settingsScale = new PluginSettings();
            settingsScale.Fields.Fields["NanScale"] = new FieldSpec { Type = "float", Offset = 0, Scale = double.NaN };
            new Action(() => settingsScale.Validate()).Should().Throw<ArgumentException>().WithMessage("*invalid Scale*");

            var settingsBias = new PluginSettings();
            settingsBias.Fields.Fields["NanBias"] = new FieldSpec { Type = "float", Offset = 0, Bias = double.PositiveInfinity };
            new Action(() => settingsBias.Validate()).Should().Throw<ArgumentException>().WithMessage("*invalid Bias*");
        }

        [Fact]
        public void Settings_Load_WithTypeNameHandlingPayload_ShouldDeserializeSafely()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "grebels_sec_test_" + Guid.NewGuid() + ".json");
            try
            {
                // Attacker payload attempting to inject type metadata
                string maliciousJson = @"{
                    ""$type"": ""System.IO.FileInfo, System.IO.FileSystem"",
                    ""Port"": 33005,
                    ""BindAddress"": ""127.0.0.1""
                }";
                File.WriteAllText(tempFile, maliciousJson);

                var loaded = PluginSettings.Load(tempFile);
                loaded.Should().NotBeNull();
                loaded.Port.Should().Be(33005);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void TryReadString_ControlCharacters_ShouldBeSanitized()
        {
            var map = new FieldMap();
            map.Fields["SanitizedStr"] = new FieldSpec { Type = "string", Offset = 0, Length = 16 };
            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            // String with control characters in a 16-byte buffer: "Test\x07\x1bPilot\x00"
            byte[] data = new byte[16];
            byte[] content = new byte[] {
                (byte)'T', (byte)'e', (byte)'s', (byte)'t',
                0x07, // bell
                0x1B, // ESC
                (byte)'P', (byte)'i', (byte)'l', (byte)'o', (byte)'t',
                0x00
            };
            content.CopyTo(data, 0);

            PacketParser.ParseInto(data, compiled, 1, frame);
            string text = frame.GetString(0);

            text.Should().Be("TestPilot");
            text.Should().NotContain("\x07");
            text.Should().NotContain("\x1B");
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void TelemetryReceiver_ImpulseCalculations_NanStrength_ShouldBeHandledSafely(float anomalousStrength)
        {
            var settings = new PluginSettings { HitPulseMs = 100, CollisionPulseMs = 100 };
            var map = new FieldMap();
            map.Fields["HitCount"] = new FieldSpec { Type = "int", Offset = 0 };
            map.Fields["HitStrength"] = new FieldSpec { Type = "float", Offset = 4 };
            map.Fields["CollisionCount"] = new FieldSpec { Type = "int", Offset = 8 };
            map.Fields["CollisionStrength"] = new FieldSpec { Type = "float", Offset = 12 };
            settings.Fields = map;
            settings.CompiledFields = new CompiledFieldMap(map);

            var receiver = new TelemetryReceiver(settings);

            // Baseline packet
            byte[] p0 = new byte[16];
            receiver.IngestPacket(p0, 1);

            // Packet with hit and collision events with anomalous strength
            byte[] p1 = new byte[16];
            BitConverter.GetBytes(1).CopyTo(p1, 0); // hit count = 1
            BitConverter.GetBytes(anomalousStrength).CopyTo(p1, 4);
            BitConverter.GetBytes(1).CopyTo(p1, 8); // collision count = 1
            BitConverter.GetBytes(anomalousStrength).CopyTo(p1, 12);
            receiver.IngestPacket(p1, 2);

            double hitImpulse = receiver.HitImpulse;
            double collImpulse = receiver.CollisionImpulse;

            double.IsNaN(hitImpulse).Should().BeFalse();
            double.IsInfinity(hitImpulse).Should().BeFalse();
            hitImpulse.Should().BeInRange(0.0, 100.0);

            double.IsNaN(collImpulse).Should().BeFalse();
            double.IsInfinity(collImpulse).Should().BeFalse();
            collImpulse.Should().BeInRange(0.0, 100.0);
        }
    }
}
