using System;
using System.IO;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace SimHub.GRebels.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void DefaultSettings_ShouldHaveSaneDefaults()
        {
            var settings = new PluginSettings();
            settings.Port.Should().Be(33001);
            settings.BindAddress.Should().Be("127.0.0.1");
            settings.StaleSeconds.Should().Be(1.0);
            settings.WeaponPulseMs.Should().Be(100);
            settings.HitPulseMs.Should().Be(200);
            settings.HitStrengthRef.Should().Be(25.0);
            settings.CollisionPulseMs.Should().Be(100);
            settings.CollisionStrengthRef.Should().Be(150.0);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Validate_InvalidPort_ShouldThrow(int port)
        {
            var settings = new PluginSettings { Port = port };
            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*Port*");
        }

        [Fact]
        public void Validate_NegativeStaleSeconds_ShouldThrow()
        {
            var settings = new PluginSettings { StaleSeconds = -0.5 };
            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*StaleSeconds*");
        }

        [Fact]
        public void Validate_NegativePulseTimes_ShouldThrow()
        {
            new Action(() => new PluginSettings { WeaponPulseMs = -1 }.Validate())
                .Should().Throw<ArgumentException>().WithMessage("*WeaponPulseMs*");

            new Action(() => new PluginSettings { HitPulseMs = -1 }.Validate())
                .Should().Throw<ArgumentException>().WithMessage("*HitPulseMs*");

            new Action(() => new PluginSettings { CollisionPulseMs = -1 }.Validate())
                .Should().Throw<ArgumentException>().WithMessage("*CollisionPulseMs*");
        }

        [Fact]
        public void Validate_InconsistentExpectedSize_ShouldThrow()
        {
            var settings = new PluginSettings();
            settings.Fields = new FieldMap
            {
                ExpectedSize = 100
            };
            settings.Fields.Fields["BigField"] = new FieldSpec { Type = "float", Offset = 120 };

            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*ExpectedSize*smaller than*");
        }

        [Fact]
        public void Validate_PropertyMinGreaterThanMax_ShouldThrow()
        {
            var settings = new PluginSettings();
            settings.Properties.Add(new PropertyMapEntry
            {
                Property = "TestProp",
                Field = "Pitch",
                Min = 100,
                Max = 50
            });
            var act = () => settings.Validate();
            act.Should().Throw<ArgumentException>().WithMessage("*Min greater than Max*");
        }

        [Fact]
        public void Validate_EdgeEventMissingFieldOrEvent_ShouldThrow()
        {
            var settingsNoField = new PluginSettings();
            settingsNoField.EdgeEvents.Add(new EdgeEventSpec { Event = "Evt", Field = "" });
            var act1 = () => settingsNoField.Validate();
            act1.Should().Throw<ArgumentException>().WithMessage("*missing \"Field\"*");

            var settingsNoEvt = new PluginSettings();
            settingsNoEvt.EdgeEvents.Add(new EdgeEventSpec { Event = "", Field = "Speed" });
            var act2 = () => settingsNoEvt.Validate();
            act2.Should().Throw<ArgumentException>().WithMessage("*missing \"Event\"*");
        }

        [Fact]
        public void Load_NonExistentFile_ReturnsDefaultSettings()
        {
            var settings = PluginSettings.Load("non_existent_file.json");
            settings.Should().NotBeNull();
            settings.Port.Should().Be(33001);
        }

        [Fact]
        public void Load_ValidJson_ReturnsDeserializedSettings()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "{ \"Port\": 28999, \"StaleSeconds\": 2.5 }");
                var loaded = PluginSettings.Load(tempFile);
                loaded.Port.Should().Be(28999);
                loaded.StaleSeconds.Should().Be(2.5);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
