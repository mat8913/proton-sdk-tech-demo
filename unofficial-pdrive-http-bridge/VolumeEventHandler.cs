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
        channel.NodeCreated += Wrap1<INode>(OnNodeCreated);
        channel.NodeMetadataChanged += Wrap1<INode>(OnNodeMetadataChanged);
        channel.FileContentsChanged += Wrap1<FileNode>(OnFileContentsChanged);
        channel.NodeDeleted += Wrap2<VolumeId, LinkId>(OnNodeDeleted);
    }

    private void OnNodeCreated(VolumeEventId eventId, INode node)
    {
        var fileNode = node as FileNode;

        Console.WriteLine($"""

        [NODEWATCHER] {eventId}
            NodeCreated
            VolumeId: {node.NodeIdentity.VolumeId.Value}
            NodeId: {node.NodeIdentity.NodeId.Value}
            Name: {node.Name}
            Parent NodeId: {node.ParentId?.Value ?? "(null)"}
            State: {node.State}
            MediaType: {fileNode?.MediaType ?? "(null)"}
            Revision: {fileNode?.ActiveRevision?.RevisionId?.Value ?? "(null)"}
            Size: {fileNode?.ActiveRevision?.Size.ToString() ?? "(null)"}
            ModTime: {fileNode?.ActiveRevision?.CreationTime.ToString() ?? "(null)"}

        """);
    }

    private void OnNodeMetadataChanged(VolumeEventId eventId, INode node)
    {
        var fileNode = node as FileNode;

        Console.WriteLine($"""

        [NODEWATCHER] {eventId}
            NodeMetadataChanged
            VolumeId: {node.NodeIdentity.VolumeId.Value}
            NodeId: {node.NodeIdentity.NodeId.Value}
            Name: {node.Name}
            Parent NodeId: {node.ParentId?.Value ?? "(null)"}
            State: {node.State}
            MediaType: {fileNode?.MediaType ?? "(null)"}
            Revision: {fileNode?.ActiveRevision?.RevisionId?.Value ?? "(null)"}
            Size: {fileNode?.ActiveRevision?.Size.ToString() ?? "(null)"}
            ModTime: {fileNode?.ActiveRevision?.CreationTime.ToString() ?? "(null)"}

        """);
    }

    private void OnFileContentsChanged(VolumeEventId eventId, FileNode node)
    {
        Console.WriteLine($"""

        [NODEWATCHER] {eventId}
            FileContentsChanged
            VolumeId: {node.NodeIdentity.VolumeId.Value}
            NodeId: {node.NodeIdentity.NodeId.Value}
            Name: {node.Name}
            Parent NodeId: {node.ParentId?.Value ?? "(null)"}
            State: {node.State}
            MediaType: {node.MediaType ?? "(null)"}
            Revision: {node.ActiveRevision?.RevisionId?.Value ?? "(null)"}
            Size: {node.ActiveRevision?.Size.ToString() ?? "(null)"}
            ModTime: {node.ActiveRevision?.CreationTime.ToString() ?? "(null)"}

        """);
    }

    private void OnNodeDeleted(VolumeEventId eventId, VolumeId volumeId, LinkId nodeId)
    {
        if (_volumeId != volumeId.Value)
        {
            throw new InvalidOperationException($"Wrong volume ID. Expected: {_volumeId}. Got: {volumeId.Value}.");
        }

        Console.WriteLine($"""

        [NODEWATCHER] {eventId}
            NodeDeleted
            VolumeId: {volumeId.Value}
            NodeId: {nodeId.Value}

        """);
    }

    private Action<VolumeEventId, T> Wrap1<T>(Action<VolumeEventId, T> f)
    {
        return (eventId, x) =>
        {
            try
            {
                f(eventId, x);
            }
            catch (Exception ex)
            {
                Environment.FailFast($"Error handling VolumeEvent {eventId.Value}", ex);
            }
        };
    }

    private Action<VolumeEventId, T, U> Wrap2<T, U>(Action<VolumeEventId, T, U> f)
    {
        return (eventId, x, y) =>
        {
            try
            {
                f(eventId, x, y);
            }
            catch (Exception ex)
            {
                Environment.FailFast($"Error handling VolumeEvent {eventId.Value}", ex);
            }
        };
    }
}
