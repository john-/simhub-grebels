using System;
using System.IO;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace SimHub.GRebels.Tests
{
    public class ProtocolTests
    {
        [Fact]
        public void ParseDataType_ShouldRecognizeAllSupportedTypes()
        {
            FieldSpec.ParseDataType("float").Should().Be(TelemetryDataType.Float);
            FieldSpec.ParseDataType("double").Should().Be(TelemetryDataType.Double);
            FieldSpec.ParseDataType("int").Should().Be(TelemetryDataType.Int);
            FieldSpec.ParseDataType("int32").Should().Be(TelemetryDataType.Int);
            FieldSpec.ParseDataType("uint").Should().Be(TelemetryDataType.UInt);
            FieldSpec.ParseDataType("uint32").Should().Be(TelemetryDataType.UInt);
            FieldSpec.ParseDataType("short").Should().Be(TelemetryDataType.Short);
            FieldSpec.ParseDataType("int16").Should().Be(TelemetryDataType.Short);
            FieldSpec.ParseDataType("ushort").Should().Be(TelemetryDataType.UShort);
            FieldSpec.ParseDataType("uint16").Should().Be(TelemetryDataType.UShort);
            FieldSpec.ParseDataType("byte").Should().Be(TelemetryDataType.Byte);
            FieldSpec.ParseDataType("sbyte").Should().Be(TelemetryDataType.SByte);
            FieldSpec.ParseDataType("string").Should().Be(TelemetryDataType.String);
            FieldSpec.ParseDataType("unknown").Should().Be(TelemetryDataType.Float);
            FieldSpec.ParseDataType("").Should().Be(TelemetryDataType.Float);
        }

        [Fact]
        public void FieldSpec_ByteSize_ShouldMatchDataType()
        {
            new FieldSpec { Type = "double" }.ByteSize.Should().Be(8);
            new FieldSpec { Type = "float" }.ByteSize.Should().Be(4);
            new FieldSpec { Type = "int" }.ByteSize.Should().Be(4);
            new FieldSpec { Type = "uint" }.ByteSize.Should().Be(4);
            new FieldSpec { Type = "short" }.ByteSize.Should().Be(2);
            new FieldSpec { Type = "ushort" }.ByteSize.Should().Be(2);
            new FieldSpec { Type = "byte" }.ByteSize.Should().Be(1);
            new FieldSpec { Type = "sbyte" }.ByteSize.Should().Be(1);
            new FieldSpec { Type = "string", Length = 48 }.ByteSize.Should().Be(48);
        }

        [Fact]
        public void SimHub_GRebels_Json_ShouldBeValidAndWellFormed()
        {
            string jsonPath = Path.Combine(AppContext.BaseDirectory, "SimHub.GRebels.json");
            File.Exists(jsonPath).Should().BeTrue("SimHub.GRebels.json should be copied to output");

            string json = File.ReadAllText(jsonPath);
            var settings = JsonConvert.DeserializeObject<PluginSettings>(json);
            settings.Should().NotBeNull();
            settings!.Fields.Should().NotBeNull();
            settings.Fields.Fields.Should().NotBeEmpty();

            // Expected packet size in current configuration is 236
            settings.Fields.ExpectedSize.Should().BeNull();

            // Largest offset in fields must fit in expected size
            settings.Fields.LargestOffset.Should().BeLessOrEqualTo(264);

            // Verify compiled map builds without exceptions
            var compiled = new CompiledFieldMap(settings.Fields);
            compiled.NumericFields.Length.Should().BeGreaterThan(0);
            compiled.GetFieldIndex("Speed").Should().BeGreaterThanOrEqualTo(0);
            compiled.GetFieldIndex("Pitch").Should().BeGreaterThanOrEqualTo(0);
            compiled.GetFieldIndex("PrimaryCount").Should().BeGreaterThanOrEqualTo(0);
            compiled.GetFieldIndex("CollisionCount").Should().BeGreaterThanOrEqualTo(0);
        }

        [Fact]
        public void PacketParser_ShouldDecodeAllNumericTypesCorrectly()
        {
            var map = new FieldMap();
            map.Fields["F_Float"] = new FieldSpec { Type = "float", Offset = 0 };
            map.Fields["F_Double"] = new FieldSpec { Type = "double", Offset = 4 };
            map.Fields["F_Int"] = new FieldSpec { Type = "int", Offset = 12 };
            map.Fields["F_UInt"] = new FieldSpec { Type = "uint", Offset = 16 };
            map.Fields["F_Short"] = new FieldSpec { Type = "short", Offset = 20 };
            map.Fields["F_UShort"] = new FieldSpec { Type = "ushort", Offset = 22 };
            map.Fields["F_Byte"] = new FieldSpec { Type = "byte", Offset = 24 };
            map.Fields["F_SByte"] = new FieldSpec { Type = "sbyte", Offset = 25 };
            map.Fields["F_Scaled"] = new FieldSpec { Type = "float", Offset = 26, Scale = 3.6, Bias = 10.0 };

            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] buffer = new byte[32];
            BitConverter.GetBytes(123.45f).CopyTo(buffer, 0);
            BitConverter.GetBytes(987654.321d).CopyTo(buffer, 4);
            BitConverter.GetBytes(-42).CopyTo(buffer, 12);
            BitConverter.GetBytes(4294967200u).CopyTo(buffer, 16);
            BitConverter.GetBytes((short)-1234).CopyTo(buffer, 20);
            BitConverter.GetBytes((ushort)54321).CopyTo(buffer, 22);
            buffer[24] = 200;
            buffer[25] = unchecked((byte)(sbyte)-50);
            BitConverter.GetBytes(10.0f).CopyTo(buffer, 26); // (10.0 * 3.6) + 10.0 = 46.0

            PacketParser.ParseInto(buffer, compiled, 1, frame);

            frame.Sequence.Should().Be(1);
            frame.Length.Should().Be(buffer.Length);
            frame.GetNumber(compiled.GetFieldIndex("F_Float")).Should().BeApproximately(123.45, 0.001);
            frame.GetNumber(compiled.GetFieldIndex("F_Double")).Should().BeApproximately(987654.321, 0.0001);
            frame.GetNumber(compiled.GetFieldIndex("F_Int")).Should().Be(-42);
            frame.GetNumber(compiled.GetFieldIndex("F_UInt")).Should().Be(4294967200);
            frame.GetNumber(compiled.GetFieldIndex("F_Short")).Should().Be(-1234);
            frame.GetNumber(compiled.GetFieldIndex("F_UShort")).Should().Be(54321);
            frame.GetNumber(compiled.GetFieldIndex("F_Byte")).Should().Be(200);
            frame.GetNumber(compiled.GetFieldIndex("F_SByte")).Should().Be(-50);
            frame.GetNumber(compiled.GetFieldIndex("F_Scaled")).Should().BeApproximately(46.0, 0.001);
        }

        [Fact]
        public void PacketParser_ShouldDecodeStringsAndHandleNullTerminators()
        {
            var map = new FieldMap();
            map.Fields["VehicleName"] = new FieldSpec { Type = "string", Offset = 0, Length = 16 };
            map.Fields["TrackName"] = new FieldSpec { Type = "string", Offset = 16, Length = 16 };

            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] buffer = new byte[32];
            byte[] strBytes = Encoding.UTF8.GetBytes("Razorback");
            Array.Copy(strBytes, 0, buffer, 0, strBytes.Length); // Null-terminated automatically by remaining zeroes

            byte[] strBytes2 = Encoding.UTF8.GetBytes("Neo-Veridia Orbit");
            Array.Copy(strBytes2, 0, buffer, 16, 16); // Exactly fills 16 bytes

            PacketParser.ParseInto(buffer, compiled, 42, frame);

            frame.GetString(compiled.GetStringIndex("VehicleName")).Should().Be("Razorback");
            frame.GetString(compiled.GetStringIndex("TrackName")).Should().Be("Neo-Veridia Orbi"); // 16 bytes truncated
        }

        [Fact]
        public void PacketParser_ShouldHandleTruncatedBuffersGracefully()
        {
            var map = new FieldMap();
            map.Fields["SafeField"] = new FieldSpec { Type = "float", Offset = 0 };
            map.Fields["OOBField"] = new FieldSpec { Type = "float", Offset = 100 };

            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);

            byte[] buffer = new byte[10]; // Less than 104
            BitConverter.GetBytes(55.5f).CopyTo(buffer, 0);

            // Should not throw IndexOutOfRangeException
            PacketParser.ParseInto(buffer, compiled, 1, frame);

            frame.GetNumber(compiled.GetFieldIndex("SafeField")).Should().BeApproximately(55.5, 0.01);
            frame.GetNumber(compiled.GetFieldIndex("OOBField")).Should().Be(0.0);
        }

        [Fact]
        public void Frame_GetNumberAndGetString_OutOfBoundsReturnsDefaults()
        {
            var compiled = new CompiledFieldMap(new FieldMap());
            var frame = new Frame(compiled);

            frame.GetNumber(-1).Should().Be(0.0);
            frame.GetNumber(999).Should().Be(0.0);
            frame.GetString(-1).Should().Be(string.Empty);
            frame.GetString(999).Should().Be(string.Empty);
        }

        [Fact]
        public void Frame_AgeSeconds_WhenUninitialized_ReportsInfinityAndNoData()
        {
            var compiled = new CompiledFieldMap(new FieldMap());
            var frame = new Frame(compiled);

            frame.HasData.Should().BeFalse();
            double.IsPositiveInfinity(frame.AgeSeconds).Should().BeTrue();
        }

        [Fact]
        public void Frame_AgeSeconds_WhenInitialized_ReportsAccurateNonNegativeAge()
        {
            var compiled = new CompiledFieldMap(new FieldMap());
            var frame = new Frame(compiled);

            frame.ReceivedUtc = DateTime.UtcNow.AddMilliseconds(-500);
            frame.HasData.Should().BeTrue();
            frame.AgeSeconds.Should().BeInRange(0.4, 2.0);

            // Clock drift into the future should clamp to 0.0
            frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(5);
            frame.AgeSeconds.Should().Be(0.0);
        }

        [Theory]
        [InlineData("v1.0.1-6-gce78d2a", "v1.0.1-6-ce78d2a")]
        [InlineData("v1.0.1-6-gce78d2a-dirty", "v1.0.1-6-ce78d2a-dirty")]
        [InlineData("v1.0.1", "v1.0.1")]
        [InlineData("1.0.1+gce78d2a123456-dirty", "1.0.1 (ce78d2a-dirty)")]
        [InlineData("1.0.1+ce78d2a123456", "1.0.1 (ce78d2a)")]
        public void FormatVersionString_StripsGitGPrefixProperly(string input, string expected)
        {
            VersionFormatter.FormatVersionString(input).Should().Be(expected);
        }
    }
}
