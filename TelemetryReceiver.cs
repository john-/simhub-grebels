using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SimHub.GRebels
{
    /// <summary>A transient SimHub event detected on the receive thread.</summary>
    public struct PendingEvent
    {
        public string Name;
    }

    /// <summary>
    /// Owns the UDP socket and the receive thread.
    ///
    /// Design notes:
    ///  - The parsed frame is swapped by reference, so SimHub's DataUpdate thread
    ///    always sees a self-consistent snapshot and never blocks on us.
    ///  - Edge detection runs here, on the packet stream, not in DataUpdate. SimHub
    ///    calls DataUpdate at 60 Hz and packets arrive at 60 Hz independently, so
    ///    polling in DataUpdate would drop or double-count transitions.
    /// </summary>
    public sealed class TelemetryReceiver : IDisposable
    {
        private readonly PluginSettings _settings;
        private readonly CompiledFieldMap _compiledMap;
        private readonly List<EdgeEventSpec> _edgeSpecs;

        private Thread _thread;
        private UdpClient _client;
        private volatile bool _stopRequested;

        private Frame _frameA;
        private Frame _frameB;
        private Frame _backFrame;
        private volatile Frame _current;

        private const int MaxPendingEvents = 64;
        private readonly ConcurrentQueue<PendingEvent> _pending = new ConcurrentQueue<PendingEvent>();
        private readonly Dictionary<string, double> _lastEdgeValue =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastEdgeFire =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _lengthCounts = new Dictionary<int, int>();
        private readonly object _lengthsLock = new object();

        // Pre-resolved channel indices for zero-allocation pulse and direction handling
        private readonly int _primaryCountIdx;
        private readonly int _secondaryCountIdx;
        private readonly int _hitCountIdx;
        private readonly int _collisionCountIdx;
        private readonly int _hitStrengthIdx;
        private readonly int _collisionStrengthIdx;
        private readonly int _vxIdx;
        private readonly int _vyIdx;
        private readonly int _vzIdx;

        private double? _prevPrimaryCount;
        private double? _prevSecondaryCount;
        private double _sessionPrimaryCount;
        private double _sessionSecondaryCount;
        private DateTime _lastPrimaryFireUtc = DateTime.MinValue;
        private DateTime _lastSecondaryFireUtc = DateTime.MinValue;

        public double SessionPrimaryCount => _sessionPrimaryCount;
        public double SessionSecondaryCount => _sessionSecondaryCount;

        private double? _prevHitCount;
        private DateTime _lastHitFireUtc = DateTime.MinValue;
        private double _lastHitStrength = 0.0;

        private double? _prevCollisionCount;
        private DateTime _lastCollisionFireUtc = DateTime.MinValue;
        private double _lastCollisionStrength = 0.0;
        public double LastCollisionStrength => _lastCollisionStrength;
        private double? _prevVx;
        private double? _prevVy;
        private double? _prevVz;
        private volatile string _lastCollisionDirection = "None";
        private volatile int _lastCollisionDirectionCode = 0;

        private long _packetCount;
        private long _byteCount;
        private int _lastLength;
        private DateTime _lastPacketUtc = DateTime.MinValue;
        private int _dumped;
        private int _lengthMismatchCount;

        private int _packetsInCurrentSecond;
        private DateTime _rateWindowStartUtc = DateTime.UtcNow;
        private double _smoothedPacketsPerSecond;

        public TelemetryReceiver(PluginSettings settings)
        {
            _settings = settings ?? new PluginSettings();
            _compiledMap = _settings.CompiledFields ?? new CompiledFieldMap(_settings.Fields);
            _edgeSpecs = _settings.EdgeEvents ?? new List<EdgeEventSpec>();

            _frameA = new Frame(_compiledMap);
            _frameB = new Frame(_compiledMap);
            _current = _frameA;
            _backFrame = _frameB;

            _primaryCountIdx = _compiledMap.GetFieldIndex("PrimaryCount");
            _secondaryCountIdx = _compiledMap.GetFieldIndex("SecondaryCount");
            _hitCountIdx = _compiledMap.GetFieldIndex("HitCount");
            _collisionCountIdx = _compiledMap.GetFieldIndex("CollisionCount");
            _hitStrengthIdx = _compiledMap.GetFieldIndex("HitStrength");
            _collisionStrengthIdx = _compiledMap.GetFieldIndex("CollisionStrength");
            _vxIdx = _compiledMap.GetFieldIndex("Expanded.Float07");
            _vyIdx = _compiledMap.GetFieldIndex("Expanded.Float08");
            _vzIdx = _compiledMap.GetFieldIndex("Expanded.Float09");
        }

        public Frame Current { get { return _current; } }
        public long PacketCount { get { return Interlocked.Read(ref _packetCount); } }
        public long ByteCount { get { return Interlocked.Read(ref _byteCount); } }
        public int LastLength { get { return _lastLength; } }
        public int LengthMismatchCount { get { return _lengthMismatchCount; } }
        public DateTime LastPacketUtc { get { return _lastPacketUtc; } }

        public double PacketsPerSecond
        {
            get
            {
                var now = DateTime.UtcNow;
                if (_lastPacketUtc == DateTime.MinValue || (now - _lastPacketUtc).TotalSeconds > _settings.StaleSeconds)
                    return 0.0;

                return Volatile.Read(ref _smoothedPacketsPerSecond);
            }
        }

        public double PrimaryFired
        {
            get
            {
                if (_settings.WeaponPulseMs <= 0 || _lastPrimaryFireUtc == DateTime.MinValue) return 0.0;
                return (DateTime.UtcNow - _lastPrimaryFireUtc).TotalMilliseconds < _settings.WeaponPulseMs ? 100.0 : 0.0;
            }
        }

        public double SecondaryFired
        {
            get
            {
                if (_settings.WeaponPulseMs <= 0 || _lastSecondaryFireUtc == DateTime.MinValue) return 0.0;
                return (DateTime.UtcNow - _lastSecondaryFireUtc).TotalMilliseconds < _settings.WeaponPulseMs ? 100.0 : 0.0;
            }
        }

        public double HitFired
        {
            get
            {
                if (_settings.HitPulseMs <= 0 || _lastHitFireUtc == DateTime.MinValue) return 0.0;
                return (DateTime.UtcNow - _lastHitFireUtc).TotalMilliseconds < _settings.HitPulseMs ? 100.0 : 0.0;
            }
        }

        public double HitImpulse
        {
            get
            {
                if (_settings.HitPulseMs <= 0 || _lastHitFireUtc == DateTime.MinValue) return 0.0;
                double elapsedMs = (DateTime.UtcNow - _lastHitFireUtc).TotalMilliseconds;
                if (elapsedMs >= _settings.HitPulseMs || elapsedMs < 0) return 0.0;

                double refVal = _settings.HitStrengthRef > 0.0 ? _settings.HitStrengthRef : 25.0;
                double strengthRatio = Math.Abs(_lastHitStrength) / refVal;
                double clampedRatio = Math.Max(0.05, Math.Min(1.0, strengthRatio));
                double decay = 1.0 - (elapsedMs / _settings.HitPulseMs);
                return clampedRatio * decay * 100.0;
            }
        }

        public double CollisionFired
        {
            get
            {
                if (_settings.CollisionPulseMs <= 0 || _lastCollisionFireUtc == DateTime.MinValue) return 0.0;
                return (DateTime.UtcNow - _lastCollisionFireUtc).TotalMilliseconds < _settings.CollisionPulseMs ? 100.0 : 0.0;
            }
        }

        public double CollisionImpulse
        {
            get
            {
                if (_settings.CollisionPulseMs <= 0 || _lastCollisionFireUtc == DateTime.MinValue) return 0.0;
                double elapsedMs = (DateTime.UtcNow - _lastCollisionFireUtc).TotalMilliseconds;
                if (elapsedMs >= _settings.CollisionPulseMs || elapsedMs < 0) return 0.0;

                double refVal = _settings.CollisionStrengthRef > 0.0 ? _settings.CollisionStrengthRef : 150.0;
                double strengthRatio = Math.Abs(_lastCollisionStrength) / refVal;
                double clampedRatio = Math.Max(0.05, Math.Min(1.0, strengthRatio));
                double decay = 1.0 - (elapsedMs / _settings.CollisionPulseMs);
                return clampedRatio * decay * 100.0;
            }
        }

        public string CollisionDirection
        {
            get
            {
                if (_settings.CollisionPulseMs <= 0 || _lastCollisionFireUtc == DateTime.MinValue) return "None";
                return (DateTime.UtcNow - _lastCollisionFireUtc).TotalMilliseconds < _settings.CollisionPulseMs ? _lastCollisionDirection : "None";
            }
        }

        public int CollisionDirectionCode
        {
            get
            {
                if (_settings.CollisionPulseMs <= 0 || _lastCollisionFireUtc == DateTime.MinValue) return 0;
                return (DateTime.UtcNow - _lastCollisionFireUtc).TotalMilliseconds < _settings.CollisionPulseMs ? _lastCollisionDirectionCode : 0;
            }
        }

        /// <summary>Distinct payload lengths seen, for spotting a wrong layout.</summary>
        public string DescribeLengths()
        {
            KeyValuePair<int, int>[] snapshot;
            lock (_lengthsLock)
            {
                if (_lengthCounts.Count == 0) return "none";
                snapshot = new KeyValuePair<int, int>[_lengthCounts.Count];
                int idx = 0;
                foreach (var kv in _lengthCounts) snapshot[idx++] = kv;
            }

            // String formatting and sorting performed outside the lock to prevent blocking the receive thread
            var parts = new List<string>(snapshot.Length);
            for (int i = 0; i < snapshot.Length; i++)
            {
                parts.Add(snapshot[i].Key + "B x" + snapshot[i].Value);
            }
            parts.Sort();
            return string.Join(", ", parts.ToArray());
        }

        public bool Start(out string error)
        {
            error = null;
            try
            {
                IPAddress address;
                if (!IPAddress.TryParse(_settings.BindAddress ?? "127.0.0.1", out address))
                {
                    error = "Invalid BindAddress: " + _settings.BindAddress;
                    return false;
                }

                _client = new UdpClient();
                try
                {
                    _client.Client.ExclusiveAddressUse = true;
                }
                catch
                {
                    // Fall back cleanly if platform does not support ExclusiveAddressUse
                }
                _client.Client.Bind(new IPEndPoint(address, _settings.Port));

                _stopRequested = false;
                _thread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "GRebels UDP receive"
                };
                _thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _client = null;
                return false;
            }
        }

        private void ReceiveLoop()
        {
            long sequence = 0;

            while (!_stopRequested)
            {
                try
                {
                    var endpoint = new IPEndPoint(IPAddress.Any, 0);
                    var payload = _client.Receive(ref endpoint);
                    if (payload == null || payload.Length == 0) continue;

                    int count = payload.Length;
                    sequence++;

                    if (_compiledMap.ExpectedSize.HasValue &&
                        _compiledMap.ExpectedSize.Value != count)
                    {
                        _lengthMismatchCount++;
                    }

                    lock (_lengthsLock)
                    {
                        int seen;
                        _lengthCounts.TryGetValue(count, out seen);
                        _lengthCounts[count] = seen + 1;
                    }

                    var now = DateTime.UtcNow;
                    if (_lastPacketUtc != DateTime.MinValue && (now - _lastPacketUtc).TotalSeconds > _settings.StaleSeconds)
                    {
                        // Stale feed resumed: reset previous values to prevent false edge/fire events,
                        // but preserve session baselines across pauses
                        _prevPrimaryCount = null;
                        _prevSecondaryCount = null;
                        _prevHitCount = null;
                        _prevCollisionCount = null;
                    }

                    _lastLength = count;
                    _lastPacketUtc = now;
                    Interlocked.Increment(ref _packetCount);
                    Interlocked.Add(ref _byteCount, count);

                    Interlocked.Increment(ref _packetsInCurrentSecond);
                    var elapsed = (now - _rateWindowStartUtc).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        int pktCount = Interlocked.Exchange(ref _packetsInCurrentSecond, 0);
                        Volatile.Write(ref _smoothedPacketsPerSecond, pktCount / elapsed);
                        _rateWindowStartUtc = now;
                    }

                    if (_settings.DumpFirstPackets > 0 && Interlocked.Increment(ref _dumped) <= _settings.DumpFirstPackets)
                    {
                        global::SimHub.Logging.Current.Debug(
                            "[SimHubGRebels] packet #" + sequence + " len=" + count + " " + HexDump(payload));
                    }

                    // Zero-allocation decode into the pre-allocated back buffer
                    PacketParser.ParseInto(payload, _compiledMap, sequence, _backFrame);

                    UpdateWeaponPulses(_backFrame);
                    DetectEdges(_backFrame);

                    // Track previous velocity components for directional collision detection
                    if (_vxIdx >= 0) _prevVx = _backFrame.GetNumber(_vxIdx);
                    if (_vyIdx >= 0) _prevVy = _backFrame.GetNumber(_vyIdx);
                    if (_vzIdx >= 0) _prevVz = _backFrame.GetNumber(_vzIdx);

                    // Atomically publish back buffer as current frame and swap buffers
                    var temp = _current;
                    _current = _backFrame;
                    _backFrame = temp;
                }
                catch (ObjectDisposedException)
                {
                    break; // Stop() closed the socket out from under us
                }
                catch (SocketException ex)
                {
                    if (_stopRequested) break;
                    global::SimHub.Logging.Current.Warn("[SimHubGRebels] socket error: " + ex.Message);
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    if (_stopRequested) break;
                    global::SimHub.Logging.Current.Error("[SimHubGRebels] receive error: " + ex);
                    Thread.Sleep(50);
                }
            }
        }

        private void UpdateWeaponPulses(Frame frame)
        {
            if (_primaryCountIdx >= 0)
            {
                CheckWeaponCount(frame.GetNumber(_primaryCountIdx), ref _prevPrimaryCount, ref _sessionPrimaryCount, ref _lastPrimaryFireUtc, "PrimaryFired");
            }

            if (_secondaryCountIdx >= 0)
            {
                CheckWeaponCount(frame.GetNumber(_secondaryCountIdx), ref _prevSecondaryCount, ref _sessionSecondaryCount, ref _lastSecondaryFireUtc, "SecondaryFired");
            }

            if (_hitCountIdx >= 0)
            {
                double strength = _hitStrengthIdx >= 0 ? frame.GetNumber(_hitStrengthIdx) : 0.0;
                CheckHitCount(frame.GetNumber(_hitCountIdx), strength);
            }

            if (_collisionCountIdx >= 0)
            {
                double strength = _collisionStrengthIdx >= 0 ? frame.GetNumber(_collisionStrengthIdx) : 0.0;
                CheckCollisionCount(frame.GetNumber(_collisionCountIdx), strength, frame);
            }
        }

        private void CheckHitCount(double value, double strength)
        {
            if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value)) return;

            if (!_prevHitCount.HasValue)
            {
                _prevHitCount = value; // baseline only
                return;
            }

            double delta = value - _prevHitCount.Value;

            if (delta >= 1.0 && delta <= 20.0)
            {
                // Real hit event within valid combat range
                _lastHitFireUtc = DateTime.UtcNow;
                _lastHitStrength = strength;
                EnqueueEvent("HitReceived");
                _prevHitCount = value;
            }
            else
            {
                // delta < 0 (restart/reset) or delta > 20 (abnormal jump/re-sync) -> re-baseline without firing
                if (delta < 0.0)
                {
                    _lastHitStrength = 0.0;
                    _lastHitFireUtc = DateTime.MinValue;
                }
                _prevHitCount = value;
            }
        }

        private void CheckCollisionCount(double value, double strength, Frame frame)
        {
            if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value)) return;

            double absStrength = Math.Abs(strength);

            if (!_prevCollisionCount.HasValue)
            {
                _prevCollisionCount = value; // baseline only
                if (absStrength > 0.001) _lastCollisionStrength = absStrength;
                return;
            }

            double delta = value - _prevCollisionCount.Value;

            if (delta >= 1.0)
            {
                // Real collision impact event: fire trigger event and start pulse
                _lastCollisionFireUtc = DateTime.UtcNow;
                _lastCollisionStrength = absStrength > 0.001 ? absStrength : 15.0;

                // Determine directional impact vector from velocity delta
                DetermineCollisionDirection(frame);

                EnqueueEvent("CollisionReceived");
                _prevCollisionCount = value;
            }
            else
            {
                // delta == 0 (flying away in open space) -> do nothing (let pulse decay to 0)
                // delta < 0 (respawn / restart / destruction) -> clear fire timestamp and re-baseline
                if (delta < 0.0)
                {
                    _lastCollisionStrength = 0.0;
                    _lastCollisionFireUtc = DateTime.MinValue;
                    _lastCollisionDirection = "None";
                    _lastCollisionDirectionCode = 0;
                }
                _prevCollisionCount = value;
            }
        }

        private void DetermineCollisionDirection(Frame frame)
        {
            if (_prevVx.HasValue && _prevVy.HasValue && _prevVz.HasValue &&
                _vxIdx >= 0 && _vyIdx >= 0 && _vzIdx >= 0)
            {
                double dvx = frame.GetNumber(_vxIdx) - _prevVx.Value;
                double dvy = frame.GetNumber(_vyIdx) - _prevVy.Value;
                double dvz = frame.GetNumber(_vzIdx) - _prevVz.Value;

                double ax = Math.Abs(dvx);
                double ay = Math.Abs(dvy);
                double az = Math.Abs(dvz);

                if (ax > ay && ax > az)
                {
                    if (dvx < 0) { _lastCollisionDirection = "Front"; _lastCollisionDirectionCode = 1; }
                    else { _lastCollisionDirection = "Rear"; _lastCollisionDirectionCode = 2; }
                }
                else if (ay > ax && ay > az)
                {
                    if (dvy > 0) { _lastCollisionDirection = "Left"; _lastCollisionDirectionCode = 3; }
                    else { _lastCollisionDirection = "Right"; _lastCollisionDirectionCode = 4; }
                }
                else if (az > ax && az > ay)
                {
                    if (dvz < 0) { _lastCollisionDirection = "Bottom"; _lastCollisionDirectionCode = 5; }
                    else { _lastCollisionDirection = "Top"; _lastCollisionDirectionCode = 6; }
                }
                else
                {
                    _lastCollisionDirection = "Front";
                    _lastCollisionDirectionCode = 1;
                }
            }
            else
            {
                _lastCollisionDirection = "Front";
                _lastCollisionDirectionCode = 1;
            }
        }

        private void CheckWeaponCount(double value, ref double? prev, ref double sessionCount, ref DateTime lastFireUtc, string eventName)
        {
            // Allow 0 (valid initial missile counter at match start); ignore negative/invalid readings
            if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value)) return;

            if (!prev.HasValue)
            {
                prev = value; // first real reading of session: baseline reference
                return;
            }

            double delta = value - prev.Value;

            // 10 rounds max in a single 16ms frame (covers multi-barrel cannons / rapid fire / missile release)
            if (delta >= 1.0 && delta <= 10.0)
            {
                // Real increase = shot fired
                lastFireUtc = DateTime.UtcNow;
                EnqueueEvent(eventName);
                sessionCount += delta;
                prev = value;
            }
            else
            {
                // delta < 0 (rack reload / cycle / wrap) or delta > 10 (inter-match rebase/jump):
                // re-sync reference without firing and without wiping the accumulated session count
                prev = value;
            }
        }

        private void DetectEdges(Frame frame)
        {
            if (_edgeSpecs.Count == 0) return;

            foreach (var spec in _edgeSpecs)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Event) || spec.FieldIndex < 0)
                    continue;

                double now = frame.GetNumber(spec.FieldIndex);
                double before;
                _lastEdgeValue.TryGetValue(spec.Field, out before);

                bool rose = before < spec.Threshold && now >= spec.Threshold;
                _lastEdgeValue[spec.Field] = now;

                if (!rose) continue;

                DateTime lastFire;
                bool hasFired = _lastEdgeFire.TryGetValue(spec.Event, out lastFire);
                if (hasFired &&
                    (DateTime.UtcNow - lastFire).TotalMilliseconds < Math.Max(0, spec.DebounceMs))
                {
                    continue;
                }

                _lastEdgeFire[spec.Event] = DateTime.UtcNow;
                EnqueueEvent(spec.Event);
            }
        }

        private void EnqueueEvent(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            while (_pending.Count >= MaxPendingEvents)
            {
                PendingEvent discarded;
                _pending.TryDequeue(out discarded);
            }
            _pending.Enqueue(new PendingEvent { Name = name });
        }

        /// <summary>Drains events detected since the last call. Called from DataUpdate.</summary>
        public void DrainEvents(List<string> into)
        {
            PendingEvent evt;
            while (_pending.TryDequeue(out evt))
            {
                if (!string.IsNullOrEmpty(evt.Name)) into.Add(evt.Name);
            }
        }

        private static string HexDump(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("X2"));
                sb.Append(i == data.Length - 1 ? "" : " ");
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _stopRequested = true;
            var client = _client;
            _client = null;
            if (client != null)
            {
                try { client.Close(); } catch { }
            }

            var thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive)
            {
                try { thread.Join(500); } catch { }
            }
        }
    }
}
