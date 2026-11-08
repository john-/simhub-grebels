using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SimHub.GRebels
{
    /// <summary>
    /// Maps one decoded telemetry field onto one SimHub property.
    /// </summary>
    public sealed class PropertyMapEntry
    {
        /// <summary>SimHub property name, e.g. "SpeedKmh". Prefixed by the plugin name at runtime.</summary>
        public string Property;

        /// <summary>Field name as it appears in the "Fields" section of the layout.</summary>
        public string Field;

        public double Scale = 1.0;
        public double Bias = 0.0;
        public double Min = double.MinValue;
        public double Max = double.MaxValue;

        /// <summary>Pre-compiled index into the flat frame Numbers array for O(1) zero-allocation access.</summary>
        [JsonIgnore]
        public int FieldIndex = -1;
    }

    /// <summary>
    /// A transient condition worth raising as a SimHub event, e.g. a weapon firing.
    /// Edge detection happens on the receive thread so no rising edge is ever missed
    /// between SimHub's 60 Hz data updates.
    /// </summary>
    public sealed class EdgeEventSpec
    {
        /// <summary>SimHub event name to trigger.</summary>
        public string Event;

        /// <summary>Field name from the layout.</summary>
        public string Field;

        /// <summary>Threshold the field must cross to count as active.</summary>
        public double Threshold = 0.5;

        /// <summary>Ignore further triggers for this long after one fires.</summary>
        public int DebounceMs = 60;

        /// <summary>Pre-compiled index into the flat frame Numbers array for O(1) zero-allocation access.</summary>
        [JsonIgnore]
        public int FieldIndex = -1;
    }

    /// <summary>
    /// Plugin configuration, read from GRebelsSimHub.json next to the DLL.
    /// </summary>
    public sealed class PluginSettings
    {
        public int Port = 33001;
        public string BindAddress = "127.0.0.1";

        /// <summary>Optional IP filter. If non-empty, UDP packets from any other source IP are dropped.</summary>
        public string AllowedSenderIp = "";

        /// <summary>Treat the feed as stale after this many seconds without a packet.</summary>
        public double StaleSeconds = 1.0;

        /// <summary>Log the first N packets in hex, to help identify the layout.</summary>
        public int DumpFirstPackets = 20;

        /// <summary>Duration in ms to keep PrimaryFired and SecondaryFired active (100) after firing.</summary>
        public int WeaponPulseMs = 100;

        /// <summary>Duration in ms to keep HitFired active and decay HitImpulse after an incoming hit.</summary>
        public int HitPulseMs = 200;

        /// <summary>Reference hit strength value for normalising HitImpulse (default 25.0).</summary>
        public double HitStrengthRef = 25.0;

        /// <summary>Duration in ms to keep CollisionFired active and decay CollisionImpulse after a collision.</summary>
        public int CollisionPulseMs = 100;

        /// <summary>Reference collision strength value for normalising CollisionImpulse (default 150.0).</summary>
        public double CollisionStrengthRef = 150.0;

        /// <summary>The packet layout. Empty means "discovery mode": receive and report, map nothing.</summary>
        public FieldMap Fields = new FieldMap();

        /// <summary>Compiled representation of Fields for zero-allocation parsing and O(1) property lookup.</summary>
        [JsonIgnore]
        public CompiledFieldMap CompiledFields;

        public List<PropertyMapEntry> Properties = new List<PropertyMapEntry>();
        public List<EdgeEventSpec> EdgeEvents = new List<EdgeEventSpec>();

        /// <summary>
        /// Reads the config, falling back to defaults on any problem.
        ///
        /// A malformed config must never propagate: Init runs on SimHub's startup
        /// path and a throw here aborts plugin initialisation and blocks the UI from
        /// loading. Losing the field map is recoverable; a SimHub that won't start is not.
        /// </summary>
        /// <summary>
        /// Sanity-checks the configuration. Throws with a specific message on the
        /// first problem so a typo in the JSON is reported rather than silently
        /// producing nonsense telemetry.
        /// </summary>
        public void Validate()
        {
            if (Port < 1 || Port > 65535)
                throw new ArgumentException("Port " + Port + " is out of range 1-65535");

            if (StaleSeconds <= 0.0 || double.IsNaN(StaleSeconds) || double.IsInfinity(StaleSeconds))
                throw new ArgumentException("StaleSeconds must be greater than 0");

            if (WeaponPulseMs < 0)
                throw new ArgumentException("WeaponPulseMs cannot be negative");

            if (HitPulseMs < 0)
                throw new ArgumentException("HitPulseMs cannot be negative");

            if (HitStrengthRef <= 0.0 || double.IsNaN(HitStrengthRef) || double.IsInfinity(HitStrengthRef))
                throw new ArgumentException("HitStrengthRef must be greater than 0");

            if (CollisionPulseMs < 0)
                throw new ArgumentException("CollisionPulseMs cannot be negative");

            if (CollisionStrengthRef <= 0.0 || double.IsNaN(CollisionStrengthRef) || double.IsInfinity(CollisionStrengthRef))
                throw new ArgumentException("CollisionStrengthRef must be greater than 0");

            if (!string.IsNullOrWhiteSpace(AllowedSenderIp))
            {
                System.Net.IPAddress parsed;
                if (!System.Net.IPAddress.TryParse(AllowedSenderIp.Trim(), out parsed))
                {
                    throw new ArgumentException("AllowedSenderIp \"" + AllowedSenderIp + "\" is not a valid IP address");
                }
            }

            if (Fields == null)
            {
                Fields = new FieldMap();
            }

            if (Fields.Fields != null)
            {
                foreach (var kv in Fields.Fields)
                {
                    if (kv.Value == null) continue;
                    if (kv.Value.Offset < 0)
                        throw new ArgumentException("Field \"" + kv.Key + "\" has negative Offset " + kv.Value.Offset);

                    if (!FieldSpec.IsValidDataType(kv.Value.Type))
                        throw new ArgumentException("Field \"" + kv.Key + "\" has unrecognized Type \"" + kv.Value.Type + "\"");

                    if (string.Equals(kv.Value.Type, "string", StringComparison.OrdinalIgnoreCase) && kv.Value.Length <= 0)
                        throw new ArgumentException("String field \"" + kv.Key + "\" has non-positive Length " + kv.Value.Length);

                    if (double.IsNaN(kv.Value.Scale) || double.IsInfinity(kv.Value.Scale))
                        throw new ArgumentException("Field \"" + kv.Key + "\" has invalid Scale");

                    if (double.IsNaN(kv.Value.Bias) || double.IsInfinity(kv.Value.Bias))
                        throw new ArgumentException("Field \"" + kv.Key + "\" has invalid Bias");
                }
            }

            CompiledFields = new CompiledFieldMap(Fields);

            int? limit = Fields.LargestOffset;
            if (Fields.ExpectedSize.HasValue && limit.HasValue &&
                Fields.ExpectedSize.Value < limit.Value)
            {
                throw new ArgumentException(
                    "ExpectedSize " + Fields.ExpectedSize.Value +
                    " is smaller than the highest field end offset " + limit.Value +
                    " - the layout is inconsistent");
            }

            foreach (var entry in Properties ?? new List<PropertyMapEntry>())
            {
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.Property))
                    throw new ArgumentException("A Properties entry is missing \"Property\"");
                if (string.IsNullOrEmpty(entry.Field))
                    throw new ArgumentException(
                        "Properties entry \"" + entry.Property + "\" is missing \"Field\"");
                if (double.IsNaN(entry.Scale) || double.IsInfinity(entry.Scale))
                    throw new ArgumentException("Properties entry \"" + entry.Property + "\" has invalid Scale");
                if (double.IsNaN(entry.Bias) || double.IsInfinity(entry.Bias))
                    throw new ArgumentException("Properties entry \"" + entry.Property + "\" has invalid Bias");
                if (double.IsNaN(entry.Min) || double.IsNaN(entry.Max))
                    throw new ArgumentException("Properties entry \"" + entry.Property + "\" has NaN Min or Max");
                if (entry.Min > entry.Max)
                {
                    throw new ArgumentException(
                        "Properties entry \"" + entry.Property + "\" has Min greater than Max");
                }
                entry.FieldIndex = CompiledFields.GetFieldIndex(entry.Field);
            }

            foreach (var spec in EdgeEvents ?? new List<EdgeEventSpec>())
            {
                if (spec == null) continue;
                if (string.IsNullOrEmpty(spec.Event))
                    throw new ArgumentException("An EdgeEvents entry is missing \"Event\"");
                if (string.IsNullOrEmpty(spec.Field))
                    throw new ArgumentException(
                        "EdgeEvents entry \"" + spec.Event + "\" is missing \"Field\"");
                if (spec.DebounceMs < 0)
                    throw new ArgumentException("EdgeEvents entry \"" + spec.Event + "\" has negative DebounceMs");
                if (double.IsNaN(spec.Threshold) || double.IsInfinity(spec.Threshold))
                    throw new ArgumentException("EdgeEvents entry \"" + spec.Event + "\" has invalid Threshold");
                spec.FieldIndex = CompiledFields.GetFieldIndex(spec.Field);
            }
        }

        public static PluginSettings Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return new PluginSettings();

            try
            {
                var text = System.IO.File.ReadAllText(path);
                var jsonSettings = new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                    MaxDepth = 32
                };
                return JsonConvert.DeserializeObject<PluginSettings>(text, jsonSettings) ?? new PluginSettings();
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(
                    "[GRebelsSimHub] could not read " + path + " (" + ex.Message +
                    ") - continuing with defaults");
                return new PluginSettings();
            }
        }
    }
}
