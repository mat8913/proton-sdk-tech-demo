namespace Proton.Sdk.Drive;

using Google.Protobuf;

public interface INode
{
    NodeIdentity NodeIdentity { get; }
    LinkId? ParentId { get; }
    string Name { get; }
    ByteString NameHashDigest { get; }
    NodeState State { get; }
    long ModificationTime { get; }
}
