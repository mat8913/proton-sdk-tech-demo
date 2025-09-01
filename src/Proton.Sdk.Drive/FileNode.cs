using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Proton.Cryptography.Pgp;
using Proton.Sdk.Cryptography;
using Proton.Sdk.Drive.Files;
using Proton.Sdk.Drive.Serialization;

namespace Proton.Sdk.Drive;

public sealed partial class FileNode : INode
{
    internal const string CacheContentKeyValueName = "content-key";

    internal FileNode(
        NodeIdentity nodeIdentity,
        LinkId? parentId,
        string name,
        ByteString nameHashDigest,
        NodeState state,
        (RevisionDto Properties, ExtendedAttributes ExtendedAttributes)? activeRevision = null,
        string? mediaType = null)
    {
        NodeIdentity = nodeIdentity;
        ParentId = parentId;
        Name = name;
        NameHashDigest = nameHashDigest;
        State = state;
        MediaType = mediaType;

        if (activeRevision is not null)
        {
            var (activeRevisionProperties, extendedAttributes) = activeRevision.Value;
            ActiveRevision = new Revision(nodeIdentity.VolumeId, nodeIdentity.NodeId, activeRevisionProperties, extendedAttributes);
        }
    }

    internal static async Task<FileUploadResponse> CreateFileAsync(
        ProtonDriveClient client,
        FileUploadRequest fileUploadRequest,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        var parentFolderIdentity = fileUploadRequest.ParentFolderIdentity;
        var parentFolderKey = await Node.GetKeyAsync(client, parentFolderIdentity, cancellationToken).ConfigureAwait(false);

        var signingKey = await client.Account.GetAddressPrimaryKeyAsync(
            fileUploadRequest.ShareMetadata.MembershipAddressId,
            cancellationToken).ConfigureAwait(false);

        var parentFolderHashKey = await Node.GetHashKeyAsync(
            client,
            parentFolderIdentity,
            cancellationToken).ConfigureAwait(false);

        Node.GetCommonCreationParameters(
            fileUploadRequest.Name,
            parentFolderKey,
            parentFolderHashKey.Span,
            signingKey,
            out var key,
            out var nameSessionKey,
            out var passphraseSessionKey,
            out var encryptedName,
            out var nameHashDigest,
            out var encryptedKeyPassphrase,
            out var passphraseSignature,
            out var lockedKeyBytes);

        var contentKey = PgpSessionKey.Generate();
        var (contentKeyToken, _) = contentKey.Export();

        var parameters = new FileCreationParameters
        {
            ClientId = client.ClientId,
            Name = encryptedName,
            NameHashDigest = nameHashDigest,
            ParentLinkId = fileUploadRequest.ParentFolderIdentity.NodeId.Value,
            Passphrase = encryptedKeyPassphrase,
            PassphraseSignature = passphraseSignature,
            SignatureEmailAddress = fileUploadRequest.ShareMetadata.MembershipEmailAddress,
            Key = lockedKeyBytes,
            MediaType = fileUploadRequest.MimeType,
            ContentKeyPacket = key.EncryptSessionKey(contentKey),
            ContentKeyPacketSignature = key.Sign(contentKeyToken),
        };

        LinkId createdNodeId;
        RevisionId createdRevisionId;
        try
        {
            var response = await client.FilesApi.CreateFileAsync(parentFolderIdentity.ShareId, parameters, cancellationToken, operationId)
                .ConfigureAwait(false);

            createdNodeId = new LinkId(response.Identities.LinkId);
            createdRevisionId = new RevisionId(response.Identities.RevisionId);

            client.SecretsCache.Set(Node.GetNodeKeyCacheKey(parentFolderIdentity.VolumeId, createdNodeId), key.ToBytes());
            client.SecretsCache.Set(Node.GetNameSessionKeyCacheKey(parentFolderIdentity.VolumeId, createdNodeId), nameSessionKey.Export().Token);
            client.SecretsCache.Set(Node.GetPassphraseSessionKeyCacheKey(parentFolderIdentity.VolumeId, createdNodeId), passphraseSessionKey.Export().Token);
            client.SecretsCache.Set(GetContentKeyCacheKey(parentFolderIdentity.VolumeId, createdNodeId), contentKeyToken);
        }
        catch (ProtonApiException<RevisionConflictResponse> ex) when (ex.Response is { Conflict: { LinkId: not null, DraftClientId: not null, DraftRevisionId: not null } })
        {
            if (ex.Response.Conflict.DraftClientId != client.ClientId)
            {
                throw;
            }

            createdNodeId = new LinkId(ex.Response.Conflict.LinkId);
            createdRevisionId = new RevisionId(ex.Response.Conflict.DraftRevisionId);

            if (operationId is not null)
            {
                await Node.GetAsync(
                    client,
                    fileUploadRequest.ShareMetadata.ShareId,
                    createdNodeId,
                    cancellationToken,
                    operationId).ConfigureAwait(false);
            }
        }

        var file = new FileNode
        {
            NodeIdentity = new NodeIdentity
            {
                VolumeId = parentFolderIdentity.VolumeId,
                NodeId = createdNodeId,
                ShareId = parentFolderIdentity.ShareId,
            },
            ParentId = fileUploadRequest.ParentFolderIdentity.NodeId,
            Name = fileUploadRequest.Name,
            NameHashDigest = ByteStringExtensions.FromMemory(nameHashDigest),
            State = NodeState.Draft,
        };

        var draftRevision = new Revision(
            file.NodeIdentity.VolumeId,
            createdNodeId,
            createdRevisionId,
            RevisionState.Draft,
            0,
            default);

        return new FileUploadResponse
        {
            File = file,
            Revision = draftRevision,
        };
    }

