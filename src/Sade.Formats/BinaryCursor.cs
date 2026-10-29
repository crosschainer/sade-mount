using System.Buffers.Binary;
using System.Text;

namespace Sade.Formats;

internal ref struct BinaryCursor(ReadOnlySpan<byte> data)
{
    public readonly ReadOnlySpan<byte> Data = data;
    public int Position;
    public readonly int Remaining => Data.Length - Position;
    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException($"Truncated data at byte {Position}: need {count}, have {Remaining}.");
        var result = Data.Slice(Position, count);
        Position += count;
        return result;
    }
    public void Skip(int count) => Take(count);
    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float RawSingle() => BitConverter.Int32BitsToSingle(Int32());
    public float Single()
    {
        var value = RawSingle();
        if (!float.IsFinite(value)) throw new InvalidDataException($"Nonfinite float at byte {Position - 4}.");
        return value;
    }
    public string Text(int count)
    {
        var bytes = Take(count);
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }
    public int Count(int maximum, string what)
    {
        var value = Int32();
        if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid {what} count {value}.");
        return value;
    }
    public BinaryCursor Chunk(string expected)
    {
        var tag = Text(4);
        var size = UInt32();
        if (tag != expected) throw new InvalidDataException($"Expected {expected}, found {tag} at byte {Position - 8}.");
        if (size > int.MaxValue) throw new InvalidDataException("Chunk is too large.");
        var result = new BinaryCursor(Take((int)size));
        var padding = (4 - ((int)size & 3)) & 3;
        Skip(padding);
        return result;
    }
    public readonly void RequireZeroTail()
    {
        foreach (var value in Data[Position..])
            if (value != 0) throw new InvalidDataException($"Unexpected nonzero trailing data at byte {Position}.");
    }
}
