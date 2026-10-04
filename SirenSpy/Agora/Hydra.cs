using System.Buffers.Binary;
using System.Text;

namespace SirenSpy.Agora
{
	// Agora "hydra" binary serialization (Content-Type: application/x-hydra-binary).
	// Reversed from the Agora SDK 0.13.2 in the Gotham City Impostors PS3 EBOOT (agBufferOperations.cpp / agByteBuffer.cpp).
	// Every value is a 1 byte type tag followed by its payload, all integers big-endian.
	public enum HydraType : byte
	{
		None = 1,       // no payload
		Int32 = 2,      // 4 bytes
		Binary = 3,     // int32 length + bytes
		Bool = 4,       // 1 byte (0/1)
		UByte = 5,      // 1 byte
		Float64 = 6,    // 8 bytes
		DateTime = 7,   // uint32 (unix seconds)
		Array = 8,      // int32 count + typed values
		Struct = 9,     // same layout as HashMap
		Int64 = 10,     // 8 bytes
		Bitstruct = 11, // uint32
		HashMap = 12,   // int32 count + (typed key, typed value) pairs
		UInt64 = 13,    // 8 bytes
		Utf8 = 14,      // int32 length + bytes (no terminator)
	}

	public abstract class HValue
	{
		public abstract HydraType Type { get; }

		public static implicit operator HValue(int v) => new HInt32(v);
		public static implicit operator HValue(long v) => new HInt64(v);
		public static implicit operator HValue(ulong v) => new HUInt64(v);
		public static implicit operator HValue(bool v) => new HBool(v);
		public static implicit operator HValue(double v) => new HFloat64(v);
		public static implicit operator HValue(string v) => new HUtf8(v);
		public static implicit operator HValue(byte[] v) => new HBinary(v);

		// Loose accessors used by the handlers when reading request arguments
		public virtual long AsLong() => throw new InvalidCastException($"{Type} is not numeric");
		public virtual string AsString() => ToString();
	}

	public sealed class HNone : HValue
	{
		public static readonly HNone Instance = new();
		public override HydraType Type => HydraType.None;
		public override string ToString() => "None";
	}

	public sealed class HInt32(int value) : HValue
	{
		public int Value { get; } = value;
		public override HydraType Type => HydraType.Int32;
		public override long AsLong() => Value;
		public override string AsString() => Value.ToString();
		public override string ToString() => $"Int32({Value})";
	}

	public sealed class HInt64(long value) : HValue
	{
		public long Value { get; } = value;
		public override HydraType Type => HydraType.Int64;
		public override long AsLong() => Value;
		public override string AsString() => Value.ToString();
		public override string ToString() => $"Int64({Value})";
	}

	public sealed class HUInt64(ulong value) : HValue
	{
		public ulong Value { get; } = value;
		public override HydraType Type => HydraType.UInt64;
		public override long AsLong() => unchecked((long)Value);
		public override string AsString() => Value.ToString();
		public override string ToString() => $"UInt64({Value})";
	}

	public sealed class HBool(bool value) : HValue
	{
		public bool Value { get; } = value;
		public override HydraType Type => HydraType.Bool;
		public override long AsLong() => Value ? 1 : 0;
		public override string AsString() => Value ? "true" : "false";
		public override string ToString() => $"Bool({Value})";
	}

	public sealed class HUByte(byte value) : HValue
	{
		public byte Value { get; } = value;
		public override HydraType Type => HydraType.UByte;
		public override long AsLong() => Value;
		public override string AsString() => Value.ToString();
		public override string ToString() => $"UByte({Value})";
	}

	public sealed class HFloat64(double value) : HValue
	{
		public double Value { get; } = value;
		public override HydraType Type => HydraType.Float64;
		public override long AsLong() => (long)Value;
		public override string AsString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		public override string ToString() => $"Float64({AsString()})";
	}

