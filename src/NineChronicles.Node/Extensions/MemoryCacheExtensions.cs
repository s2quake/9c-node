using System.Diagnostics.CodeAnalysis;
using Bencodex;
using Bencodex.Types;
using MessagePack;
using Microsoft.Extensions.Caching.Memory;

namespace NineChronicles.Node.Extensions;

public static class MemoryCacheExtensions
{
    private static readonly Codec Codec = new();
    private static readonly MessagePackSerializerOptions Lz4Options
        = MessagePackSerializerOptions.Standard.WithCompression(
            MessagePackCompression.Lz4BlockArray);

    public static byte[] SetSheet(
        this MemoryCache @this, string cacheKey, IValue value, TimeSpan ex)
    {
        var compressed = MessagePackSerializer.Serialize(Codec.Encode(value), Lz4Options);
        @this.Set(cacheKey, compressed, ex);
        return compressed;
    }

    public static bool TryGetSheet<T>(
        this MemoryCache @this, string cacheKey, [MaybeNullWhen(false)] out T cached)
    {
        if (@this.TryGetValue(cacheKey, out var c) && c is T t)
        {
            cached = t;
            return true;
        }

        cached = default;
        return false;
    }

    public static string? GetSheet(this MemoryCache @this, string cacheKey)
    {
        if (@this.TryGetSheet(cacheKey, out byte[]? cached))
        {
            var bytes = MessagePackSerializer.Deserialize<byte[]>(cached, Lz4Options);
            return (Text)Codec.Decode(bytes);
        }

        return null;
    }
}
