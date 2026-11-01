using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace SimHub.GRebels
{
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
    /// Access by string name is provided for backward compatibility.
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

        public Frame(Dictionary<string, double> numbers, Dictionary<string, string> strings, long sequence, int length)
        {
            Sequence = sequence;
            Length = length;
            ReceivedUtc = DateTime.UtcNow;

            var map = new FieldMap();
            if (numbers != null)
            {
                foreach (var k in numbers.Keys) map.Fields[k] = new FieldSpec { Type = "double" };
            }
            if (strings != null)
            {
                foreach (var k in strings.Keys) map.Fields[k] = new FieldSpec { Type = "string" };
            }
            Map = new CompiledFieldMap(map);

            Numbers = new double[Map.NumericFields.Length];
            Strings = new string[Map.StringFields.Length];

            if (numbers != null)
            {
                foreach (var kv in numbers)
                {
                    int idx = Map.GetFieldIndex(kv.Key);
                    if (idx >= 0) Numbers[idx] = kv.Value;
                }
            }
            if (strings != null)
            {
                foreach (var kv in strings)
                {
                    int idx = Map.GetStringIndex(kv.Key);
                    if (idx >= 0) Strings[idx] = kv.Value;
                }
            }
        }

        public double GetNumber(int index)
        {
            if (Numbers != null && index >= 0 && index < Numbers.Length)
                return Numbers[index];
            return 0.0;
        }

        public double GetNumber(string name)
        {
            if (Map != null)
            {
                int idx = Map.GetFieldIndex(name);
                if (idx >= 0) return Numbers[idx];
            }
            return 0.0;
        }

        public bool Has(string name)
        {
            if (Map != null)
            {
                return Map.FieldNameToIndex.ContainsKey(name) || Map.StringNameToIndex.ContainsKey(name);
            }
            return false;
        }

        public bool Has(int index)
        {
            return index >= 0 && Numbers != null && index < Numbers.Length;
        }

        public string GetString(int index)
        {
            if (Strings != null && index >= 0 && index < Strings.Length)
                return Strings[index] ?? string.Empty;
            return string.Empty;
        }

        public string GetString(string name)
        {
            if (Map != null)
            {
                int idx = Map.GetStringIndex(name);
                if (idx >= 0 && Strings != null && idx < Strings.Length)
                    return Strings[idx] ?? string.Empty;
            }
            return string.Empty;
        }

        public double AgeSeconds
        {
            get { return (DateTime.UtcNow - ReceivedUtc).TotalSeconds; }
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
                int max = 0;
                foreach (var f in Fields.Values)
                {
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
                    numbers[i] = raw * spec.Scale + spec.Bias;
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

        /// <summary>
        /// Backward-compatible overload allocating a new Frame.
        /// </summary>
        public static Frame Parse(byte[] data, FieldMap map, long sequence)
        {
            var compiled = new CompiledFieldMap(map);
            var frame = new Frame(compiled);
            ParseInto(data, compiled, sequence, frame);
            return frame;
        }

        private static bool TryReadNumber(byte[] data, CompiledFieldSpec spec, out double value)
        {
            value = 0.0;
            int size = spec.ByteSize;
            if (size <= 0 || spec.Offset < 0 || spec.Offset + size > data.Length) return false;

            switch (spec.Type)
            {
                case TelemetryDataType.Double:
                    value = BitConverter.ToDouble(data, spec.Offset);
                    return true;
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
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryReadString(byte[] data, CompiledFieldSpec spec, out string text)
        {
            text = string.Empty;
            int len = spec.Length;
            if (len <= 0 || spec.Offset < 0 || spec.Offset + len > data.Length) return false;

            int end = spec.Offset;
            int limit = spec.Offset + len;
            while (end < limit && data[end] != 0) end++;

            if (end == spec.Offset) return true;

            text = Encoding.UTF8.GetString(data, spec.Offset, end - spec.Offset).Trim();
            return true;
        }
    }
}
