using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameReaderCommon;
using SimHub.Plugins;

namespace SimHub.GRebels
{
    /// <summary>
    /// SimHub plugin for G-Rebels (Steam appid 2445980).
    ///
    /// G-Rebels broadcasts telemetry over UDP using a modified Prismatic Studios
    /// Motion Platform Linker. This plugin binds the game's telemetry port, decodes
    /// the payload against a JSON-supplied field map, and republishes the values as
    /// SimHub properties and events.
    ///
    /// Official telemetry specification: https://reakktor.com/htm/gr_telemetry.htm
    /// </summary>
    [PluginDescription("Reads G-Rebels UDP telemetry and exposes it to SimHub. GitHub: https://github.com/john-/simhub-grebels")]
    [PluginAuthor("John")]
    [PluginName("G-Rebels Telemetry")]
    public class GRebelsTelemetryPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PropertyPrefix = "GRebels.";

        public static string PluginVersion { get; } = ResolvePluginVersion();

        private static ImageSource _cachedIcon;

        private TelemetryReceiver _receiver;
        private PluginSettings _settings;
        private string _configPath;
        private readonly List<string> _eventBuffer = new List<string>(16);
        private SettingsControl _settingsControl;

        public PluginManager PluginManager { get; set; }
        public TelemetryReceiver Receiver => _receiver;
        public PluginSettings Settings => _settings;
        public bool IsFeedStale => IsStale();

