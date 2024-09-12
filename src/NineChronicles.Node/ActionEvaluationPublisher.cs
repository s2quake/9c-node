using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Reactive.Linq;
using Bencodex.Types;
using Grpc.Core;
using Grpc.Net.Client;
using Lib9c.Renderers;
using Libplanet.Common;
using Libplanet.Crypto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Nekoyume;
using Nekoyume.Action;
using NineChronicles.Node.Extensions;
using Serilog;

namespace NineChronicles.Node;

public class ActionEvaluationPublisher : BackgroundService
{
    private readonly string _host;
    private readonly int _port;
    private readonly BlockRenderer _blockRenderer;
    private readonly ActionRenderer _actionRenderer;
    private readonly ExceptionRenderer _exceptionRenderer;
    private readonly NodeStatusRenderer _nodeStatusRenderer;

    private readonly ConcurrentDictionary<Address, Client> _clients = new();
    private readonly ConcurrentDictionary<Address, string> _clientsByDevice = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _clientsByIp = new();
    private readonly IMemoryCache _cache;
    private MemoryCache _memoryCache;

    public ActionEvaluationPublisher(
        BlockRenderer blockRenderer,
        ActionRenderer actionRenderer,
        ExceptionRenderer exceptionRenderer,
        NodeStatusRenderer nodeStatusRenderer,
        string host,
        int port,
        StateMemoryCache cache)
    {
        _blockRenderer = blockRenderer;
        _actionRenderer = actionRenderer;
        _exceptionRenderer = exceptionRenderer;
        _nodeStatusRenderer = nodeStatusRenderer;
        _host = host;
        _port = port;
        var memoryCacheOptions = new MemoryCacheOptions();
        var options = Options.Create(memoryCacheOptions);
        _cache = new MemoryCache(options);
        _memoryCache = cache.SheetCache;

        var meter = new Meter("NineChronicles");
        meter.CreateObservableGauge(
            "ninechronicles_rpc_clients_count",
            () => GetClients().Count,
            description: "Number of RPC clients connected.");
        meter.CreateObservableGauge(
            "ninechronicles_rpc_clients_count_by_device",
            () => new[]
            {
                new Measurement<int>(GetClientsCountByDevice("mobile"), new[] { new KeyValuePair<string, object?>("device", "mobile") }),
                new Measurement<int>(GetClientsCountByDevice("pc"), new[] { new KeyValuePair<string, object?>("device", "pc") }),
                new Measurement<int>(GetClientsCountByDevice("other"), new[] { new KeyValuePair<string, object?>("device", "other") }),
            },
            description: "Number of RPC clients connected by device.");
        meter.CreateObservableGauge(
            "ninechronicles_clients_count_by_ips",
            () => new[]
            {
                new Measurement<int>(
                    GetClientsCountByIp(10),
                    new KeyValuePair<string, object?>("account-type", "multi")),
                new Measurement<int>(
                    GetClientsCountByIp(0),
                    new KeyValuePair<string, object?>("account-type", "all")),
            },
            description: "Number of RPC clients connected grouped by ips.");

        ActionEvaluationHub.OnClientDisconnected += RemoveClient;
        _actionRenderer.EveryRender<PatchTableSheet>().Subscribe(ev =>
        {
            if (ev.Exception is null)
            {
                var action = ev.Action;
                var sheetAddress = Addresses.GetSheetAddress(action.TableName);
                _memoryCache.SetSheet(sheetAddress.ToString(), (Text)action.TableCsv, TimeSpan.FromMinutes(1));
            }
        });
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public async Task AddClient(Address clientAddress)
    {
        var options = new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure,
            MaxReceiveMessageSize = null,
        };

        GrpcChannel channel = GrpcChannel.ForAddress($"http://{_host}:{_port}", options);
        Client client = await Client.CreateAsync(channel, clientAddress);
        if (_clients.TryAdd(clientAddress, client))
        {
            if (clientAddress == default)
            {
                Log.Warning("[{ClientAddress}] AddClient set default address", clientAddress);
            }

            Log.Information("[{ClientAddress}] AddClient", clientAddress);
            client.Subscribe(
                _blockRenderer,
                _actionRenderer,
                _exceptionRenderer,
                _nodeStatusRenderer
            );
        }
        else
        {
            await client.DisposeAsync();
        }
    }

