using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace SimHub.GRebels
{
    /// <summary>
    /// Utility for formatting plugin and git version strings without WPF/SimHub dependencies.
    /// </summary>
    public static class VersionFormatter
    {
        public static string FormatVersionString(string info)
        {
            if (string.IsNullOrWhiteSpace(info)) return "Unknown";
            info = info.Trim();

            int plusIdx = info.IndexOf('+');
            if (plusIdx >= 0)
            {
                string ver = info.Substring(0, plusIdx).Trim();
                string commit = info.Substring(plusIdx + 1).Trim();
                bool isDirty = commit.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase);
                if (isDirty)
                {
                    commit = commit.Substring(0, commit.Length - "-dirty".Length);
                }
                if (commit.StartsWith("g", StringComparison.OrdinalIgnoreCase) && commit.Length > 8)
                {
                    commit = commit.Substring(1);
                }
                string shortCommit = commit.Length > 7 ? commit.Substring(0, 7) : commit;
                if (isDirty) shortCommit += "-dirty";

                if (string.IsNullOrEmpty(ver) || ver == "1.0.0.0" || ver == "0.0.0" || ver == "1.0.0")
                {
                    return !string.IsNullOrEmpty(shortCommit)
                        ? (!string.IsNullOrEmpty(ver) ? $"{ver} ({shortCommit})" : shortCommit)
                        : ver;
                }

                return !string.IsNullOrEmpty(shortCommit) ? $"{ver} ({shortCommit})" : ver;
            }

            // If it was generated via git describe (e.g. v1.0.1-6-gce78d2a or ...-dirty)
            if (info.StartsWith("v", StringComparison.OrdinalIgnoreCase) || info.Contains("-g") || info.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase))
            {
                // Strip the Git 'g' prefix before the commit hash (e.g. -gce78d2a -> -ce78d2a)
                return System.Text.RegularExpressions.Regex.Replace(info, @"-g(?=[0-9a-fA-F]{7,40})", "-");
            }

            return info;
        }
    }

    /// <summary>
    /// Supported primitive telemetry types inside the UDP packet.
    /// </summary>
    public enum TelemetryDataType
    {
        Float,
        Double,
        Int,
        UInt,
        Short,
        UShort,
        Byte,
        SByte,
        String
    }

    /// <summary>
    /// One received telemetry packet, parsed into a flat, pre-indexed array for zero-allocation access.
    ///
    /// The layout is defined by SimHub.GRebels.json and compiled into CompiledFieldMap at startup.
    /// Access by integer index is O(1) and performs no allocations or string hashing.
    /// </summary>
    public sealed class Frame
    {
        public readonly double[] Numbers;
        public readonly string[] Strings;
        public readonly CompiledFieldMap Map;
        public long Sequence;
        public DateTime ReceivedUtc;
        public int Length;

        public Frame(CompiledFieldMap map)
        {
            Map = map;
            int numCount = map?.NumericFields?.Length ?? 0;
            int strCount = map?.StringFields?.Length ?? 0;
            Numbers = new double[numCount];
            Strings = new string[strCount];
            Sequence = 0;
            Length = 0;
            ReceivedUtc = DateTime.MinValue;
        }

        public double GetNumber(int index)
        {
            if (Numbers != null && index >= 0 && index < Numbers.Length)
                return Numbers[index];
            return 0.0;
        }

        public string GetString(int index)
        {
            if (Strings != null && index >= 0 && index < Strings.Length)
                return Strings[index] ?? string.Empty;
            return string.Empty;
        }

        public bool HasData
        {
            get { return ReceivedUtc != DateTime.MinValue; }
        }

        public double AgeSeconds
        {
            get
            {
                if (ReceivedUtc == DateTime.MinValue)
                    return double.PositiveInfinity;
                double seconds = (DateTime.UtcNow - ReceivedUtc).TotalSeconds;
                return seconds < 0.0 ? 0.0 : seconds;
            }
        }
    }

    /// <summary>
    /// Describes one field's position and encoding inside the UDP payload.
    /// Loaded from SimHub.GRebels.json.
    /// </summary>
    public sealed class FieldSpec
    {
        /// <summary>float | double | int | uint | short | ushort | byte | sbyte | string</summary>
        public string Type = "float";

        /// <summary>Byte offset from the start of the payload.</summary>
        public int Offset;

        /// <summary>Byte length. Only meaningful for Type = "string".</summary>
        public int Length = 32;

        /// <summary>Applied after decoding: value * Scale + Bias.</summary>
        public double Scale = 1.0;
        public double Bias = 0.0;

        [JsonIgnore]
        private TelemetryDataType? _parsedType;

        [JsonIgnore]
        public TelemetryDataType ParsedType
        {
            get
            {
                if (!_parsedType.HasValue)
                    _parsedType = ParseDataType(Type);
                return _parsedType.Value;
            }
        }

        [JsonIgnore]
        public int ByteSize
        {
            get
            {
                switch (ParsedType)
                {
                    case TelemetryDataType.Double: return 8;
                    case TelemetryDataType.Int:
                    case TelemetryDataType.UInt: return 4;
                    case TelemetryDataType.Short:
                    case TelemetryDataType.UShort: return 2;
                    case TelemetryDataType.Byte:
                    case TelemetryDataType.SByte: return 1;
                    case TelemetryDataType.String: return Length;
                    default: return 4;
                }
            }
        }

        public static bool IsValidDataType(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            switch (type.ToLowerInvariant())
            {
                case "float":
                case "double":
                case "int":
                case "int32":
                case "uint":
                case "uint32":
                case "short":
                case "int16":
                case "ushort":
                case "uint16":
                case "byte":
                case "sbyte":
                case "string":
                    return true;
                default:
                    return false;
            }
        }

        public static TelemetryDataType ParseDataType(string type)
        {
            if (string.IsNullOrEmpty(type)) return TelemetryDataType.Float;
            switch (type.ToLowerInvariant())
            {
                case "double": return TelemetryDataType.Double;
                case "int":
                case "int32": return TelemetryDataType.Int;
                case "uint":
                case "uint32": return TelemetryDataType.UInt;
                case "short":
                case "int16": return TelemetryDataType.Short;
                case "ushort":
                case "uint16": return TelemetryDataType.UShort;
                case "byte": return TelemetryDataType.Byte;
                case "sbyte": return TelemetryDataType.SByte;
                case "string": return TelemetryDataType.String;
                default: return TelemetryDataType.Float;
            }
        }
    }

    /// <summary>
    /// Compiled, immutable field specification for zero-allocation parsing.
    /// </summary>
    public sealed class CompiledFieldSpec
    {
        public int Index;
        public string Name;
        public TelemetryDataType Type;
        public int Offset;
        public int Length;
        public int ByteSize;
        public double Scale;
        public double Bias;
    }

    /// <summary>
    /// Compiled schema mapping field names to flat array indices.
    /// </summary>
    public sealed class CompiledFieldMap
    {
        public readonly CompiledFieldSpec[] NumericFields;
        public readonly CompiledFieldSpec[] StringFields;
        public readonly Dictionary<string, int> FieldNameToIndex;
        public readonly Dictionary<string, int> StringNameToIndex;
        public readonly int? ExpectedSize;

        public CompiledFieldMap(FieldMap map)
        {
            FieldNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            StringNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (map == null || map.Fields == null)
            {
                NumericFields = new CompiledFieldSpec[0];
                StringFields = new CompiledFieldSpec[0];
                ExpectedSize = null;
                return;
            }

            ExpectedSize = map.ExpectedSize;
            var numList = new List<CompiledFieldSpec>();
            var strList = new List<CompiledFieldSpec>();

            foreach (var kv in map.Fields)
            {
                var name = kv.Key;
                var spec = kv.Value;
                if (spec == null) continue;

                var parsedType = spec.ParsedType;
                if (parsedType == TelemetryDataType.String)
                {
                    int idx = strList.Count;
                    var c = new CompiledFieldSpec
                    {
                        Index = idx,
                        Name = name,
                        Type = parsedType,
                        Offset = spec.Offset,
                        Length = spec.Length,
                        ByteSize = spec.ByteSize,
                        Scale = spec.Scale,
                        Bias = spec.Bias
                    };
                    strList.Add(c);
                    StringNameToIndex[name] = idx;
                }
                else
                {
                    int idx = numList.Count;
                    var c = new CompiledFieldSpec
                    {
                        Index = idx,
                        Name = name,
                        Type = parsedType,
                        Offset = spec.Offset,
                        Length = spec.Length,
                        ByteSize = spec.ByteSize,
                        Scale = spec.Scale,
                        Bias = spec.Bias
                    };
                    numList.Add(c);
                    FieldNameToIndex[name] = idx;
                }
            }

            NumericFields = numList.ToArray();
            StringFields = strList.ToArray();
        }

        public int GetFieldIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int idx;
            return FieldNameToIndex.TryGetValue(name, out idx) ? idx : -1;
        }

        public int GetStringIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int idx;
            return StringNameToIndex.TryGetValue(name, out idx) ? idx : -1;
        }
    }

    /// <summary>
    /// The packet layout contract. Loaded from the "Fields" section of the
    /// plugin's JSON config.
    /// </summary>
    public sealed class FieldMap
    {
        public Dictionary<string, FieldSpec> Fields = new Dictionary<string, FieldSpec>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// If set, packets whose length differs are still parsed but the mismatch
        /// is counted and logged.
        /// </summary>
        public int? ExpectedSize;

        [JsonIgnore]
        public int? LargestOffset
        {
            get
            {
                if (Fields == null) return null;
                int max = 0;
                foreach (var f in Fields.Values)
                {
                    if (f == null) continue;
                    int end = f.Offset + f.ByteSize;
                    if (end > max) max = end;
                }
                return Fields.Count == 0 ? (int?)null : max;
            }
        }
    }

    /// <summary>
    /// Decodes a UDP payload according to a CompiledFieldMap. Every read is bounds checked;
    /// an out-of-range field is skipped rather than throwing.
    /// </summary>
    public static class PacketParser
    {
        /// <summary>
        /// Zero-allocation decoding into a pre-allocated target Frame.
        /// </summary>
        public static void ParseInto(byte[] data, CompiledFieldMap map, long sequence, Frame target)
        {
            if (target == null || map == null) return;
            target.Sequence = sequence;
            target.Length = data != null ? data.Length : 0;
            target.ReceivedUtc = DateTime.UtcNow;

            if (data == null) return;

            var numSpecs = map.NumericFields;
            var numbers = target.Numbers;
            int numLen = numSpecs.Length;

            for (int i = 0; i < numLen; i++)
            {
                var spec = numSpecs[i];
                if (TryReadNumber(data, spec, out double raw))
                {
                    double val = raw * spec.Scale + spec.Bias;
                    numbers[i] = (double.IsNaN(val) || double.IsInfinity(val)) ? 0.0 : val;
                }
                else
                {
                    numbers[i] = 0.0;
                }
            }

            var strSpecs = map.StringFields;
            var strings = target.Strings;
            int strLen = strSpecs.Length;
            for (int i = 0; i < strLen; i++)
            {
                var spec = strSpecs[i];
                if (TryReadString(data, spec, out string text))
                {
                    strings[i] = text;
                }
                else
                {
                    strings[i] = string.Empty;
                }
            }
        }

        private static bool TryReadNumber(byte[] data, CompiledFieldSpec spec, out double value)
        {
            value = 0.0;
            if (data == null || spec == null) return false;
            int size = spec.ByteSize;
            if (size <= 0 || spec.Offset < 0 || spec.Offset > data.Length - size) return false;

            switch (spec.Type)
            {
                case TelemetryDataType.Double:
                    value = BitConverter.ToDouble(data, spec.Offset);
                    return !double.IsNaN(value) && !double.IsInfinity(value);
                case TelemetryDataType.Int:
                    value = BitConverter.ToInt32(data, spec.Offset);
                    return true;
                case TelemetryDataType.UInt:
                    value = BitConverter.ToUInt32(data, spec.Offset);
                    return true;
                case TelemetryDataType.Short:
                    value = BitConverter.ToInt16(data, spec.Offset);
                    return true;
                case TelemetryDataType.UShort:
                    value = BitConverter.ToUInt16(data, spec.Offset);
                    return true;
                case TelemetryDataType.Byte:
                    value = data[spec.Offset];
                    return true;
                case TelemetryDataType.SByte:
                    value = (sbyte)data[spec.Offset];
                    return true;
                case TelemetryDataType.Float:
                    value = BitConverter.ToSingle(data, spec.Offset);
                    return !double.IsNaN(value) && !double.IsInfinity(value);
                default:
                    return false;
            }
        }

        private static bool TryReadString(byte[] data, CompiledFieldSpec spec, out string text)
        {
            text = string.Empty;
            if (data == null || spec == null) return false;
            int len = spec.Length;
            if (len <= 0 || spec.Offset < 0 || spec.Offset > data.Length - len) return false;

            int end = spec.Offset;
            int limit = spec.Offset + len;
            while (end < limit && data[end] != 0) end++;

            if (end == spec.Offset) return true;

            string rawText = Encoding.UTF8.GetString(data, spec.Offset, end - spec.Offset).Trim();
            var sb = new StringBuilder(rawText.Length);
            for (int i = 0; i < rawText.Length; i++)
            {
                char c = rawText[i];
                if (!char.IsControl(c) || c == '\t' || c == '\r' || c == '\n')
                {
                    sb.Append(c);
                }
            }
            text = sb.ToString();
            return true;
        }
    }
}
