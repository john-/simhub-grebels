using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace SimHub.GRebels.Tests
{
    public class TelemetryEdgeCaseTests
    {
        private (PluginSettings settings, TelemetryReceiver receiver) CreateReceiver(double staleSeconds = 1.0)
        {
            var settings = new PluginSettings { StaleSeconds = staleSeconds };
            var map = new FieldMap();

            map.Fields["PrimaryCount"] = new FieldSpec { Type = "int", Offset = 0 };
            map.Fields["SecondaryCount"] = new FieldSpec { Type = "int", Offset = 4 };
            map.Fields["HitCount"] = new FieldSpec { Type = "int", Offset = 8 };
            map.Fields["HitStrength"] = new FieldSpec { Type = "float", Offset = 12 };
            map.Fields["CollisionCount"] = new FieldSpec { Type = "int", Offset = 16 };
            map.Fields["CollisionStrength"] = new FieldSpec { Type = "float", Offset = 20 };
            map.Fields["LateralVelocity"] = new FieldSpec { Type = "float", Offset = 24 }; // Sway
            map.Fields["VerticalVelocity"] = new FieldSpec { Type = "float", Offset = 28 }; // Heave
            map.Fields["LongitudinalVelocity"] = new FieldSpec { Type = "float", Offset = 32 }; // Surge

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
            float lateralVel = 0f,
            float verticalVel = 0f,
            float longitudinalVel = 0f)
        {
            byte[] packet = new byte[36];
            BitConverter.GetBytes(primaryCount).CopyTo(packet, 0);
            BitConverter.GetBytes(secondaryCount).CopyTo(packet, 4);
            BitConverter.GetBytes(hitCount).CopyTo(packet, 8);
            BitConverter.GetBytes(hitStrength).CopyTo(packet, 12);
            BitConverter.GetBytes(collisionCount).CopyTo(packet, 16);
            BitConverter.GetBytes(collisionStrength).CopyTo(packet, 20);
            BitConverter.GetBytes(lateralVel).CopyTo(packet, 24);
            BitConverter.GetBytes(verticalVel).CopyTo(packet, 28);
            BitConverter.GetBytes(longitudinalVel).CopyTo(packet, 32);
            return packet;
        }

        [Fact]
        public void Collision_ShouldDetectTopAndBottomImpacts()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Baseline with verticalVel = 0
            receiver.IngestPacket(CreatePacket(collisionCount: 0, verticalVel: 0f), sequence: 1);
            receiver.DrainEvents(events);

            // 1. Bottom impact: craft pushed upward or downward deceleration (dVert < 0)
            receiver.IngestPacket(CreatePacket(collisionCount: 1, collisionStrength: 60f, verticalVel: -25f), sequence: 2);
            receiver.DrainEvents(events);
            receiver.LastCollisionDirection.Should().Be("Bottom");
            receiver.LastCollisionDirectionCode.Should().Be(5);

            // 2. Top impact: roof crash (dVert > 0)
            receiver.IngestPacket(CreatePacket(collisionCount: 2, collisionStrength: 60f, verticalVel: 20f), sequence: 3);
            receiver.DrainEvents(events);
            receiver.LastCollisionDirection.Should().Be("Top");
            receiver.LastCollisionDirectionCode.Should().Be(6);
        }

        [Fact]
        public void FeedStaleness_ResumedFeed_ShouldRebaselineWithoutFiringFalseEvents()
        {
            var (_, receiver) = CreateReceiver(staleSeconds: 1.0);
            var events = new List<string>();

            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            // 1. Initial packet at T0
            receiver.IngestPacket(CreatePacket(primaryCount: 10, collisionCount: 5), sequence: 1, packetTimeUtc: t0);
            receiver.DrainEvents(events);

            // 2. Telemetry pauses for 5 seconds (exceeds StaleSeconds = 1.0)
            var t1 = t0.AddSeconds(5);

            // 3. Resumed feed with counter difference (e.g. from in-game restart or player respawn)
            receiver.IngestPacket(CreatePacket(primaryCount: 15, collisionCount: 8), sequence: 2, packetTimeUtc: t1);
            receiver.DrainEvents(events);

            // Must NOT fire false events from stale gap jump
            events.Should().NotContain("PrimaryFired");
            events.Should().NotContain("CollisionReceived");
            receiver.SessionPrimaryCount.Should().Be(0);

            // 4. Next packet arriving on time (16ms later) should now track legitimate game events
            var t2 = t1.AddMilliseconds(16);
            receiver.IngestPacket(CreatePacket(primaryCount: 16, collisionCount: 8), sequence: 3, packetTimeUtc: t2);
            receiver.DrainEvents(events);
            events.Should().Contain("PrimaryFired");
            receiver.SessionPrimaryCount.Should().Be(1);
        }

        [Fact]
        public void MalformedFloats_NaNAndInfinity_ShouldBeIgnoredSafely()
        {
            var (_, receiver) = CreateReceiver();
            var events = new List<string>();

            // Send packet containing NaN and Infinity
            byte[] packet = new byte[36];
            BitConverter.GetBytes(float.NaN).CopyTo(packet, 12); // HitStrength = NaN
            BitConverter.GetBytes(float.PositiveInfinity).CopyTo(packet, 20); // CollisionStrength = Infinity

            // Ingest should not throw ArithmeticException or corrupt internal state
            Action act = () => receiver.IngestPacket(packet, sequence: 1);
            act.Should().NotThrow();
            receiver.DrainEvents(events);
            events.Should().BeEmpty();
        }
    }
}
