using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeGraph.Core;

/// <summary>
/// Short, stable handles for node ids. A row ends in the node's id, and a C# id repeats the full name with every
/// parameter type: the id of <c>GraphStore.Replace</c> is over 400 characters that say nothing the row did not.
/// The handle is 8 base-32 characters (40 bits) of the SHA-256 of the id, so it is the same in every answer and
/// every session for as long as the id exists. Two ids whose handles collide keep their long form. Made once per
/// loaded snapshot; without a snapshot, rows print the id.
/// </summary>
public sealed class NodeHandles
{
    public const int Length = 8;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private static readonly ConditionalWeakTable<GraphSnapshot, NodeHandles> PerSnapshot = new();
    private readonly Dictionary<string, string> _idByHandle;
    private readonly Dictionary<string, string> _handleById;

    /// <param name="ids">Every node id of the graph.</param>
    /// <param name="hash">The hash of an id; <see cref="Hash"/> unless a test needs collisions.</param>
    public NodeHandles(IEnumerable<string> ids, Func<string, ulong>? hash = null)
    {
        hash ??= Hash;
        var idByHandle = new Dictionary<string, string>(StringComparer.Ordinal);
        var collided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var handle = Encode(hash(id));
            if (collided.Contains(handle))
            {
                continue;
            }

            if (idByHandle.TryGetValue(handle, out var other))
            {
                if (other == id)
                {
                    continue;
                }

                idByHandle.Remove(handle);
                collided.Add(handle);
                continue;
            }

            idByHandle[handle] = id;
        }

        _idByHandle = idByHandle;
        _handleById = new Dictionary<string, string>(idByHandle.Count, StringComparer.Ordinal);
        foreach (var (handle, id) in idByHandle)
        {
            _handleById[id] = handle;
        }

        Collisions = collided.Count;
    }

    /// <summary>Handles that two or more ids shared; those ids keep their long form.</summary>
    public int Collisions { get; }

    /// <summary>Ids that have a handle.</summary>
    public int Count => _idByHandle.Count;

    /// <summary>The handle to print for <paramref name="id"/>, or the id itself when it has none.</summary>
    public string Handle(string id) => _handleById.TryGetValue(id, out var handle) ? handle : id;

    /// <summary>The id a handle stands for, or null when it is not one of this graph's handles.</summary>
    public string? Resolve(string handle) => _idByHandle.TryGetValue(handle, out var id) ? id : null;

    /// <summary>
    /// Whether <paramref name="text"/> has the shape of a handle. No node id has it: ids carry a kind prefix and a
    /// colon (<c>M:</c>, <c>ts:</c>, <c>scip:</c>).
    /// </summary>
    public static bool LooksLikeHandle(string text) => text.Length == Length && text.All(c => Alphabet.Contains(c));

    /// <summary>The handles of <paramref name="snapshot"/>'s nodes, made on first use and kept with the snapshot.</summary>
    public static NodeHandles For(GraphSnapshot snapshot) =>
        PerSnapshot.GetValue(snapshot, s => new NodeHandles(s.Ids));

    /// <summary>The first 64 bits of the SHA-256 of the id's UTF-8 bytes; <see cref="Encode"/> uses the low 40.</summary>
    public static ulong Hash(string id) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    /// <summary>Eight base-32 characters (a to z, 2 to 7) from the low 40 bits of <paramref name="hash"/>, low bits first.</summary>
    public static string Encode(ulong hash)
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[(int)(hash & 31)];
            hash >>= 5;
        }

        return new string(chars);
    }
}
