using System.Collections.Concurrent;
using Lib9c.Formatters;
using Lib9c.Renderers;
using Libplanet.Crypto;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NineChronicles.Node.Extensions;

public static class NineChroniclesNodeExtensions
{
    public static void AddNineChroniclesNode(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<BlockRenderer>();
        services.AddSingleton<ActionRenderer>();
        services.AddSingleton<ExceptionRenderer>();
        services.AddSingleton<NodeStatusRenderer>();
        services.AddSingleton<StateMemoryCache>();
        services.AddSingleton<ActionEvaluationHub>();
        services.AddSingleton<RpcContext>();

        services.AddMagicOnion();

        var resolver = MessagePack.Resolvers.CompositeResolver.Create(
            NineChroniclesResolver.Instance,
            StandardResolver.Instance
        );

        MessagePackSerializer.DefaultOptions
            = MessagePackSerializerOptions.Standard.WithResolver(resolver);
    }
}
