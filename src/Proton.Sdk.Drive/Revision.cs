using Google.Protobuf;
using Google.Protobuf.Collections;
using Proton.Sdk.Drive.Files;

namespace Proton.Sdk.Drive;

public sealed partial class Revision : IRevisionForTransfer
{
    internal Revision(VolumeId volumeId, LinkId fileId, RevisionDto properties, ExtendedAttributes extendedAttributes)
        : this(volumeId, fileId, new RevisionId(properties.Id), properties.State, properties.Size, extendedAttributes, GetSamplesSha256Digests(properties))
    {
        ManifestSignature = ByteStringExtensions.FromMemory(properties.ManifestSignature);
        SignatureEmailAddress = properties.SignatureEmailAddress;
        CreationTime = (long)(properties.CreationTime - new DateTime(1970, 1, 1)).TotalSeconds;
        SamplesSha256Digests.Add(GetSamplesSha256Digests(properties));
    }

    internal Revision(VolumeId volumeId, LinkId fileId, RevisionId id, RevisionState state, long size, in ExtendedAttributes extendedAttributes)
        : this(volumeId, fileId, id, state, size, extendedAttributes, [])
    {
    }

    private Revision(
        VolumeId volumeId,
        LinkId fileId,
        RevisionId id,
        RevisionState state,
        long size,
        in ExtendedAttributes extendedAttributes,
        RepeatedField<ByteString> previewImageSha256Digests)
    {
        VolumeId = volumeId;
        FileId = fileId;
        RevisionId = id;
        State = state;
        Size = extendedAttributes.Common?.Size ?? 0;
        QuotaConsumption = size;
        SamplesSha256Digests.Add(previewImageSha256Digests);
        if (extendedAttributes.Common is not null)
        {
            BlockSizes.AddRange(extendedAttributes.Common.BlockSizes);
        }
    }

    public RevisionMetadata Metadata()
    {
        var revisionMetadata = new RevisionMetadata
        {
            RevisionId = RevisionId,
            State = State,
            ManifestSignature = ManifestSignature,
            SignatureEmailAddress = SignatureEmailAddress,
        };

        revisionMetadata.SamplesSha256Digests.Add(SamplesSha256Digests);
        revisionMetadata.BlockSizes.Add(BlockSizes);
        return revisionMetadata;
    }

    internal static async Task<Revision> CreateAsync(
        ProtonDriveClient client,
        IShareForCommand share,
        INodeIdentity file,
        RevisionId? knownLastRevisionId,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        var parameters = new RevisionCreationParameters
        {
            CurrentRevisionId = knownLastRevisionId?.Value,
            ClientId = client.ClientId,
        };

        RevisionId revisionId;
        try
        {
            var revisionResponse = await client.FilesApi.CreateRevisionAsync(share.ShareId, file.NodeId, parameters, cancellationToken, operationId)
                .ConfigureAwait(false);

            revisionId = new RevisionId(revisionResponse.Identity.RevisionId);
        }
        catch (ProtonApiException<RevisionConflictResponse> ex) when (ex.Response is { Conflict: { DraftClientId: not null, DraftRevisionId: not null } })
        {
            if (ex.Response.Conflict.DraftClientId != client.ClientId)
            {
                throw;
            }

            revisionId = new RevisionId(ex.Response.Conflict.DraftRevisionId);
        }

        return new Revision(file.VolumeId, file.NodeId, revisionId, RevisionState.Draft, 0, default);
    }

    internal static async Task<RevisionReader> OpenForReadingAsync(
        ProtonDriveClient client,
        INodeIdentity fileIdentity,
        IRevisionForTransfer revisionMetadata,
        Action<int> releaseBlockListingAction,
        CancellationToken cancellationToken,
        long startPos = 0,
        byte[]? operationId = null)
    {
        if (revisionMetadata.State is RevisionState.Draft)
        {
            throw new InvalidOperationException("Draft revision cannot be opened for reading");
        }

        var contentKey = await FileNode.GetContentKeyAsync(client, fileIdentity, cancellationToken).ConfigureAwait(false);
        var fileKey = await Node.GetKeyAsync(client, fileIdentity, cancellationToken).ConfigureAwait(false);

        var startBlockIndex = BlockUtils.GetBlockIndexFromFileIndex(revisionMetadata.BlockSizes, startPos);

        var revisionResponse = await client.FilesApi.GetRevisionAsync(
            fileIdentity.ShareId,
            fileIdentity.NodeId,
            revisionMetadata.RevisionId,
            RevisionReader.MinBlockIndex + startBlockIndex.BlockNumber,
            RevisionReader.BlockPageSize,
            false,
            cancellationToken,
            operationId).ConfigureAwait(false);

        await client.BlockDownloader.FileSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new RevisionReader(client, fileIdentity, revisionMetadata, fileKey, contentKey, revisionResponse, startBlockIndex, releaseBlockListingAction);
    }

    internal static async Task<RevisionWriter> OpenForWritingAsync(
        ProtonDriveClient client,
        RevisionUploadRequest revisionUploadRequest,
        Action<int> releaseBlocksAction,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        if (revisionUploadRequest.RevisionMetadata.State is not RevisionState.Draft)
        {
            throw new InvalidOperationException("Non-draft revision cannot be opened for writing");
        }

        var fileKey = await Node.GetKeyAsync(client, revisionUploadRequest.FileIdentity, cancellationToken, operationId).ConfigureAwait(false);
        var contentKey = await FileNode.GetContentKeyAsync(client, revisionUploadRequest.FileIdentity, cancellationToken, operationId).ConfigureAwait(false);
        var signingKey = await client.Account.GetAddressPrimaryKeyAsync(revisionUploadRequest.ShareMetadata.MembershipAddressId, cancellationToken).ConfigureAwait(false);

        await client.BlockUploader.FileSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        const int targetBlockSize = RevisionWriter.DefaultBlockSize;

        return new RevisionWriter(
            client,
            revisionUploadRequest.ShareMetadata,
            revisionUploadRequest.FileIdentity.NodeId,
            revisionUploadRequest.RevisionMetadata.RevisionId,
            fileKey,
            contentKey,
            signingKey,
            releaseBlocksAction,
            targetBlockSize,
            targetBlockSize * 3 / 2);
    }

    internal static async Task DeleteAsync(FilesApiClient filesApi, ShareBasedRevisionIdentity shareBasedRevisionIdentity, CancellationToken cancellationToken)
    {
        await filesApi.DeleteRevisionAsync(shareBasedRevisionIdentity.ShareId, shareBasedRevisionIdentity.NodeId, shareBasedRevisionIdentity.RevisionId, cancellationToken).ConfigureAwait(false);
    }

    private static RepeatedField<ByteString> GetSamplesSha256Digests(RevisionDto properties)
    {
        if (properties.Thumbnails is null)
        {
            return [];
        }

        Span<ThumbnailType> keys = stackalloc ThumbnailType[properties.Thumbnails.Count];
        var digests = new RepeatedField<ByteString>();

        for (var i = 0; i < properties.Thumbnails.Count; ++i)
        {
            var thumbnail = properties.Thumbnails[i];
            keys[i] = thumbnail.Type;
            digests.Add(ByteStringExtensions.FromMemory(thumbnail.HashDigest));
        }

        keys.Sort(digests.ToArray().AsSpan());

        return digests;
    }
}