    internal static async Task<Revision[]> GetFileRevisionsAsync(ProtonDriveClient client, INodeIdentity fileNodeIdentity, CancellationToken cancellationToken)
    {
        var fileKey = await Node.GetKeyAsync(client, fileNodeIdentity, cancellationToken).ConfigureAwait(false);

        var response = await client.FilesApi.GetRevisionsAsync(fileNodeIdentity.ShareId, fileNodeIdentity.NodeId, cancellationToken).ConfigureAwait(false);

        return await Task.WhenAll(response.Revisions.Select(
            async dto =>
            {
                var extendedAttributes = await DecryptExtendedAttributesAsync(
                    client,
                    fileNodeIdentity.VolumeId,
                    fileNodeIdentity.NodeId,
                    new RevisionId(dto.Id),
                    fileKey,
                    dto.ExtendedAttributes,
                    dto.SignatureEmailAddress,
                    cancellationToken).ConfigureAwait(false);

                return new Revision(fileNodeIdentity.VolumeId, fileNodeIdentity.NodeId, dto, extendedAttributes);
            })).ConfigureAwait(false);
    }

    internal static async Task<Revision> GetFileRevisionAsync(
        ProtonDriveClient client,
        NodeIdentity fileNodeIdentity,
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        var response = await client.FilesApi.GetRevisionAsync(fileNodeIdentity.ShareId, fileNodeIdentity.NodeId, revisionId, 1, 1, true, cancellationToken)
            .ConfigureAwait(false);

        var fileKey = await Node.GetKeyAsync(client, fileNodeIdentity, cancellationToken).ConfigureAwait(false);

        var extendedAttributes = response.Revision.ExtendedAttributes is not null
            ? await DecryptExtendedAttributesAsync(
                client,
                fileNodeIdentity.VolumeId,
                fileNodeIdentity.NodeId,
                revisionId,
                fileKey,
                response.Revision.ExtendedAttributes,
                response.Revision.SignatureEmailAddress,
                cancellationToken).ConfigureAwait(false)
            : default;

        return new Revision(fileNodeIdentity.VolumeId, fileNodeIdentity.NodeId, response.Revision, extendedAttributes);
    }