    public void AddClientByDevice(Address clientAddress, string device)
    {
        if (!_clientsByDevice.ContainsKey(clientAddress))
        {
            _clientsByDevice.TryAdd(clientAddress, device);
        }
    }

    private void RemoveClientByDevice(Address clientAddress)
    {
        if (_clientsByDevice.ContainsKey(clientAddress))
        {
            _clientsByDevice.TryRemove(clientAddress, out _);
        }
    }

    public int GetClientsCountByDevice(string device)
    {
        return _clientsByDevice.Values.Count(x => x == device);
    }

    public List<Address> GetClientsByDevice(string device)
    {
        return _clientsByDevice
            .Where(x => x.Value == device)
            .Select(x => x.Key)
            .ToList();
    }

    public void AddClientAndIp(string ipAddress, string clientAddress)
    {
        if (!_clientsByIp.ContainsKey(ipAddress))
        {
            _clientsByIp[ipAddress] = new HashSet<string>();
        }

        _clientsByIp[ipAddress].Add(clientAddress);
    }

    public int GetClientsCountByIp(int minimum)
    {
        var finder = new IdGroupFinder(_cache);
        var groups = finder.FindGroups(_clientsByIp);
        return groups.Where(group => group.IDs.Count >= minimum)
            .Sum(group => group.IDs.Count);
    }

    public ConcurrentDictionary<List<string>, List<string>> GetClientsByIp(int minimum)
    {
        var finder = new IdGroupFinder(_cache);
        var groups = finder.FindGroups(_clientsByIp);
        ConcurrentDictionary<List<string>, List<string>> clientsIpList = new();
        foreach (var group in groups)
        {
            if (group.IDs.Count >= minimum)
            {
                clientsIpList.TryAdd(group.IPs.ToList(), group.IDs.ToList());
            }
        }

        return new ConcurrentDictionary<List<string>, List<string>>(
            clientsIpList.OrderByDescending(x => x.Value.Count));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (Client? client in _clients.Values)
        {
            if (client is { })
            {
                await client.DisposeAsync();
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public void UpdateSubscribeAddresses(byte[] addressBytes, IEnumerable<byte[]> addressesBytes)
    {
        var address = new Address(addressBytes);
        if (address == default)
        {
            Log.Warning("[{ClientAddress}] UpdateSubscribeAddresses set default address", address);
        }
        var addresses = addressesBytes.Select(a => new Address(a)).ToImmutableHashSet();
        if (_clients.TryGetValue(address, out Client? client) && client is { })
        {
            lock (client)
            {
                client.TargetAddresses = addresses;
            }

            Log.Information("[{ClientAddress}] UpdateSubscribeAddresses: {Addresses}", address, string.Join(", ", addresses));
        }
        else
        {
            Log.Error("[{ClientAddress}] target address does not contain in clients", address);
        }
    }

    public async Task RemoveClient(Address clientAddress)
    {
        if (_clients.TryGetValue(clientAddress, out Client? client) && client is { })
        {
            Log.Information("[{ClientAddress}] RemoveClient", clientAddress);
            await client.LeaveAsync();
            await client.DisposeAsync();
            _clients.TryRemove(clientAddress, out _);
        }
    }

    public List<Address> GetClients()
    {
        return _clients.Keys.ToList();
    }

    private async void RemoveClient(string clientAddressHex)
    {
        try
        {
            var clientAddress = new Address(ByteUtil.ParseHex(clientAddressHex));
            Log.Information("[{ClientAddress}] Client Disconnected. RemoveClient", clientAddress);
            RemoveClientByDevice(clientAddress);
            await RemoveClient(clientAddress);
        }
        catch (Exception e)
        {
            Log.Error(e, "[{ClientAddress}] Error while removing client.", clientAddressHex);
        }
    }
}
