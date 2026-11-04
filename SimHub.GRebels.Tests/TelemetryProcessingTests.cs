using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace SimHub.GRebels.Tests
{
    public class TelemetryProcessingTests
    {
        private (PluginSettings settings, TelemetryReceiver receiver) CreateReceiver()
        {
            var settings = new PluginSettings();
            var map = new FieldMap();

            // Set up fields required for weapon counts, hits, and collision direction
            map.Fields["PrimaryCount"] = new FieldSpec { Type = "int", Offset = 0 };
            map.Fields["SecondaryCount"] = new FieldSpec { Type = "int", Offset = 4 };
            map.Fields["HitCount"] = new FieldSpec { Type = "int", Offset = 8 };
            map.Fields["HitStrength"] = new FieldSpec { Type = "float", Offset = 12 };
            map.Fields["CollisionCount"] = new FieldSpec { Type = "int", Offset = 16 };
            map.Fields["CollisionStrength"] = new FieldSpec { Type = "float", Offset = 20 };
            map.Fields["Expanded.Float07"] = new FieldSpec { Type = "float", Offset = 24 }; // Surge (Vx)
            map.Fields["Expanded.Float08"] = new FieldSpec { Type = "float", Offset = 28 }; // Sway (Vy)
            map.Fields["Expanded.Float09"] = new FieldSpec { Type = "float", Offset = 32 }; // Heave (Vz)

            settings.Fields = map;
            settings.CompiledFields = new CompiledFieldMap(map);
            var receiver = new TelemetryReceiver(settings);
            return (settings, receiver);
        }

        private byte[] CreatePacket(
            int primaryCount = 0,
            int secondaryCount = 0,
            int hitCount = 0,
            float hitStrength = 0f,
            int collisionCount = 0,
            float collisionStrength = 0f,
            float surge = 0f,
            float sway = 0f,
            float heave = 0f)
        {
            byte[] packet = new byte[36];
            BitConverter.GetBytes(primaryCount).CopyTo(packet, 0);
            BitConverter.GetBytes(secondaryCount).CopyTo(packet, 4);
            BitConverter.GetBytes(hitCount).CopyTo(packet, 8);
            BitConverter.GetBytes(hitStrength).CopyTo(packet, 12);
            BitConverter.GetBytes(collisionCount).CopyTo(packet, 16);
            BitConverter.GetBytes(collisionStrength).CopyTo(packet, 20);
            BitConverter.GetBytes(surge).CopyTo(packet, 24);
            BitConverter.GetBytes(sway).CopyTo(packet, 28);
            BitConverter.GetBytes(heave).CopyTo(packet, 32);
            return packet;
        }

        [Fact]
        public void PrimaryAndSecondaryFire_ShouldEstablishBaselineThenTrackShots()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // 1. Initial packet: should baseline without firing
            receiver.IngestPacket(CreatePacket(primaryCount: 10, secondaryCount: 5), sequence: 1);
            receiver.DrainEvents(events);
            events.Should().BeEmpty();
            receiver.SessionPrimaryCount.Should().Be(0);
            receiver.SessionSecondaryCount.Should().Be(0);

            // 2. Fire 2 primary rounds and 1 secondary round
            receiver.IngestPacket(CreatePacket(primaryCount: 12, secondaryCount: 6), sequence: 2);
            receiver.DrainEvents(events);
            events.Should().Contain("PrimaryFired");
            events.Should().Contain("SecondaryFired");
            receiver.SessionPrimaryCount.Should().Be(2);
            receiver.SessionSecondaryCount.Should().Be(1);

            // 3. Frame with no change
            events.Clear();
            receiver.IngestPacket(CreatePacket(primaryCount: 12, secondaryCount: 6), sequence: 3);
            receiver.DrainEvents(events);
            events.Should().BeEmpty();
            receiver.SessionPrimaryCount.Should().Be(2);
        }

        [Fact]
        public void WeaponCount_AbnormalDelta_ShouldRebaseWithoutFiring()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline at 10
            receiver.IngestPacket(CreatePacket(primaryCount: 10), sequence: 1);
            receiver.DrainEvents(events);

            // Delta > 10 (e.g. counter jumped from 10 to 100 on game reload)
            receiver.IngestPacket(CreatePacket(primaryCount: 100), sequence: 2);
            receiver.DrainEvents(events);
            events.Should().NotContain("PrimaryFired");
            receiver.SessionPrimaryCount.Should().Be(0);

            // Negative delta (rack reload / counter wrap from 100 to 0)
            receiver.IngestPacket(CreatePacket(primaryCount: 0), sequence: 3);
            receiver.DrainEvents(events);
            events.Should().NotContain("PrimaryFired");
            receiver.SessionPrimaryCount.Should().Be(0);

            // Legitimate fire after rebase
            receiver.IngestPacket(CreatePacket(primaryCount: 1), sequence: 4);
            receiver.DrainEvents(events);
            events.Should().Contain("PrimaryFired");
            receiver.SessionPrimaryCount.Should().Be(1);
        }

        [Fact]
        public void HitReceived_ShouldTriggerEventAndRecordStrength()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline
            receiver.IngestPacket(CreatePacket(hitCount: 0, hitStrength: 0f), sequence: 1);
            receiver.DrainEvents(events);

            // Receive projectile hit
            receiver.IngestPacket(CreatePacket(hitCount: 1, hitStrength: 18.5f), sequence: 2);
            receiver.DrainEvents(events);
            events.Should().Contain("HitReceived");
            receiver.LastHitFireUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void Collision_ShouldDetectFrontImpact()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline with initial velocity/accel: surge = 20.0
            receiver.IngestPacket(CreatePacket(collisionCount: 0, surge: 20f), sequence: 1);
            receiver.DrainEvents(events);

            // Impact: collision count increases, surge sharply drops (dvx = -15)
            receiver.IngestPacket(CreatePacket(collisionCount: 1, collisionStrength: 75f, surge: 5f), sequence: 2);
            receiver.DrainEvents(events);

            events.Should().Contain("CollisionReceived");
            receiver.LastCollisionStrength.Should().Be(75f);
            receiver.LastCollisionDirection.Should().Be("Front");
            receiver.LastCollisionDirectionCode.Should().Be(1);
        }

        [Fact]
        public void Collision_ShouldDetectRearImpact()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline: surge = 10.0
            receiver.IngestPacket(CreatePacket(collisionCount: 0, surge: 10f), sequence: 1);
            receiver.DrainEvents(events);

            // Rear ended: surge suddenly spikes forward (dvx = +25)
            receiver.IngestPacket(CreatePacket(collisionCount: 1, collisionStrength: 50f, surge: 35f), sequence: 2);
            receiver.DrainEvents(events);

            events.Should().Contain("CollisionReceived");
            receiver.LastCollisionDirection.Should().Be("Rear");
            receiver.LastCollisionDirectionCode.Should().Be(2);
        }

        [Fact]
        public void Collision_ShouldDetectLateralImpacts()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline
            receiver.IngestPacket(CreatePacket(collisionCount: 0, sway: 0f), sequence: 1);
            receiver.DrainEvents(events);

            // Impact pushing left (+dvy)
            receiver.IngestPacket(CreatePacket(collisionCount: 1, collisionStrength: 40f, sway: 20f), sequence: 2);
            receiver.DrainEvents(events);
            receiver.LastCollisionDirection.Should().Be("Left");
            receiver.LastCollisionDirectionCode.Should().Be(3);

            // Next impact pushing right (-dvy)
            receiver.IngestPacket(CreatePacket(collisionCount: 2, collisionStrength: 40f, sway: -10f), sequence: 3);
            receiver.DrainEvents(events);
            receiver.LastCollisionDirection.Should().Be("Right");
            receiver.LastCollisionDirectionCode.Should().Be(4);
        }

        [Fact]
        public void EdgeDetection_ShouldDebounceAndTriggerEdgeEvents()
        {
            var settings = new PluginSettings();
            var map = new FieldMap();
            map.Fields["BoostActive"] = new FieldSpec { Type = "float", Offset = 0 };
            settings.Fields = map;
            settings.CompiledFields = new CompiledFieldMap(map);

            settings.EdgeEvents.Add(new EdgeEventSpec
            {
                Field = "BoostActive",
                Threshold = 0.5,
                DebounceMs = 100,
                Event = "BoostEngaged"
            });
            settings.Validate();

            var receiver = new TelemetryReceiver(settings);
            var events = new List<string>();

            byte[] offPacket = new byte[4];
            BitConverter.GetBytes(0.0f).CopyTo(offPacket, 0);

            byte[] onPacket = new byte[4];
            BitConverter.GetBytes(1.0f).CopyTo(onPacket, 0);

            // 1. Initial off state
            receiver.IngestPacket(offPacket, sequence: 1);
            receiver.DrainEvents(events);
            events.Should().BeEmpty();

            // 2. Rising edge: transitions from 0.0 to 1.0 -> should trigger
            receiver.IngestPacket(onPacket, sequence: 2);
            receiver.DrainEvents(events);
            events.Should().Contain("BoostEngaged");

            // 3. Thread sleep for debounce interval, then send another on packet
            events.Clear();
            System.Threading.Thread.Sleep(110);
            receiver.IngestPacket(onPacket, sequence: 3);
            receiver.DrainEvents(events);
            // Must NOT re-trigger because it is already high (no rising edge occurred)
            events.Should().BeEmpty();
        }

        [Fact]
        public void DrainEvents_ShouldEnforceQueueMaxCapacity()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline
            receiver.IngestPacket(CreatePacket(primaryCount: 0), sequence: 1);

            // Rapidly send more events than MaxPendingEvents (64)
            for (int i = 1; i <= 80; i++)
            {
                receiver.IngestPacket(CreatePacket(primaryCount: i), sequence: i + 1);
            }

            receiver.DrainEvents(events);
            events.Count.Should().Be(64); // Clamped at MaxPendingEvents
        }
    }
}
