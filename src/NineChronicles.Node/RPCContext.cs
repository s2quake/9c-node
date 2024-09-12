using System.Collections.Immutable;
using Libplanet.Crypto;

namespace NineChronicles.Node;

public sealed class RpcContext
{
    public ImmutableHashSet<Address> AddressesToSubscribe { get; set; } = ImmutableHashSet<Address>.Empty;

    public bool RpcRemoteSever { get; set; }
}
