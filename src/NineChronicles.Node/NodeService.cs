using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Bencodex;
using Bencodex.Types;
using Grpc.Core;
using Libplanet.Action;
using Libplanet.Action.Loader;
using Libplanet.Action.State;
using Libplanet.Common;
using Libplanet.Crypto;
using Libplanet.Node.Services;
using Libplanet.Types.Assets;
using Libplanet.Types.Blocks;
using Libplanet.Types.Tx;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nekoyume;
using Nekoyume.Module;
using NineChronicles.Node.Extensions;
using INodeService = Nekoyume.Shared.Services.IBlockChainService;

namespace NineChronicles.Node;

internal sealed class NodeService(
    IReadChainService readChainService,
    ITransactionService transactionService,
    IActionService actionService,
    IPolicyService policyService,
    RpcContext context,
    ActionEvaluationPublisher publisher,
    StateMemoryCache cache,
    ILogger<NodeService> logger) : ServiceBase<INodeService>, INodeService
{
    private static readonly Codec _codec = new();
    private static byte[] _nullBytes = _codec.Encode(Null.Value);
    private readonly MemoryCache _memoryCache = cache.SheetCache;

    public UnaryResult<bool> AddClient(byte[] addressByte)
    {
        var address = new Address(addressByte);
        publisher.AddClient(address).Wait();
        return new UnaryResult<bool>(true);
    }

    public async UnaryResult<Dictionary<byte[], byte[]>> GetAgentStatesByBlockHash(
        byte[] blockHashBytes, IEnumerable<byte[]> addressBytesList)
    {
        var hash = new BlockHash(blockHashBytes);
        var worldState = actionService.GetWorldState(hash);
        var result = new ConcurrentDictionary<byte[], byte[]>();
        var taskList = addressBytesList.Select(addressByte => Task.Run(() =>
        {
            var value = worldState.GetResolvedState(new Address(addressByte), Addresses.Agent);
            result.TryAdd(addressByte, _codec.Encode(value ?? Null.Value));
        }));

        await Task.WhenAll(taskList);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async UnaryResult<Dictionary<byte[], byte[]>> GetAgentStatesByStateRootHash(
        byte[] stateRootHashBytes, IEnumerable<byte[]> addressBytesList)
    {
        var stateRootHash = new HashDigest<SHA256>(stateRootHashBytes);
        var worldState = actionService.GetWorldState(stateRootHash);
        var result = new ConcurrentDictionary<byte[], byte[]>();
        var taskList = addressBytesList.Select(addressByte => Task.Run(() =>
        {
            var value = worldState.GetResolvedState(new Address(addressByte), Addresses.Agent);
            result.TryAdd(addressByte, _codec.Encode(value ?? Null.Value));
        }));

        await Task.WhenAll(taskList);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async UnaryResult<Dictionary<byte[], byte[]>> GetAvatarStatesByBlockHash(
        byte[] blockHashBytes, IEnumerable<byte[]> addressBytesList)
    {
        var hash = new BlockHash(blockHashBytes);
        var worldState = actionService.GetWorldState(hash);
        var result = new ConcurrentDictionary<byte[], byte[]>();
        var addresses = addressBytesList.Select(a => new Address(a)).ToList();
        var taskList = addresses.Select(address => Task.Run(() =>
        {
            var value = GetFullAvatarStateRaw(logger, worldState, address);
            result.TryAdd(address.ToByteArray(), _codec.Encode(value ?? Null.Value));
        }));

        await Task.WhenAll(taskList);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async UnaryResult<Dictionary<byte[], byte[]>> GetAvatarStatesByStateRootHash(
        byte[] stateRootHashBytes, IEnumerable<byte[]> addressBytesList)
    {
        var addresses = addressBytesList.Select(a => new Address(a)).ToList();
        var stateRootHash = new HashDigest<SHA256>(stateRootHashBytes);
        var worldState = actionService.GetWorldState(stateRootHash);
        var result = new ConcurrentDictionary<byte[], byte[]>();
        var taskList = addresses.Select(address => Task.Run(() =>
        {
            var value = GetFullAvatarStateRaw(logger, worldState, address);
            result.TryAdd(address.ToByteArray(), _codec.Encode(value ?? Null.Value));
        }));

        await Task.WhenAll(taskList);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public UnaryResult<byte[]> GetBalanceByBlockHash(
        byte[] blockHashBytes, byte[] addressBytes, byte[] currencyBytes)
    {
        var blockHash = new BlockHash(blockHashBytes);
        var address = new Address(addressBytes);
        var serializedCurrency = (Dictionary)_codec.Decode(currencyBytes);
        var currency = CurrencyExtensions.Deserialize(serializedCurrency);
        var balance = actionService
            .GetWorldState(blockHash)
            .GetBalance(address, currency);
        var values = new IValue[]
        {
            balance.Currency.Serialize(),
            (Integer)balance.RawValue,
        };
        var encoded = _codec.Encode(new List(values));
        return new UnaryResult<byte[]>(encoded);
    }

    public UnaryResult<byte[]> GetBalanceByStateRootHash(
        byte[] stateRootHashBytes, byte[] addressBytes, byte[] currencyBytes)
    {
        var stateRootHash = new HashDigest<SHA256>(stateRootHashBytes);
        var address = new Address(addressBytes);
        var serializedCurrency = (Dictionary)_codec.Decode(currencyBytes);
        var currency = CurrencyExtensions.Deserialize(serializedCurrency);
        var balance = actionService
            .GetWorldState(stateRootHash)
            .GetBalance(address, currency);
        var values = new IValue[]
        {
            balance.Currency.Serialize(),
            (Integer)balance.RawValue,
        };
        var encoded = _codec.Encode(new List(values));
        return new UnaryResult<byte[]>(encoded);
    }

    public UnaryResult<byte[]> GetBlockHash(long blockIndex)
    {
        try
        {
            var block = readChainService.GetBlock(height: blockIndex);
            var bytes = _codec.Encode(block.Hash.Bencoded);
            return new UnaryResult<byte[]>(bytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new UnaryResult<byte[]>(_nullBytes);
        }
    }

    public UnaryResult<Dictionary<byte[], byte[]>> GetBulkStateByBlockHash(
        byte[] blockHashBytes, byte[] accountAddressBytes, IEnumerable<byte[]> addressBytesList)
    {
        var blockHash = new BlockHash(blockHashBytes);
        var accountAddress = new Address(accountAddressBytes);
        var addresses = addressBytesList.Select(b => new Address(b)).ToList();

        var result = new Dictionary<byte[], byte[]>();
        var values = actionService
            .GetWorldState(blockHash)
            .GetAccountState(accountAddress)
            .GetStates(addresses);
        for (var i = 0; i < addresses.Count; i++)
        {
            result.TryAdd(addresses[i].ToByteArray(), _codec.Encode(values[i] ?? Null.Value));
        }

        return new UnaryResult<Dictionary<byte[], byte[]>>(result);
    }

    public UnaryResult<Dictionary<byte[], byte[]>> GetBulkStateByStateRootHash(
        byte[] stateRootHashBytes, byte[] accountAddressBytes, IEnumerable<byte[]> addressBytesList)
    {
        var stateRootHash = new HashDigest<SHA256>(stateRootHashBytes);
        var accountAddress = new Address(accountAddressBytes);
        var addresses = addressBytesList.Select(b => new Address(b)).ToList();

        var result = new Dictionary<byte[], byte[]>();
        var values = actionService
            .GetWorldState(stateRootHash)
            .GetAccountState(accountAddress)
            .GetStates(addresses);
        for (var i = 0; i < addresses.Count; i++)
        {
            result.TryAdd(addresses[i].ToByteArray(), _codec.Encode(values[i] ?? Null.Value));
        }

        return new UnaryResult<Dictionary<byte[], byte[]>>(result);
    }

    public UnaryResult<long> GetNextTxNonce(byte[] addressBytes)
    {
        var address = new Address(addressBytes);
        var nonce = transactionService.GetNextTxNonce(address);
        return new UnaryResult<long>(nonce);
    }

    public UnaryResult<Dictionary<byte[], byte[]>> GetSheets(
        byte[] blockHashBytes, IEnumerable<byte[]> addressBytesList)
    {
        var started = DateTime.UtcNow;
        var sw = new Stopwatch();
        sw.Start();
        var result = new Dictionary<byte[], byte[]>();
        List<Address> addresses = new List<Address>();
        foreach (var b in addressBytesList)
        {
            var address = new Address(b);
            if (_memoryCache.TryGetSheet<byte[]>(address.ToString(), out var cached))
            {
                result.TryAdd(b, cached);
            }
            else
            {
                addresses.Add(address);
            }
        }

        sw.Stop();
        logger.LogInformation(
            "[GetSheets]Get sheet from cache count: {CachedCount}, not Cached: " +
            "{CacheMissedCount}, Elapsed: {Elapsed}",
            result.Count,
            addresses.Count,
            sw.Elapsed);
        sw.Restart();
        if (addresses.Any())
        {
            var stateRootHash = new BlockHash(blockHashBytes);
            var values = actionService.GetWorldState(stateRootHash).GetLegacyStates(addresses);
            sw.Stop();
            logger.LogInformation(
                "[GetSheets]Get sheet from state: {Count}, Elapsed: {Elapsed}",
                addresses.Count,
                sw.Elapsed);
            sw.Restart();
            for (int i = 0; i < addresses.Count; i++)
            {
                var address = addresses[i];
                var value = values[i] ?? Null.Value;
                var ex = TimeSpan.FromMinutes(1);
                var compressed = _memoryCache.SetSheet(address.ToString(), value, ex);
                result.TryAdd(address.ToByteArray(), compressed);
            }
        }

        logger.LogInformation("[GetSheets]Total: {Elapsed}", DateTime.UtcNow - started);
        return new UnaryResult<Dictionary<byte[], byte[]>>(result);
    }

    public UnaryResult<byte[]> GetStateByBlockHash(
        byte[] blockHashBytes, byte[] accountAddressBytes, byte[] addressBytes)
    {
        var hash = new BlockHash(blockHashBytes);
        var accountAddress = new Address(accountAddressBytes);
        var address = new Address(addressBytes);
        var state = actionService
            .GetWorldState(hash)
            .GetAccountState(accountAddress)
            .GetState(address);
        // FIXME: Null과 null 구분해서 반환해야 할 듯
        var encoded = _codec.Encode(state ?? Null.Value);
        return new UnaryResult<byte[]>(encoded);
    }

    public UnaryResult<byte[]> GetStateByStateRootHash(
        byte[] stateRootHashBytes, byte[] accountAddressBytes, byte[] addressBytes)
    {
        var stateRootHash = new HashDigest<SHA256>(stateRootHashBytes);
        var accountAddress = new Address(accountAddressBytes);
        var address = new Address(addressBytes);
        var state = actionService
            .GetWorldState(stateRootHash)
            .GetAccountState(accountAddress)
            .GetState(address);
        var encoded = _codec.Encode(state ?? Null.Value);
        return new UnaryResult<byte[]>(encoded);
    }

    public UnaryResult<byte[]> GetTip()
    {
        var headerDictionary = readChainService.Tip.MarshalBlock();
        var headerBytes = _codec.Encode(headerDictionary);
        return new UnaryResult<byte[]>(headerBytes);
    }

    public UnaryResult<bool> IsTransactionStaged(byte[] txidBytes)
    {
        var id = new TxId(txidBytes);
        var isStaged = transactionService.IsTransactionStaged(id);
        return new UnaryResult<bool>(isStaged);
    }

    public UnaryResult<bool> PutTransaction(byte[] txBytes)
    {
        try
        {
            var tx = Transaction.Deserialize(txBytes);

            var actionName = actionService.LoadAction(tx.Actions[0]) is { } action
                ? $"{action}"
                : "NoAction";
            var txId = tx.Id.ToString();

            try
            {
                transactionService.StageTransaction(tx);

                return new UnaryResult<bool>(true);
            }
            catch (InvalidTxException ite)
            {
                logger.LogError(ite, $"{nameof(InvalidTxException)} occurred during {nameof(PutTransaction)}(). {{e}}", ite);
                return new UnaryResult<bool>(false);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, $"Unexpected exception occurred during {nameof(PutTransaction)}(). {{e}}", e);
            throw;
        }
    }

    public UnaryResult<bool> RemoveClient(byte[] addressByte)
    {
        var address = new Address(addressByte);
        publisher.RemoveClient(address).Wait();
        return new UnaryResult<bool>(true);
    }

    public UnaryResult<bool> ReportException(string code, string message)
    {
        logger.LogDebug(
            "Reported exception from Unity player. (code: {Code}, message: {Message})",
            code,
            message);

        switch (code)
        {
            case "26":
            case "27":
                NodeExceptionType exceptionType = NodeExceptionType.ActionTimeout;
                // _libplanetNodeServiceProperties.NodeExceptionOccurred(exceptionType, message);
                break;
        }

        return new UnaryResult<bool>(true);
    }

    public UnaryResult<bool> SetAddressesToSubscribe(
        byte[] toByteArray, IEnumerable<byte[]> addressesBytes)
    {
        if (context.RpcRemoteSever)
        {
            publisher.UpdateSubscribeAddresses(toByteArray, addressesBytes);
        }
        else
        {
            context.AddressesToSubscribe =
                addressesBytes.Select(ba => new Address(ba)).ToImmutableHashSet();
            logger.LogDebug(
                "Subscribed addresses: {addresses}",
                string.Join(", ", context.AddressesToSubscribe));
        }

        return new UnaryResult<bool>(true);
    }

    // Returning value is a list of [ Avatar, Inventory, QuestList, WorldInformation ]
    private static IValue? GetFullAvatarStateRaw(
        ILogger logger, IWorldState worldState, Address address)
    {
        var serializedAvatarRaw = worldState.GetAccountState(Addresses.Avatar).GetState(address);
        if (serializedAvatarRaw is not List)
        {
            logger.LogWarning(
                "Avatar state ({AvatarAddress}) should be List but: {Raw}",
                address.ToHex(),
                serializedAvatarRaw);
            return null;
        }

        var serializedInventoryRaw =
            worldState.GetAccountState(Addresses.Inventory).GetState(address);
        var serializedQuestListRaw =
            worldState.GetAccountState(Addresses.QuestList).GetState(address);
        var serializedWorldInformationRaw =
            worldState.GetAccountState(Addresses.WorldInformation).GetState(address);

        return new List(
            serializedAvatarRaw,
            serializedInventoryRaw!,
            serializedQuestListRaw!,
            serializedWorldInformationRaw!);
    }
}
