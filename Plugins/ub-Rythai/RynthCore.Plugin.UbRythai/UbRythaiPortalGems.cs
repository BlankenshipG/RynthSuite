using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// UB-ILT MiniRemote portal gem list + inventory lookup (name match) for RynthCore hosts.
/// </summary>
internal static class UbRythaiPortalGems
{
    /// <summary>Same canonical names as UB-ILT MiniRemote / Enlightenment recall gem list.</summary>
    public static readonly string[] CanonicalGemNames =
    {
        "Enlightened Facility Hub Portal Gem",
        "Facility Hub Portal Gem",
        "Infinite Town Network Portal Gem",
        "Lifestone Recall Portal Gem",
        "Frozen Valley Everlasting Portal Gem",
    };

    /// <summary>Attempts to find an inventory object id whose name matches <paramref name="gemName"/>.</summary>
    public static bool TryFindInventoryObjectIdByName(RynthCoreHost host, uint playerId, string gemName, out uint objectId)
    {
        objectId = 0;
        if (playerId == 0 || string.IsNullOrWhiteSpace(gemName))
            return false;
        if (!host.HasGetContainerContents || !host.HasGetNumContainedItems || !host.HasGetObjectName)
            return false;

        var visited = new HashSet<uint>();
        var queue = new Queue<(uint containerId, int depth)>();
        queue.Enqueue((playerId, 0));

        uint[] buf = new uint[512];
        while (queue.Count > 0)
        {
            (uint containerId, int depth) = queue.Dequeue();
            if (containerId == 0 || !visited.Add(containerId))
                continue;
            if (depth > 6)
                continue;

            int n = host.GetContainerContents(containerId, buf);
            for (int i = 0; i < n; i++)
            {
                uint id = buf[i];
                if (id == 0)
                    continue;

                if (host.TryGetObjectName(id, out string? name) && !string.IsNullOrWhiteSpace(name))
                {
                    if (name.Equals(gemName, StringComparison.OrdinalIgnoreCase))
                    {
                        objectId = id;
                        return true;
                    }
                }

                int contained = host.GetNumContainedItems(id);
                int subContainers = host.GetNumContainedContainers(id);
                if (contained > 0 || subContainers > 0)
                    queue.Enqueue((id, depth + 1));
            }
        }

        return false;
    }
}
