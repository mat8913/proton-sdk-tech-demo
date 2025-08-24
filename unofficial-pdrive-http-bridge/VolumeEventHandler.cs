using System;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public sealed class VolumeEventHandler
{
    private readonly string _volumeId;

    public VolumeEventHandler(VolumeId volumeId)
    {
        _volumeId = volumeId.Value ?? throw new ArgumentNullException(nameof(volumeId));
    }

    public void Connect(VolumeEventChannel channel)
    {
        channel.NodeCreated += OnNodeCreated;
        channel.NodeMetadataChanged += OnNodeMetadataChanged;
        channel.FileContentsChanged += OnFileContentsChanged;
        channel.NodeDeleted += OnNodeDeleted;
    }

    public void OnNodeCreated(VolumeEventId eventId, INode node)
    {
        Console.WriteLine($"{eventId} Created: {node.Name} {node.NodeIdentity.NodeId.Value}");
    }

    public void OnNodeMetadataChanged(VolumeEventId eventId, INode node)
    {
        Console.WriteLine($"{eventId} Metadata changed: {node.Name} {node.NodeIdentity.NodeId.Value}");
    }

    public void OnFileContentsChanged(VolumeEventId eventId, FileNode node)
    {
        Console.WriteLine($"{eventId} Contents changed: {node.Name} {node.NodeIdentity.NodeId.Value}");
    }

    public void OnNodeDeleted(VolumeEventId eventId, VolumeId volumeId, LinkId nodeId)
    {
        if (_volumeId != volumeId.Value)
        {
            throw new InvalidOperationException($"Wrong volume ID. Expected: {_volumeId}. Got: {volumeId.Value}.");
        }

        Console.WriteLine($"{eventId} Deleted: {nodeId.Value}");
    }
}
