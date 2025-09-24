using System;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public static class Converters
{
    public static DbModels.NodeMetadata ProtonNodeToDbModel(INode node)
    {
        // Note: Assumes State=Active.

        var fileNode = node as FileNode;

        return new DbModels.NodeMetadata
        {
            VolumeId = node.NodeIdentity.VolumeId.Value,
            NodeId = node.NodeIdentity.NodeId.Value,
            Name = node.Name,
            ParentNodeId = node.ParentId?.Value ?? string.Empty,
            IsFile = fileNode is not null,
            MediaType = fileNode?.MediaType,
            ActiveRevisionId = fileNode?.ActiveRevision.RevisionId.Value,
            Size = fileNode?.ActiveRevision.Size,
            ModificationTime = fileNode?.ActiveRevision.CreationTime,
        };
    }

    public static HttpModels.NodeMetadata DbModelNodeMetadataToHttpModel(string shareId, DbModels.NodeMetadata node)
    {
        var metadata = new HttpModels.NodeMetadata
        {
            NodeId = node.NodeId,
            Name = node.Name,
            ParentId = string.IsNullOrEmpty(node.ParentNodeId) ? null : node.ParentNodeId,
            State = "Active",
            ActiveRevisionId = node.ActiveRevisionId,
            Size = node.Size,
            Url = $"/volumes/{node.VolumeId}/shares/{shareId}/node-content/by-id/{node.NodeId}",
        };

        if (node.IsFile)
        {
            metadata.Type = HttpModels.NodeType.File;
        }
        else
        {
            metadata.Type = HttpModels.NodeType.Folder;
        }

        return metadata;
    }

    public static HttpModels.NodeMetadata ProtonNodeToHttpModel(string volumeId, string shareId, INode node)
    {
        var metadata = new HttpModels.NodeMetadata
        {
            NodeId = node.NodeIdentity.NodeId.Value,
            Name = node.Name,
            ParentId = node.ParentId?.Value,
            State = node.State.ToString(),
            Url = $"/volumes/{volumeId}/shares/{shareId}/node-content/by-id/{node.NodeIdentity.NodeId.Value}",
        };

        if (node is FileNode fileNode)
        {
            metadata.ActiveRevisionId = fileNode.ActiveRevision?.RevisionId?.Value;
            metadata.Size = fileNode.ActiveRevision?.Size;
            metadata.Type = HttpModels.NodeType.File;
        }
        else if (node is FolderNode)
        {
            metadata.Type = HttpModels.NodeType.Folder;
        }
        else
        {
            throw new InvalidOperationException($"Unknown node type: {node.GetType()}");
        }

        return metadata;
    }
}
