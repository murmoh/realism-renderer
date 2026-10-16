using System.Buffers.Binary;
using System.Text;

namespace Realism.Minecraft;

/// <summary>A compound tag. Values are boxed primitives, arrays, strings, lists or nested compounds.</summary>
public sealed class NbtCompound : Dictionary<string, object>
{
    public NbtCompound? GetCompound(string key) => TryGetValue(key, out var v) ? v as NbtCompound : null;
    public List<object>? GetList(string key) => TryGetValue(key, out var v) ? v as List<object> : null;
    public long[]? GetLongArray(string key) => TryGetValue(key, out var v) ? v as long[] : null;
    public int[]? GetIntArray(string key) => TryGetValue(key, out var v) ? v as int[] : null;
    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    public long? GetNumber(string key) => TryGetValue(key, out var v) ? ToLong(v) : null;

    public static long? ToLong(object v) => v switch
    {
        sbyte b => b, short s => s, int i => i, long l => l, float f => (long)f, double d => (long)d, _ => null,
    };

    public static double? ToDouble(object v) => v switch
    {
        float f => f, double d => d, sbyte b => b, short s => s, int i => i, long l => l, _ => null,
    };
}

/// <summary>Minimal reader for Minecraft's big-endian Named Binary Tag format.</summary>
public static class Nbt
{
    public static NbtCompound Parse(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        byte type = data[pos++];
        if (type != 10) throw new InvalidDataException("NBT root is not a compound");
        ReadString(data, ref pos);
        return (NbtCompound)ReadPayload(data, ref pos, type);
    }

    static object ReadPayload(ReadOnlySpan<byte> d, ref int p, byte type)
    {
        switch (type)
        {
            case 1: return (sbyte)d[p++];
            case 2: { var v = BinaryPrimitives.ReadInt16BigEndian(d[p..]); p += 2; return v; }
            case 3: { var v = BinaryPrimitives.ReadInt32BigEndian(d[p..]); p += 4; return v; }
            case 4: { var v = BinaryPrimitives.ReadInt64BigEndian(d[p..]); p += 8; return v; }
            case 5: { var v = BinaryPrimitives.ReadSingleBigEndian(d[p..]); p += 4; return v; }
            case 6: { var v = BinaryPrimitives.ReadDoubleBigEndian(d[p..]); p += 8; return v; }
            case 7:
            {
                int len = ReadInt(d, ref p);
                var v = d.Slice(p, len).ToArray();
                p += len;
                return v;
            }
            case 8: return ReadString(d, ref p);
            case 9:
            {
                byte elem = d[p++];
                int len = ReadInt(d, ref p);
                var list = new List<object>(Math.Max(len, 0));
                for (int i = 0; i < len; i++) list.Add(ReadPayload(d, ref p, elem));
                return list;
            }
            case 10:
            {
                var c = new NbtCompound();
                while (true)
                {
                    byte t = d[p++];
                    if (t == 0) break;
                    string name = ReadString(d, ref p);
                    c[name] = ReadPayload(d, ref p, t);
                }
                return c;
            }
            case 11:
            {
                int len = ReadInt(d, ref p);
                var v = new int[len];
                for (int i = 0; i < len; i++) v[i] = ReadInt(d, ref p);
                return v;
            }
            case 12:
            {
                int len = ReadInt(d, ref p);
                var v = new long[len];
                for (int i = 0; i < len; i++, p += 8) v[i] = BinaryPrimitives.ReadInt64BigEndian(d[p..]);
                return v;
            }
            default: throw new InvalidDataException($"Unknown NBT tag type {type}");
        }
    }

    static int ReadInt(ReadOnlySpan<byte> d, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(d[p..]);
        p += 4;
        return v;
    }

    static string ReadString(ReadOnlySpan<byte> d, ref int p)
    {
        int len = BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
        p += 2;
        var s = Encoding.UTF8.GetString(d.Slice(p, len));
        p += len;
        return s;
    }
}
