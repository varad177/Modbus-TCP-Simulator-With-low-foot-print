using System.Runtime.InteropServices;
using ModbusTcpSimulator.Core.Models;

namespace ModbusTcpSimulator.Core.Conversion;

/// <summary>
/// Converts logical double values ↔ raw Modbus 16-bit word arrays.
/// Handles all supported data types and byte/word orders.
/// </summary>
public static class DataTypeConverter
{
    /// <summary>Returns how many 16-bit registers are needed for a given data type.</summary>
    public static int RegisterCount(DataType dt) => dt switch
    {
        DataType.Bool => 1,
        DataType.UInt16 => 1,
        DataType.Int16 => 1,
        DataType.UInt32 => 2,
        DataType.Int32 => 2,
        DataType.Float32 => 2,
        DataType.UInt64 => 4,
        DataType.Int64 => 4,
        DataType.Float64 => 4,
        _ => 1
    };

    /// <summary>Encode a logical value into raw Modbus words.</summary>
    public static ushort[] Encode(double value, DataType dt, ByteOrder order)
    {
        byte[] bytes = dt switch
        {
            DataType.Bool => BitConverter.GetBytes(value >= 0.5 ? (ushort)1 : (ushort)0),
            DataType.UInt16 => BitConverter.GetBytes((ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue)),
            DataType.Int16 => BitConverter.GetBytes((short)Math.Clamp(value, short.MinValue, short.MaxValue)),
            DataType.UInt32 => BitConverter.GetBytes((uint)Math.Clamp(value, uint.MinValue, uint.MaxValue)),
            DataType.Int32 => BitConverter.GetBytes((int)Math.Clamp(value, int.MinValue, int.MaxValue)),
            DataType.Float32 => BitConverter.GetBytes((float)value),
            DataType.UInt64 => BitConverter.GetBytes((ulong)Math.Clamp(value, ulong.MinValue, (double)ulong.MaxValue)),
            DataType.Int64 => BitConverter.GetBytes((long)Math.Clamp(value, long.MinValue, (double)long.MaxValue)),
            DataType.Float64 => BitConverter.GetBytes(value),
            _ => BitConverter.GetBytes((ushort)0)
        };

        // Convert bytes to ushort[] (always in pairs)
        int wordCount = (bytes.Length + 1) / 2;
        if (bytes.Length < wordCount * 2)
            Array.Resize(ref bytes, wordCount * 2);

        var words = new ushort[wordCount];
        for (int i = 0; i < wordCount; i++)
            words[i] = BitConverter.ToUInt16(bytes, i * 2);

        return ApplyWordOrder(words, order);
    }

    /// <summary>Decode raw Modbus words into a logical double value.</summary>
    public static double Decode(ushort[] words, DataType dt, ByteOrder order)
    {
        var ordered = ApplyWordOrder(words, order); // reverse the order to get original bytes
        var bytes = new byte[ordered.Length * 2];
        for (int i = 0; i < ordered.Length; i++)
        {
            var w = BitConverter.GetBytes(ordered[i]);
            bytes[i * 2] = w[0];
            bytes[i * 2 + 1] = w[1];
        }

        return dt switch
        {
            DataType.Bool => bytes.Length >= 2 ? BitConverter.ToUInt16(bytes, 0) != 0 ? 1.0 : 0.0 : 0,
            DataType.UInt16 => bytes.Length >= 2 ? BitConverter.ToUInt16(bytes, 0) : 0,
            DataType.Int16 => bytes.Length >= 2 ? BitConverter.ToInt16(bytes, 0) : 0,
            DataType.UInt32 => bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0,
            DataType.Int32 => bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0,
            DataType.Float32 => bytes.Length >= 4 ? BitConverter.ToSingle(bytes, 0) : 0,
            DataType.UInt64 => bytes.Length >= 8 ? BitConverter.ToUInt64(bytes, 0) : 0,
            DataType.Int64 => bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0,
            DataType.Float64 => bytes.Length >= 8 ? BitConverter.ToDouble(bytes, 0) : 0,
            _ => 0
        };
    }

    private static ushort[] ApplyWordOrder(ushort[] words, ByteOrder order)
    {
        if (words.Length <= 1 || order == ByteOrder.BigEndian)
            return words;

        var result = (ushort[])words.Clone();

        if (order == ByteOrder.LittleEndian)
        {
            Array.Reverse(result);
            // Also swap bytes within each word
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)((result[i] >> 8) | (result[i] << 8));
        }
        else if (order == ByteOrder.WordSwap)
        {
            Array.Reverse(result);
        }

        return result;
    }
}
