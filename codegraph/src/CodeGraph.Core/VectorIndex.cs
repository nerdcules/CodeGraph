using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeGraph.Core;

/// <summary>
/// Embedding vectors as the graph stores them: each component scaled so the vector's largest one maps to ±127, one
/// signed byte per component, plus the factor that turns the integer dot product back into a cosine. Scaling per
/// vector uses the whole byte range; scaling a unit vector by a fixed 127 would leave a 768-dimension embedding
/// (components around ±0.05) about a dozen levels and visibly reorder close results.
/// </summary>
public readonly record struct QuantizedVector(sbyte[] Values, float Scale)
{
    /// <summary>Four bytes of scale, then one byte per component.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[4 + Values.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), Scale);
        MemoryMarshal.AsBytes(Values.AsSpan()).CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    public static QuantizedVector FromBytes(byte[] bytes) =>
        new(MemoryMarshal.Cast<byte, sbyte>(bytes.AsSpan(4)).ToArray(), BitConverter.ToSingle(bytes, 0));
}

public static class VectorQuantizer
{
    public const float Levels = 127f;

    /// <summary>
    /// The vector as bytes and the factor <c>max|x| / (127 · |x|)</c>: the cosine of two vectors is then
    /// <c>dot(bytes) · scaleA · scaleB</c>.
    /// </summary>
    public static QuantizedVector Quantize(ReadOnlySpan<float> vector)
    {
        var norm = 0f;
        var max = 0f;
        foreach (var x in vector)
        {
            norm += x * x;
            max = MathF.Max(max, MathF.Abs(x));
        }

        norm = MathF.Sqrt(norm);
        var values = new sbyte[vector.Length];
        if (norm == 0 || max == 0)
        {
            return new QuantizedVector(values, 0);
        }

        for (var i = 0; i < vector.Length; i++)
        {
            values[i] = (sbyte)Math.Clamp(MathF.Round(vector[i] / max * Levels), -127, 127);
        }

        return new QuantizedVector(values, max / (Levels * norm));
    }

    /// <summary>Integer dot product of two quantised vectors, eight to thirty-two lanes at a time.</summary>
    public static int Dot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        var acc = Vector<int>.Zero;
        var width = Vector<sbyte>.Count;
        var i = 0;
        for (; i <= a.Length - width; i += width)
        {
            Vector.Widen(new Vector<sbyte>(a[i..]), out Vector<short> a0, out Vector<short> a1);
            Vector.Widen(new Vector<sbyte>(b[i..]), out Vector<short> b0, out Vector<short> b1);
            // |x| ≤ 127, so each product fits a short; widen before summing.
            Vector.Widen(a0 * b0, out Vector<int> p0, out Vector<int> p1);
            Vector.Widen(a1 * b1, out Vector<int> p2, out Vector<int> p3);
            acc += p0 + p1 + p2 + p3;
        }

        var sum = Vector.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

}

/// <summary>
/// Every vector of one graph in one contiguous array, scored by brute force. At tens of thousands of declarations
/// that is a few milliseconds, with no index to build or keep in step. Loaded once per graph file and reused until
/// the file changes (see <see cref="For"/>).
/// </summary>
public sealed class VectorIndex
{
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, (DateTime Write, long Length, VectorIndex Index)> Cache = new(StringComparer.Ordinal);

    private readonly string[] _ids;
    private readonly sbyte[] _data;
    private readonly float[] _scales;

    public VectorIndex(IReadOnlyList<(string Id, QuantizedVector Vector)> vectors)
    {
        Dimensions = vectors.Count == 0 ? 0 : vectors[0].Vector.Values.Length;
        var rows = vectors.Where(v => v.Vector.Values.Length == Dimensions).ToList();
        _ids = rows.Select(v => v.Id).ToArray();
        _scales = rows.Select(v => v.Vector.Scale).ToArray();
        _data = new sbyte[rows.Count * Dimensions];
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Vector.Values.CopyTo(_data.AsSpan(i * Dimensions));
        }
    }

    public int Count => _ids.Length;

    public int Dimensions { get; }

    /// <summary>The index for the graph at <paramref name="dbPath"/>, loaded through <paramref name="load"/> only when the file changed.</summary>
    public static VectorIndex For(string dbPath, Func<VectorIndex> load)
    {
        var info = new FileInfo(dbPath);
        var stamp = (info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : 0);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(dbPath, out var cached) && (cached.Write, cached.Length) == stamp)
            {
                return cached.Index;
            }
        }

        var index = load();
        lock (CacheLock)
        {
            Cache[dbPath] = (stamp.Item1, stamp.Item2, index);
        }

        return index;
    }

    /// <summary>The <paramref name="count"/> closest ids with their cosine estimate, best first.</summary>
    public IReadOnlyList<(string Id, double Similarity)> Top(QuantizedVector query, int count)
    {
        if (Count == 0 || query.Values.Length != Dimensions)
        {
            return [];
        }

        var best = new PriorityQueue<int, float>(count + 1);
        for (var row = 0; row < _ids.Length; row++)
        {
            var score = VectorQuantizer.Dot(_data.AsSpan(row * Dimensions, Dimensions), query.Values) * _scales[row];
            if (best.Count < count)
            {
                best.Enqueue(row, score);
            }
            else if (best.TryPeek(out _, out var worst) && score > worst)
            {
                best.DequeueEnqueue(row, score);
            }
        }

        var result = new List<(string, double)>(best.Count);
        while (best.TryDequeue(out var row, out var score))
        {
            result.Add((_ids[row], score * (double)query.Scale));
        }

        result.Reverse();
        return result;
    }
}
