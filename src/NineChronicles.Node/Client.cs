using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.Serialization;
using Bencodex;
using Bencodex.Types;
using Grpc.Net.Client;
using Lib9c.Renderers;
using Libplanet.Crypto;
using Libplanet.Types.Blocks;
using MagicOnion.Client;
using MessagePack;
using Nekoyume.Action;
using Nekoyume.Shared.Hubs;
using Serilog;

namespace NineChronicles.Node;

internal sealed class Client : IAsyncDisposable
{
    private static readonly Codec Codec = new();
    private readonly IActionEvaluationHub _hub;
    private readonly Address _clientAddress;

    private IDisposable? _blockSubscribe;
    private IDisposable? _actionEveryRenderSubscribe;
    private IDisposable? _everyExceptionSubscribe;
    private IDisposable? _nodeStatusSubscribe;

    private Client(
        IActionEvaluationHub hub,
        Address clientAddress)
    {
        _hub = hub;
        _clientAddress = clientAddress;
        TargetAddresses = [];
    }

    public ImmutableHashSet<Address> TargetAddresses { get; set; }

    public static async Task<Client> CreateAsync(GrpcChannel channel, Address clientAddress)
    {
        var hub = await StreamingHubClient.ConnectAsync<IActionEvaluationHub, IActionEvaluationHubReceiver>(
            channel,
            null!);
        await hub.JoinAsync(clientAddress.ToHex());

        return new Client(hub, clientAddress);
    }

    public void Subscribe(
        BlockRenderer blockRenderer,
        ActionRenderer actionRenderer,
        ExceptionRenderer exceptionRenderer,
        NodeStatusRenderer nodeStatusRenderer)
    {
        _blockSubscribe = blockRenderer.BlockSubject
            .SubscribeOn(NewThreadScheduler.Default)
            .ObserveOn(NewThreadScheduler.Default)
            .Subscribe(
                async pair =>
                {
                    try
                    {
                        await _hub.BroadcastRenderBlockAsync(
                            Codec.Encode(pair.OldTip.MarshalBlock()),
                            Codec.Encode(pair.NewTip.MarshalBlock())
                        );
                    }
                    catch (Exception e)
                    {
                        // FIXME add logger as property
                        Log.Error(e, "Skip broadcasting block render due to the unexpected exception");
                    }
                }
            );

        _actionEveryRenderSubscribe = actionRenderer.EveryRender<ActionBase>()
            .SubscribeOn(NewThreadScheduler.Default)
            .ObserveOn(NewThreadScheduler.Default)
            .Subscribe(
                async ev =>
                {
                    try
                    {
                        Stopwatch stopwatch = new Stopwatch();
                        stopwatch.Start();
                        ActionBase? pa = ev.Action is RewardGold
                            ? null
                            : ev.Action;
                        var extra = new Dictionary<string, IValue>();
                        var encodeElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

                        var eval = new NCActionEvaluation(
                            pa,
                            ev.Signer,
                            ev.BlockIndex,
                            ev.OutputState,
                            ev.Exception,
                            ev.PreviousState,
                            ev.RandomSeed,
                            extra,
                            ev.TxId);
                        var encoded = MessagePackSerializer.Serialize(eval);
                        var c = new MemoryStream();
                        await using (var df = new DeflateStream(c, CompressionLevel.Fastest))
                        {
                            df.Write(encoded, 0, encoded.Length);
                        }

                        var compressed = c.ToArray();
                        Log.Verbose(
                            "[{ClientAddress}] #{BlockIndex} Broadcasting render since the given action {Action}. eval size: {Size}",
                            _clientAddress,
                            ev.BlockIndex,
                            ev.Action.GetType(),
                            compressed.LongLength
                        );

                        await _hub.BroadcastRenderAsync(compressed);
                        stopwatch.Stop();

                        var broadcastElapsedMilliseconds = stopwatch.ElapsedMilliseconds - encodeElapsedMilliseconds;
                        Log
                            .ForContext("tag", "Metric")
                            .ForContext("subtag", "ActionEvaluationPublisherElapse")
                            .Verbose(
                                "[{ClientAddress}], #{BlockIndex}, {Action}," +
                                " {EncodeElapsedMilliseconds}, {BroadcastElapsedMilliseconds}, {TotalElapsedMilliseconds}",
                                _clientAddress,
                                ev.BlockIndex,
                                ev.Action.GetType(),
                                encodeElapsedMilliseconds,
                                broadcastElapsedMilliseconds,
                                encodeElapsedMilliseconds + broadcastElapsedMilliseconds);
                    }
                    catch (SerializationException se)
                    {
                        // FIXME add logger as property
                        Log.Error(se, "[{ClientAddress}] Skip broadcasting render since the given action isn't serializable", _clientAddress);
                    }
                    catch (Exception e)
                    {
                        // FIXME add logger as property
                        Log.Error(e, "[{ClientAddress}] Skip broadcasting render due to the unexpected exception", _clientAddress);
                    }
                }
            );

        _everyExceptionSubscribe = exceptionRenderer.EveryException()
            .SubscribeOn(NewThreadScheduler.Default)
            .ObserveOn(NewThreadScheduler.Default)
            .Subscribe(
                async tuple =>
                {
                    try
                    {
                        (RPC.Shared.Exceptions.RPCException code, string message) = tuple;
                        await _hub.ReportExceptionAsync((int)code, message);
                    }
                    catch (Exception e)
                    {
                        // FIXME add logger as property
                        Log.Error(e, "Skip broadcasting exception due to the unexpected exception");
                    }
                }
            );

        _nodeStatusSubscribe = nodeStatusRenderer.EveryChangedStatus()
            .SubscribeOn(NewThreadScheduler.Default)
            .ObserveOn(NewThreadScheduler.Default)
            .Subscribe(
                async preloadStarted =>
                {
                    try
                    {
                        if (preloadStarted)
                        {
                            await _hub.PreloadStartAsync();
                        }
                        else
                        {
                            await _hub.PreloadEndAsync();
                        }
                    }
                    catch (Exception e)
                    {
                        // FIXME add logger as property
                        Log.Error(e, "Skip broadcasting status change due to the unexpected exception");
                    }
                }
            );
    }

    public Task LeaveAsync() => _hub.LeaveAsync();

    public async ValueTask DisposeAsync()
    {
        _blockSubscribe?.Dispose();
        _actionEveryRenderSubscribe?.Dispose();
        _everyExceptionSubscribe?.Dispose();
        _nodeStatusSubscribe?.Dispose();
        await _hub.DisposeAsync();
    }
}
