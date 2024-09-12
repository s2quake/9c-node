using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace NineChronicles.Node;

internal sealed class IdGroupFinder(IMemoryCache memoryCache)
{
    private readonly Dictionary<string, List<string>> _adjacencyList = [];
    private readonly HashSet<string> _visited = [];
    private readonly IMemoryCache _memoryCache = memoryCache;

    public List<(HashSet<string> IPs, HashSet<string> IDs)> FindGroups(ConcurrentDictionary<string, HashSet<string>> dict)
    {
        // Create a serialized version of the input for caching purposes
        var serializedInput = "key";

        // Check cache
        if (_memoryCache.TryGetValue(serializedInput, out List<(HashSet<string> IPs, HashSet<string> IDs)> cachedResult))
        {
            return cachedResult;
        }

        // Step 1: Construct the adjacency list
        foreach (var kvp in dict)
        {
            var ip = kvp.Key;
            if (!_adjacencyList.ContainsKey(ip))
            {
                _adjacencyList[ip] = [];
            }

            foreach (var id in kvp.Value)
            {
                _adjacencyList[ip].Add(id);

                if (!_adjacencyList.ContainsKey(id))
                {
                    _adjacencyList[id] = [];
                }
                _adjacencyList[id].Add(ip);
            }
        }

        // Step 2: DFS to find connected components
        var groups = new List<(HashSet<string> IPs, HashSet<string> IDs)>();
        foreach (var node in _adjacencyList.Keys)
        {
            if (!_visited.Contains(node))
            {
                var ips = new HashSet<string>();
                var ids = new HashSet<string>();
                DFS(node, ips, ids, dict);
                groups.Add((ips, ids));
            }
        }

        // Cache the result before returning. Here we set a sliding expiration of 1 hour.
        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        };
        _memoryCache.Set(serializedInput, groups, cacheEntryOptions);

        return groups;
    }

    private void DFS(
        string node,
        HashSet<string> ips,
        HashSet<string> ids,
        ConcurrentDictionary<string, HashSet<string>> dict)
    {
        if (_visited.Contains(node))
        {
            return;
        }

        _visited.Add(node);

        // if node is an IP
        if (dict.ContainsKey(node))
        {
            ips.Add(node);
        }
        else
        {
            ids.Add(node);
        }

        foreach (var neighbor in _adjacencyList[node])
        {
            if (!_visited.Contains(neighbor))
            {
                DFS(neighbor, ips, ids, dict);
            }
        }
    }
}
