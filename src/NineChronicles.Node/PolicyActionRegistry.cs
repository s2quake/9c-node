using System.Collections.Immutable;
using Libplanet.Action;
using Nekoyume.Action;

namespace NineChronicles.Node;

public class PolicyActionRegistry : IPolicyActionsRegistry
{
    private readonly IPolicyActionsRegistry _policyActionsRegistry;

    public PolicyActionRegistry()
    {
        _policyActionsRegistry = new PolicyActionsRegistry(
            beginBlockActions: ImmutableArray<IAction>.Empty,
            endBlockActions: new IAction[] { new RewardGold() }.ToImmutableArray(),
            beginTxActions: ImmutableArray<IAction>.Empty,
            endTxActions: ImmutableArray<IAction>.Empty);
    }

    public ImmutableArray<IAction> BeginBlockActions =>
        _policyActionsRegistry.BeginBlockActions;

    public ImmutableArray<IAction> EndBlockActions =>
        _policyActionsRegistry.EndBlockActions;

    public ImmutableArray<IAction> BeginTxActions =>
        _policyActionsRegistry.BeginTxActions;

    public ImmutableArray<IAction> EndTxActions =>
        _policyActionsRegistry.EndTxActions;
}