	public sealed class HDateTime(uint value) : HValue
	{
		public uint Value { get; } = value;
		public override HydraType Type => HydraType.DateTime;
		public override long AsLong() => Value;
		public override string AsString() => Value.ToString();
		public override string ToString() => $"DateTime({Value})";
		public static HDateTime Now() => new((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	public sealed class HBitstruct(uint value) : HValue
	{
		public uint Value { get; } = value;
		public override HydraType Type => HydraType.Bitstruct;
		public override long AsLong() => Value;
		public override string AsString() => Value.ToString();
		public override string ToString() => $"Bitstruct({Value})";
	}

	public sealed class HBinary(byte[] value) : HValue
	{
		public byte[] Value { get; } = value;
		public override HydraType Type => HydraType.Binary;
		public override string AsString() => Encoding.UTF8.GetString(Value);
		public override string ToString() => $"Binary[{Value.Length}]({Convert.ToHexString(Value.AsSpan(0, Math.Min(Value.Length, 64)))}{(Value.Length > 64 ? "..." : "")})";
	}

	public sealed class HUtf8(string value) : HValue
	{
		public string Value { get; } = value;
		public override HydraType Type => HydraType.Utf8;
		public override long AsLong() => long.Parse(Value);
		public override string AsString() => Value;
		public override string ToString() => $"\"{Value}\"";
	}

	public sealed class HArray : HValue, System.Collections.IEnumerable
	{
		public List<HValue> Items { get; } = new();
		public HArray() { }
		public HArray(IEnumerable<HValue> items) { Items.AddRange(items); }
		public override HydraType Type => HydraType.Array;
		public int Count => Items.Count;
		public HValue this[int i] => Items[i];
		public void Add(HValue v) => Items.Add(v);
		public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();
		public override string ToString() => "[" + string.Join(", ", Items) + "]";
	}

	// HashMap (12) and Struct (9) share a layout. The game's lookup helper only accepts HashMap, so that's the default.
	public sealed class HMap : HValue, System.Collections.IEnumerable
	{
		private readonly HydraType _type;
		public List<KeyValuePair<HValue, HValue>> Entries { get; } = new();
		public HMap(HydraType type = HydraType.HashMap) { _type = type; }
		public override HydraType Type => _type;

		public HValue? this[string key]
		{
			get
			{
				foreach (var kv in Entries)
					if (kv.Key is HUtf8 s && s.Value == key || kv.Key is HBinary b && Encoding.UTF8.GetString(b.Value) == key)
						return kv.Value;
				return null;
			}
			set
			{
				Entries.RemoveAll(kv => kv.Key is HUtf8 s && s.Value == key);
				if (value != null) Entries.Add(new(new HUtf8(key), value));
			}
		}

		// Collection initializer support: new HMap { { "guid", 1L }, { "name", "x" } }
		public void Add(string key, HValue value) => Entries.Add(new(new HUtf8(key), value));
		public void Add(HValue key, HValue value) => Entries.Add(new(key, value));
		public bool ContainsKey(string key) => this[key] != null;
		public System.Collections.IEnumerator GetEnumerator() => Entries.GetEnumerator();

		public override string ToString() => "{" + string.Join(", ", Entries.Select(kv => $"{kv.Key}: {kv.Value}")) + "}";
	}

	public static class Hydra
	{
		public const string ContentType = "application/x-hydra-binary";

		public static byte[] Serialize(params HValue[] values)
		{
			using var ms = new MemoryStream();
			foreach (var v in values) Write(ms, v);
			return ms.ToArray();
		}

		public static void Write(Stream s, HValue v)
		{
			s.WriteByte((byte)v.Type);
			Span<byte> buf = stackalloc byte[8];
			switch (v)
			{
				case HNone:
					break;
				case HInt32 i:
					BinaryPrimitives.WriteInt32BigEndian(buf, i.Value); s.Write(buf[..4]);
					break;
				case HInt64 l:
					BinaryPrimitives.WriteInt64BigEndian(buf, l.Value); s.Write(buf);
					break;
				case HUInt64 ul:
					BinaryPrimitives.WriteUInt64BigEndian(buf, ul.Value); s.Write(buf);
					break;
				case HBool b:
					s.WriteByte(b.Value ? (byte)1 : (byte)0);
					break;
				case HUByte ub:
					s.WriteByte(ub.Value);
					break;
				case HFloat64 f:
					BinaryPrimitives.WriteDoubleBigEndian(buf, f.Value); s.Write(buf);
					break;
				case HDateTime dt:
					BinaryPrimitives.WriteUInt32BigEndian(buf, dt.Value); s.Write(buf[..4]);
					break;
				case HBitstruct bs:
					BinaryPrimitives.WriteUInt32BigEndian(buf, bs.Value); s.Write(buf[..4]);
					break;
				case HBinary bin:
					BinaryPrimitives.WriteInt32BigEndian(buf, bin.Value.Length); s.Write(buf[..4]);
					s.Write(bin.Value);
					break;
				case HUtf8 str:
					var bytes = Encoding.UTF8.GetBytes(str.Value);
					BinaryPrimitives.WriteInt32BigEndian(buf, bytes.Length); s.Write(buf[..4]);
					s.Write(bytes);
					break;
				case HArray arr:
					BinaryPrimitives.WriteInt32BigEndian(buf, arr.Items.Count); s.Write(buf[..4]);
					foreach (var item in arr.Items) Write(s, item);
					break;
				case HMap map:
					BinaryPrimitives.WriteInt32BigEndian(buf, map.Entries.Count); s.Write(buf[..4]);
					foreach (var kv in map.Entries) { Write(s, kv.Key); Write(s, kv.Value); }
					break;
				default:
					throw new NotSupportedException(v.GetType().Name);
			}
		}

		// Parses a buffer containing a sequence of hydra values (what agUnpackBuffer does on the client).
		public static List<HValue> Deserialize(ReadOnlySpan<byte> data)
		{
			var list = new List<HValue>();
			int pos = 0;
			while (pos < data.Length) list.Add(Read(data, ref pos));
			return list;
		}

		public static HValue Read(ReadOnlySpan<byte> d, ref int pos)
		{
			var type = (HydraType)d[pos++];
			switch (type)
			{
				case HydraType.None: return HNone.Instance;
				case HydraType.Int32: { var v = BinaryPrimitives.ReadInt32BigEndian(d[pos..]); pos += 4; return new HInt32(v); }
				case HydraType.Int64: { var v = BinaryPrimitives.ReadInt64BigEndian(d[pos..]); pos += 8; return new HInt64(v); }
				case HydraType.UInt64: { var v = BinaryPrimitives.ReadUInt64BigEndian(d[pos..]); pos += 8; return new HUInt64(v); }
				case HydraType.Bool: return new HBool(d[pos++] != 0);
				case HydraType.UByte: return new HUByte(d[pos++]);
				case HydraType.Float64: { var v = BinaryPrimitives.ReadDoubleBigEndian(d[pos..]); pos += 8; return new HFloat64(v); }
				case HydraType.DateTime: { var v = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]); pos += 4; return new HDateTime(v); }
				case HydraType.Bitstruct: { var v = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]); pos += 4; return new HBitstruct(v); }
				case HydraType.Binary:
				{
					int len = BinaryPrimitives.ReadInt32BigEndian(d[pos..]); pos += 4;
					var v = d.Slice(pos, len).ToArray(); pos += len;
					return new HBinary(v);
				}
				case HydraType.Utf8:
				{
					int len = BinaryPrimitives.ReadInt32BigEndian(d[pos..]); pos += 4;
					var v = Encoding.UTF8.GetString(d.Slice(pos, len)); pos += len;
					return new HUtf8(v);
				}
				case HydraType.Array:
				{
					int count = BinaryPrimitives.ReadInt32BigEndian(d[pos..]); pos += 4;
					var arr = new HArray();
					for (int i = 0; i < count; i++) arr.Add(Read(d, ref pos));
					return arr;
				}
				case HydraType.HashMap:
				case HydraType.Struct:
				{
					int count = BinaryPrimitives.ReadInt32BigEndian(d[pos..]); pos += 4;
					var map = new HMap(type);
					for (int i = 0; i < count; i++)
					{
						var k = Read(d, ref pos);
						var v = Read(d, ref pos);
						map.Add(k, v);
					}
					return map;
				}
				default:
					throw new FormatException($"Unknown hydra type {(byte)type} at offset {pos - 1}");
			}
		}
	}
}
