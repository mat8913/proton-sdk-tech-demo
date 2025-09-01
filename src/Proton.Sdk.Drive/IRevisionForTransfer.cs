using Google.Protobuf;
using Google.Protobuf.Collections;

namespace Proton.Sdk.Drive;

public interface IRevisionForTransfer
{
    RevisionId RevisionId { get; }
    RevisionState State { get; }
    ByteString? ManifestSignature { get; }
    string? SignatureEmailAddress { get; }
    RepeatedField<ByteString> SamplesSha256Digests { get; }
    RepeatedField<int> BlockSizes { get; }
}
