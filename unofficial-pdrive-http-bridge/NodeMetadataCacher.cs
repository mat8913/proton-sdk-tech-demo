using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public sealed class NodeMetadataCacher(NodeMetadataCache cache, ProtonDriveClient client)
{
    private readonly NodeMetadataCache _cache = cache;
    private readonly ProtonDriveClient _client = client;
    private readonly Dictionary<string, VolumeEventHandler> _volumeEventHandlers = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private bool _started = false;

    public async Task StartAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var volumes = await _cache.GetVolumes(ct);
            foreach (var volume in volumes)
            {
                if (_volumeEventHandlers.ContainsKey(volume.VolumeId))
                    continue;

                var channel = new VolumeEventChannel(_client, new(volume.VolumeId));
                channel.BaselineEventId = new(volume.LatestEventId);
                _volumeEventHandlers[volume.VolumeId] = new VolumeEventHandler(channel, _cache);
            }

            foreach (var handler in _volumeEventHandlers.Values)
            {
                handler.Start();
            }

            _started = true;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<List<DbModels.NodeMetadata>> GetChildren(string volumeId, string nodeId, string shareId, CancellationToken ct)
    {
        if (!_started)
            throw new InvalidOperationException("NodeMetadataCacher not started");

        var children = await _cache.TryGetChildren(volumeId, nodeId, ct);
        if (children is not null)
            return children;

        await _sync.WaitAsync(ct);
        try
        {
            var handler = await EnsureVolumeEventHandler(volumeId, ct);
            await handler.Stop();

            try
            {
                children = await _client.GetFolderChildrenAsync(new NodeIdentity(new(shareId), new(volumeId), new(nodeId)), ct)
                    .Select(ApiNodeToModel)
                    .OrderBy(x => x.Name)
                    .ToListAsync(ct);

                await _cache.SetChildren(handler.EventId, volumeId, nodeId, children, ct);

                return children;
            }
            finally
            {
                handler.Start();
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public static DbModels.NodeMetadata ApiNodeToModel(INode node)
    {
        var fileNode = node as FileNode;

        return new DbModels.NodeMetadata
        {
            VolumeId = node.NodeIdentity.VolumeId.Value,
            NodeId = node.NodeIdentity.NodeId.Value,
            Name = node.Name,
            ParentNodeId = node.ParentId!.Value,
            IsFile = fileNode is not null,
            MediaType = fileNode?.MediaType,
            ActiveRevisionId = fileNode?.ActiveRevision.RevisionId.Value,
            Size = fileNode?.ActiveRevision.Size,
            ModificationTime = fileNode?.ActiveRevision.CreationTime,
        };
    }

    // assumes lock is already taken
    private async Task<VolumeEventHandler> EnsureVolumeEventHandler(string volumeId, CancellationToken ct)
    {
        if (_volumeEventHandlers.TryGetValue(volumeId, out var handler))
            return handler;

        var channel = new VolumeEventChannel(_client, new(volumeId));
        channel.BaselineEventId = await channel.GetLatestEventIdAsync(ct);
        handler = new VolumeEventHandler(channel, _cache);
        _volumeEventHandlers[volumeId] = handler;
        return handler;
    }
}