    internal static async Task<PgpSessionKey> GetContentKeyAsync(
        ProtonDriveClient client,
        INodeIdentity nodeIdentity,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        if (!client.SecretsCache.TryUse(
            GetContentKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId),
            (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256),
            out var nameKey))
        {
            client.Logger.LogInformation("Cache miss for content key of {NodeId}", nodeIdentity.NodeId.Value);
            await Node.GetAsync(client, nodeIdentity.ShareId, nodeIdentity.NodeId, cancellationToken, operationId).ConfigureAwait(false);
            client.Logger.LogInformation("Fetched info for {NodeId}", nodeIdentity.NodeId.Value);

            if (!client.SecretsCache.TryUse(
                GetContentKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId),
                (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256),
                out nameKey))
            {
                throw new ProtonApiException($"Could not get content key for nodeId {nodeIdentity.NodeId} shareId {nodeIdentity.ShareId} volumeId {nodeIdentity.VolumeId}");
            }
        }

        return nameKey;
    }

    internal static async Task<ExtendedAttributes> DecryptExtendedAttributesAsync(
        ProtonDriveClient client,
        VolumeId volumeId,
        LinkId fileId,
        RevisionId revisionId,
        PgpPrivateKey fileKey,
        PgpArmoredMessage? encryptedExtendedAttributes,
        string? signatureEmailAddress,
        CancellationToken cancellationToken)
    {
        if (encryptedExtendedAttributes is null)
        {
            return default;
        }

        PgpKeyRing verificationKeyRing;

        if (!string.IsNullOrEmpty(signatureEmailAddress))
        {
            try
            {
                var verificationKeys = await client.Account.GetAddressPublicKeysAsync(signatureEmailAddress, cancellationToken).ConfigureAwait(false);
                verificationKeyRing = new PgpKeyRing(verificationKeys);
            }
            catch (Exception e)
            {
                verificationKeyRing = default;
                client.Logger.LogError(e, "Failed to get address public keys for extended attributes verification");
            }
        }
        else
        {
            verificationKeyRing = new PgpKeyRing(fileKey);
        }

        ArraySegment<byte> serializedExtendedAttributes;
        PgpVerificationResult verificationResult;
        try
        {
            serializedExtendedAttributes = fileKey.DecryptAndVerify(encryptedExtendedAttributes.Value, verificationKeyRing, out verificationResult);
        }
        catch (CryptographicException e)
        {
            throw new NodeMetadataDecryptionException(NodeMetadataPart.ExtendedAttributes, e);
        }

        if (verificationResult.Status is not PgpVerificationStatus.Ok)
        {
            client.Logger.LogWarning(
                "Signature verification failed for extended attributes (volume ID: {VolumeId}, file ID: {FileNodeId}, revision ID: {RevisionId})",
                volumeId,
                fileId,
                revisionId);
        }

        try
        {
            return JsonSerializer.Deserialize(serializedExtendedAttributes, ProtonDriveApiSerializerContext.Default.ExtendedAttributes);
        }
        catch (Exception e)
        {
            client.Logger.LogError(
                e,
                "Failed to decrypt extended attributes (volume ID: {VolumeId}, file ID: {FileNodeId}, revision ID: {RevisionId})",
                volumeId,
                fileId,
                revisionId);

            return default;
        }
    }

    internal static CacheKey GetContentKeyCacheKey(VolumeId volumeId, LinkId nodeId)
        => new(Node.CacheContextName, volumeId.Value, Node.CacheValueHolderName, nodeId.Value, CacheContentKeyValueName);

    internal static PgpSessionKey DecryptContentKey(
        ProtonDriveClient client,
        VolumeId volumeId,
        LinkId fileId,
        PgpPrivateKey nodeKey,
        ReadOnlyMemory<byte> contentKeyPacket,
        PgpArmoredSignature? contentKeySignature,
        PgpKeyRing verificationKeyRing,
        ISecretsCache secretsCache)
    {
        PgpSessionKey contentKey;
        try
        {
            contentKey = nodeKey.DecryptSessionKey(contentKeyPacket.Span);
        }
        catch (CryptographicException e)
        {
            throw new NodeMetadataDecryptionException(NodeMetadataPart.ContentKey, e);
        }

        secretsCache.Set(GetContentKeyCacheKey(volumeId, fileId), contentKey.Export().Token);
        client.Logger.LogDebug("Decrypted content key for {LinkId} and added it to cache", fileId.Value);

        if (contentKeySignature is null)
        {
            client.Logger.LogWarning("Missing content key signature (volume ID: {VolumeId}, file ID: {FileNodeId})", volumeId, fileId);
            return contentKey;
        }

        try
        {
            var verificationResult = verificationKeyRing.Verify(contentKey.Export().Token, contentKeySignature.Value);

            if (verificationResult.Status is not PgpVerificationStatus.Ok)
            {
                client.Logger.LogWarning("Signature verification failed for content key (volume ID: {VolumeId}, file ID: {FileNodeId})", volumeId, fileId);
            }
        }
        catch (Exception e)
        {
            client.Logger.LogError(e, "Error while verifying content key (volume ID: {VolumeId}, file ID: {FileNodeId})", volumeId, fileId);
        }

        return contentKey;
    }
}