        public ImageSource PictureIcon
        {
            get
            {
                if (_cachedIcon == null)
                {
                    try
                    {
                        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                        using (var stream = assembly.GetManifestResourceStream("SimHub.GRebels.assets.menu_icon.png"))
                        {
                            if (stream != null)
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.StreamSource = stream;
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                bmp.Freeze();
                                _cachedIcon = bmp;
                            }
                        }
                    }
                    catch
                    {
                        // Fallback safely if resource stream loading is unavailable
                    }
                }
                return _cachedIcon;
            }
        }

        public string LeftMenuTitle { get { return "G-Rebels"; } }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            if (_settingsControl == null)
            {
                _settingsControl = new SettingsControl(this);
            }
            return _settingsControl;
        }

        public void Init(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[SimHubGRebels] Initializing plugin version " + PluginVersion);

            _configPath = Path.Combine(
                Path.GetDirectoryName(typeof(GRebelsTelemetryPlugin).Assembly.Location) ?? ".",
                "SimHub.GRebels.json");

            _settings = PluginSettings.Load(_configPath);
            global::SimHub.Logging.Current.Info(
                "[SimHubGRebels] config: " + _configPath +
                (File.Exists(_configPath) ? "" : " (missing, using defaults)"));

            try
            {
                _settings.Validate();
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(
                    "[SimHubGRebels] invalid configuration: " + ex.Message +
                    " - continuing with the settings that are valid");
            }

            AttachDiagnostics();
            AttachConfiguredProperties();
            AttachConfiguredEvents();

            _receiver = new TelemetryReceiver(_settings);
            string error;
            if (!_receiver.Start(out error))
            {
                global::SimHub.Logging.Current.Error(
                    "[SimHubGRebels] could not bind UDP " + _settings.BindAddress + ":" +
                    _settings.Port + " - " + error);
                return;
            }

            WarnIfExposed();

            int fieldCount = _settings.Fields?.Fields?.Count ?? 0;
            global::SimHub.Logging.Current.Info(
                "[SimHubGRebels] listening on UDP " + _settings.BindAddress + ":" + _settings.Port +
                ", " + fieldCount + " field(s) mapped" +
                (fieldCount == 0 ? " (discovery mode)" : ""));

            if (fieldCount == 0)
            {
                global::SimHub.Logging.Current.Warn(
                    "[SimHubGRebels] no field map configured - packets will be received and " +
                    "counted, but no game values will be published yet.");
            }
        }

        /// <summary>
        /// The game's UDP feed is unauthenticated. Binding beyond loopback lets any host that can
        /// reach the port feed this plugin telemetry, so say so in the log.
        /// </summary>
        internal void WarnIfExposed()
        {
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse((_settings.BindAddress ?? "127.0.0.1").Trim(), out addr)) return;
            if (System.Net.IPAddress.IsLoopback(addr)) return;

            if (string.IsNullOrWhiteSpace(_settings.AllowedSenderIp))
            {
                global::SimHub.Logging.Current.Warn(
                    "[SimHubGRebels] SECURITY: listening on non-loopback address " + _settings.BindAddress +
                    " with no AllowedSenderIp. Any host that can reach UDP port " + _settings.Port +
                    " can send telemetry to SimHub. Set AllowedSenderIp to the game PC and firewall the port.");
            }
            else
            {
                global::SimHub.Logging.Current.Info(
                    "[SimHubGRebels] listening on non-loopback address; accepting only " + _settings.AllowedSenderIp +
                    ". Note that UDP source addresses can be spoofed, so also firewall the port.");
            }
        }

        public void End(PluginManager pluginManager)
        {
            if (_receiver != null)
            {
                _receiver.Dispose();
                _receiver = null;
            }
        }

        /// <summary>
        /// On SimHub's critical path. Deliberately does nothing but drain and fire
        /// events that the receive thread already detected.
        /// </summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (_receiver == null) return;

            _eventBuffer.Clear();
            _receiver.DrainEvents(_eventBuffer);

            for (int i = 0; i < _eventBuffer.Count; i++)
            {
                this.TriggerEvent(_eventBuffer[i]);
            }
        }

        private int _collisionStrengthIdx = -1;
        private int _fwdIndex = -1;
        private int _vertIndex = -1;
        private int _latIndex = -1;

        private void AttachDiagnostics()
        {
            var compiled = _settings?.CompiledFields;
            if (compiled != null)
            {
                _collisionStrengthIdx = compiled.GetFieldIndex("CollisionStrength");
                _fwdIndex = compiled.GetFieldIndex("ThrustMain");
                _vertIndex = compiled.GetFieldIndex("ThrustVertical");
                _latIndex = compiled.GetFieldIndex("ThrustLateral");
            }

            Attach("PacketCount", () => (object)(_receiver?.PacketCount ?? 0));
            Attach("PluginVersion", () => (object)PluginVersion);
            Attach("PacketRate", () => (object)(_receiver?.PacketsPerSecond ?? 0.0));
            Attach("LastPacketSize", () => (object)(_receiver?.LastLength ?? 0));
            Attach("PacketLengths", () => (object)(_receiver?.DescribeLengths() ?? "none"));
            Attach("FeedAge", () => (object)(_receiver == null || !_receiver.Current.HasData ? 0.0 : _receiver.Current.AgeSeconds));
            Attach("FeedStale", () => (object)IsStale());
            Attach("LayoutMismatchCount", () => (object)(_receiver?.LengthMismatchCount ?? 0));
            Attach("PrimaryFired", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.PrimaryFired));
            Attach("SecondaryFired", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.SecondaryFired));
            Attach("PrimaryCountSession", () => (object)(_receiver == null ? 0.0 : _receiver.SessionPrimaryCount));
            Attach("SecondaryCountSession", () => (object)(_receiver == null ? 0.0 : _receiver.SessionSecondaryCount));
            Attach("HitFired", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.HitFired));
            Attach("HitImpulse", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.HitImpulse));
            Attach("CollisionFired", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.CollisionFired));
            Attach("CollisionImpulse", () => (object)(_receiver == null || IsStale() ? 0.0 : _receiver.CollisionImpulse));
            Attach("CollisionLastStrength", () => (object)(_receiver == null ? 0.0 : _receiver.LastCollisionStrength));
            Attach("CollisionStrengthAbs", () =>
            {
                if (_receiver == null || IsStale()) return (object)0.0;
                double str = _collisionStrengthIdx >= 0 ? _receiver.Current.GetNumber(_collisionStrengthIdx) : 0.0;
                if (double.IsNaN(str) || double.IsInfinity(str)) return (object)0.0;
                return (object)Math.Abs(str);
            });
            Attach("CollisionDirection", () => (object)(_receiver == null || IsStale() ? "None" : _receiver.CollisionDirection));
            Attach("CollisionDirectionCode", () => (object)(_receiver == null || IsStale() ? 0 : _receiver.CollisionDirectionCode));
            Attach("ThrottleTotalPercent", () => (object)GetThrottleTotalPercent());
        }

        private double GetThrottleTotalPercent()
        {
            if (_receiver == null || IsStale()) return 0.0;
            double fwd = _fwdIndex >= 0 ? _receiver.Current.GetNumber(_fwdIndex) : 0.0;
            double vert = _vertIndex >= 0 ? _receiver.Current.GetNumber(_vertIndex) : 0.0;
            double lat = _latIndex >= 0 ? _receiver.Current.GetNumber(_latIndex) : 0.0;

            if (double.IsNaN(fwd) || double.IsInfinity(fwd)) fwd = 0.0;
            if (double.IsNaN(vert) || double.IsInfinity(vert)) vert = 0.0;
            if (double.IsNaN(lat) || double.IsInfinity(lat)) lat = 0.0;

            double mag = Math.Sqrt(fwd * fwd + vert * vert + lat * lat);
            if (double.IsNaN(mag) || double.IsInfinity(mag)) return 0.0;
            return Math.Min(100.0, mag * 100.0);
        }

        private void AttachConfiguredProperties()
        {
            if (_settings.Properties == null) return;

            foreach (var entry in _settings.Properties)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Property) || string.IsNullOrEmpty(entry.Field))
                    continue;

                var captured = entry;
                if (captured.IsString)
                {
                    Attach(captured.Property, () => (object)ReadStringMapped(captured));
                }
                else
                {
                    Attach(captured.Property, () => (object)ReadMapped(captured));
                }
            }
        }

        internal string ReadStringMapped(PropertyMapEntry entry)
        {
            if (_receiver == null || IsStale()) return string.Empty;
            return entry.StringIndex >= 0 ? _receiver.Current.GetString(entry.StringIndex) : string.Empty;
        }

        private void AttachConfiguredEvents()
        {
            // Register built-in weapon and hit events
            this.AddEvent(eventName: "PrimaryFired");
            this.AddEvent(eventName: "SecondaryFired");
            this.AddEvent(eventName: "HitReceived");
            this.AddEvent(eventName: "CollisionReceived");

            if (_settings.EdgeEvents == null) return;

            foreach (var spec in _settings.EdgeEvents)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Event)) continue;
                this.AddEvent(eventName: spec.Event);
            }
        }

        internal double ReadMapped(PropertyMapEntry entry)
        {
            if (_receiver == null) return 0.0;

            double raw = entry.FieldIndex >= 0
                ? _receiver.Current.GetNumber(entry.FieldIndex)
                : 0.0;

            if (double.IsNaN(raw) || double.IsInfinity(raw))
                return 0.0;

            if (entry.Scale != 1.0) raw *= entry.Scale;
            if (entry.Bias != 0.0) raw += entry.Bias;

            if (double.IsNaN(raw) || double.IsInfinity(raw))
                return 0.0;

            if (raw < entry.Min) raw = entry.Min;
            else if (raw > entry.Max) raw = entry.Max;

            return raw;
        }

        private bool IsStale()
        {
            if (_receiver == null || !_receiver.Current.HasData) return true;
            return _receiver.Current.AgeSeconds > _settings.StaleSeconds;
        }

        private void Attach(string name, Func<object> provider)
        {
            this.AttachDelegate(name: PropertyPrefix + name, valueProvider: provider);
        }

        private static string ResolvePluginVersion()
        {
            try
            {
                var assembly = typeof(GRebelsTelemetryPlugin).Assembly;
                var infoAttr = (System.Reflection.AssemblyInformationalVersionAttribute)
                    Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));

                if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
                {
                    return VersionFormatter.FormatVersionString(infoAttr.InformationalVersion);
                }

                var verAttr = assembly.GetName().Version;
                if (verAttr != null)
                {
                    return verAttr.ToString();
                }
            }
            catch
            {
                // Fallback gracefully if reflection fails
            }

            return "Unknown";
        }
    }
}
