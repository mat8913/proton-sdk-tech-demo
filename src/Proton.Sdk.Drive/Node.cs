using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Proton.Cryptography.Pgp;
using Proton.Sdk.Cryptography;
using Proton.Sdk.Drive.Files;
using Proton.Sdk.Drive.Links;

namespace Proton.Sdk.Drive;

public class Node : INode
{
    internal const string CacheContextName = "drive.volume";
    internal const string CacheValueHolderName = "node";
    internal const string CacheNodeKeyValueName = "key";
    internal const string CacheNameSessionKeyValueName = "name-session-key";
    internal const string CachePassphraseSessionKeyValueName = "passphrase-session-key";

    internal Node(
        NodeIdentity nodeIdentity,
        LinkId? parentId,
        string name,
        ByteString nameHashDigest,
        NodeState state)
    {
        NodeIdentity = nodeIdentity;
        ParentId = parentId;
        Name = name;
        State = state;
        NameHashDigest = nameHashDigest;
    }

    protected Node()
    {
        throw new NotImplementedException();
    }

    public NodeIdentity NodeIdentity { get; }
    public LinkId? ParentId { get; }
    public string Name { get; }
    public ByteString NameHashDigest { get; }
    public NodeState State { get; }
    public long ModificationTime { get; }

    internal static async Task<INode> GetAsync(
        ProtonDriveClient client,
        ShareId shareId,
        LinkId itemId,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        var response = await client.LinksApi.GetLinkAsync(shareId, itemId, cancellationToken, operationId).ConfigureAwait(false);

        return await GetAsync(client, shareId, response.Link, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<INode> GetAsync(ProtonDriveClient client, ShareId shareId, Link link, CancellationToken cancellationToken)
    {
        using var parentKey = await GetParentKeyAsync(client, shareId, link, cancellationToken).ConfigureAwait(false);

        return await DecryptAsync(client, link, parentKey, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MoveAsync(
        ProtonDriveClient client,
        ShareId shareId,
        INode node,
        IShareForCommand destinationShare,
        INodeIdentity destinationFolderIdentity,
        string nameAtDestination,
        CancellationToken cancellationToken)
    {
        using var signingKey = await client.Account.GetAddressPrimaryKeyAsync(destinationShare.MembershipAddressId, cancellationToken).ConfigureAwait(false);
        var nodeIdentity = new NodeIdentity { ShareId = shareId, VolumeId = node.NodeIdentity.VolumeId, NodeId = node.NodeIdentity.NodeId };
        var destinationNodeIdentity = new NodeIdentity
        {
            ShareId = shareId,
            VolumeId = destinationFolderIdentity.VolumeId,
            NodeId = destinationFolderIdentity.NodeId,
        };

        using var nameSessionKey = await GetNameSessionKeyAsync(client, nodeIdentity, cancellationToken).ConfigureAwait(false);

        using var passphraseSessionKey = await GetPassphraseSessionKeyAsync(client, nodeIdentity, cancellationToken).ConfigureAwait(false);

        using var destinationFolderKey = await GetKeyAsync(
            client,
            destinationNodeIdentity,
            cancellationToken).ConfigureAwait(false);

        var destinationFolderHashKey = await GetHashKeyAsync(client, destinationFolderIdentity, cancellationToken).ConfigureAwait(false);

        GetNameParameters(
            nameAtDestination,
            destinationFolderKey,
            destinationFolderHashKey.Span,
            nameSessionKey,
            signingKey,
            out var encryptedName,
            out var nameHashDigest);

        var keyPassphraseKeyPacket = destinationFolderKey.EncryptSessionKey(passphraseSessionKey);

        var parameters = new MoveLinkParameters
        {
            ShareId = destinationShare.ShareId.Value,
            ParentLinkId = destinationFolderIdentity.NodeId.Value,
            KeyPassphrase = keyPassphraseKeyPacket,
            Name = encryptedName,
            NameHashDigest = nameHashDigest,
            NameSignatureEmailAddress = destinationShare.MembershipEmailAddress,
            OriginalNameHashDigest = node.NameHashDigest.Memory,
        };

        await client.LinksApi.MoveLinkAsync(shareId, node.NodeIdentity.NodeId, parameters, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RenameAsync(
        ProtonDriveClient client,
        IShareForCommand share,
        INode node,
        string newName,
        string newMediaType,
        CancellationToken cancellationToken)
    {
        var nodeIdentity = new NodeIdentity { ShareId = share.ShareId, VolumeId = node.NodeIdentity.VolumeId, NodeId = node.NodeIdentity.NodeId };

        // TODO: remove null forgiving operator
        var parentNodeIdentity = new NodeIdentity { ShareId = share.ShareId, VolumeId = node.NodeIdentity.VolumeId, NodeId = node.ParentId! };

        using var signingKey = await client.Account.GetAddressPrimaryKeyAsync(new AddressId(share.MembershipAddressId), cancellationToken).ConfigureAwait(false);
        using var nameSessionKey = await GetNameSessionKeyAsync(client, nodeIdentity, cancellationToken).ConfigureAwait(false);
        using var parentFolderKey = await GetKeyAsync(client, parentNodeIdentity, cancellationToken).ConfigureAwait(false);
        var parentFolderHashKey = await GetHashKeyAsync(client, parentNodeIdentity, cancellationToken).ConfigureAwait(false);

        GetNameParameters(newName, parentFolderKey, parentFolderHashKey.Span, nameSessionKey, signingKey, out var encryptedName, out var nameHashDigest);

        var parameters = new RenameLinkParameters
        {
            Name = encryptedName,
            NameHashDigest = nameHashDigest,
            NameSignatureEmailAddress = share.MembershipEmailAddress,
            MediaType = newMediaType,
            OriginalNameHashDigest = node.NameHashDigest.Memory,
        };

        await client.LinksApi.RenameLinkAsync(share.ShareId, nodeIdentity.NodeId, parameters, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PgpPrivateKey> GetKeyAsync(
        ProtonDriveClient client,
        INodeIdentity nodeIdentity,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        var cacheKey = GetNodeKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId);

        if (!client.SecretsCache.TryUse(cacheKey, (data, _) => PgpPrivateKey.Import(data), out var key))
        {
            client.Logger.LogInformation("Cache miss for node key of {NodeId}", nodeIdentity.NodeId.Value);
            await GetAsync(client, nodeIdentity.ShareId, nodeIdentity.NodeId, cancellationToken, operationId).ConfigureAwait(false);
            client.Logger.LogInformation("Fetched info for {NodeId}", nodeIdentity.NodeId.Value);

            if (!client.SecretsCache.TryUse(cacheKey, (data, _) => PgpPrivateKey.Import(data), out key))
            {
                throw new ProtonApiException($"Could not get node key for {nodeIdentity.NodeId}");
            }
        }

        return key;
    }

    internal static CacheKey GetNodeKeyCacheKey(VolumeId volumeId, LinkId nodeId)
        => new(CacheContextName, volumeId.Value, CacheValueHolderName, nodeId.Value, CacheNodeKeyValueName);

    internal static CacheKey GetNameSessionKeyCacheKey(VolumeId volumeId, LinkId nodeId)
        => new(CacheContextName, volumeId.Value, CacheValueHolderName, nodeId.Value, CacheNameSessionKeyValueName);

    internal static CacheKey GetPassphraseSessionKeyCacheKey(VolumeId volumeId, LinkId nodeId)
        => new(CacheContextName, volumeId.Value, CacheValueHolderName, nodeId.Value, CachePassphraseSessionKeyValueName);

    internal static void GetCommonCreationParameters(
        string name,
        PgpPrivateKey parentFolderKey,
        ReadOnlySpan<byte> parentFolderHashKey,
        PgpPrivateKey signingKey,
        out PgpPrivateKey key,
        out PgpSessionKey nameSessionKey,
        out PgpSessionKey passphraseSessionKey,
        out ArraySegment<byte> encryptedName,
        out ArraySegment<byte> nameHashDigest,
        out ArraySegment<byte> encryptedKeyPassphrase,
        out ArraySegment<byte> passphraseSignature,
        out ArraySegment<byte> lockedKeyBytes)
    {
        key = PgpPrivateKey.Generate("Drive key", "no-reply@proton.me", KeyGenerationAlgorithm.Default);
        nameSessionKey = PgpSessionKey.Generate();

        Span<byte> passphraseBuffer = stackalloc byte[Share.PassphraseMaxUtf8Length];
        var passphrase = Share.GeneratePassphrase(passphraseBuffer);

        passphraseSessionKey = PgpSessionKey.Generate();
        var passphraseEncryptionSecrets = new EncryptionSecrets(parentFolderKey, passphraseSessionKey);

        encryptedKeyPassphrase = PgpEncrypter.EncryptAndSign(passphrase, passphraseEncryptionSecrets, signingKey, out passphraseSignature);

        using var lockedKey = key.Lock(passphrase);
        lockedKeyBytes = lockedKey.ToBytes();

        GetNameParameters(name, parentFolderKey, parentFolderHashKey, nameSessionKey, signingKey, out encryptedName, out nameHashDigest);
    }

    internal static async Task<INode> DecryptAsync(ProtonDriveClient client, Link link, PgpPrivateKey parentKey, CancellationToken cancellationToken)
    {
        var secretsCache = client.SecretsCache;

        var volumeId = new VolumeId(link.VolumeId);
        var nodeId = new LinkId(link.Id);
        var parentId = link.ParentId is not null ? new LinkId(link.ParentId) : null;
        var state = (NodeState)link.State;

        var name = await DecryptNameAsync(client, volumeId, nodeId, parentKey, link.Name, link.NameSignatureEmailAddress, secretsCache, cancellationToken)
            .ConfigureAwait(false);

        var passphrase = await DecryptPassphraseAsync(
            client,
            volumeId,
            nodeId,
            parentKey,
            link.Passphrase,
            link.PassphraseSignature,
            link.SignatureEmailAddress,
            secretsCache,
            cancellationToken).ConfigureAwait(false);

        PgpPrivateKey key;
        try
        {
            key = PgpPrivateKey.ImportAndUnlock(link.Key, passphrase);
        }
        catch (Exception e)
        {
            throw new NodeMetadataDecryptionException(NodeMetadataPart.Key, e);
        }

        secretsCache.Set(GetNodeKeyCacheKey(volumeId, nodeId), key.ToBytes());
        client.Logger.LogDebug("Unlocked node key for {LinkId} and added it to cache", link.Id);

        var hashKeyAndContentKeyVerificationKeyRing =
            await GetNodeAndAddressVerificationKeyRingAsync(client, key, link.SignatureEmailAddress, cancellationToken).ConfigureAwait(false);

        if (link.Type == LinkType.Folder)
        {
            var folderProperties = link.FolderProperties ?? throw new ProtonApiException("Missing folder properties on link of type Folder.");

            FolderNode.DecryptHashKey(
                client,
                volumeId,
                nodeId,
                key,
                folderProperties.HashKey,
                hashKeyAndContentKeyVerificationKeyRing,
                secretsCache);

            return new FolderNode
            {
                NodeIdentity = new NodeIdentity
                {
                    VolumeId = volumeId,
                    NodeId = nodeId,
                },
                ParentId = parentId,
                Name = name,
                NameHashDigest = ByteStringExtensions.FromMemory(link.NameHashDigest),
                State = state,
                ModificationTime = new DateTimeOffset(link.ModificationTime).ToUnixTimeSeconds(),
            };
        }

        var fileProperties = link.FileProperties ?? throw new ProtonApiException("Missing file properties on link of type File.");

        using var contentKey = FileNode.DecryptContentKey(
            client,
            volumeId,
            nodeId,
            key,
            fileProperties.ContentKeyPacket,
            fileProperties.ContentKeyPacketSignature,
            hashKeyAndContentKeyVerificationKeyRing,
            secretsCache);

        (RevisionDto Properties, ExtendedAttributes ExtendedAttributes)? activeRevision;
        if (fileProperties.ActiveRevision is not null)
        {
            var extendedAttributes = await FileNode.DecryptExtendedAttributesAsync(
                client,
                volumeId,
                nodeId,
                new RevisionId(fileProperties.ActiveRevision.Id),
                key,
                link.ExtendedAttributes,
                fileProperties.ActiveRevision.SignatureEmailAddress,
                cancellationToken).ConfigureAwait(false);

            activeRevision = (fileProperties.ActiveRevision, extendedAttributes);
        }
        else
        {
            activeRevision = null;
        }

        return new FileNode(
            new NodeIdentity { NodeId = nodeId, VolumeId = volumeId },
            parentId,
            name,
            ByteStringExtensions.FromMemory(link.NameHashDigest),
            state,
            activeRevision)
        {
            ModificationTime = new DateTimeOffset(link.ModificationTime).ToUnixTimeSeconds(),
            MediaType = link.MediaType,
        };
    }

    internal static async Task<ReadOnlyMemory<byte>> GetHashKeyAsync(
        ProtonDriveClient client,
        INodeIdentity nodeIdentity,
        CancellationToken cancellationToken)
    {
        var cacheKey = FolderNode.GetHashKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId);

        if (!client.SecretsCache.TryUse(cacheKey, (data, _) => data.ToArray().AsMemory(), out var hashKey))
        {
            await GetAsync(client, nodeIdentity.ShareId, nodeIdentity.NodeId, cancellationToken).ConfigureAwait(false);

            if (!client.SecretsCache.TryUse(cacheKey, (data, _) => data.ToArray().AsMemory(), out hashKey))
            {
                throw new ProtonApiException($"Could not get hash key for {nodeIdentity.NodeId}");
            }
        }

        return hashKey;
    }

    internal static async Task<string> DecryptNameAsync(
        ProtonDriveClient client,
        VolumeId volumeId,
        LinkId nodeId,
        PgpPrivateKey parentKey,
        PgpArmoredMessage encryptedName,
        string? signatureEmailAddress,
        ISecretsCache? secretsCache,
        CancellationToken cancellationToken)
    {
        PgpSessionKey sessionKey;
        try
        {
            sessionKey = parentKey.DecryptSessionKey(encryptedName);
        }
        catch (CryptographicException e)
        {
            throw new NodeMetadataDecryptionException(NodeMetadataPart.Name, e);
        }

        using (sessionKey)
        {
            secretsCache?.Set(GetNameSessionKeyCacheKey(volumeId, nodeId), sessionKey.Export().Token);

            PgpKeyRing verificationKeyRing;
            if (!string.IsNullOrEmpty(signatureEmailAddress))
            {
                var verificationKeys = await client.Account.GetAddressPublicKeysAsync(signatureEmailAddress, cancellationToken).ConfigureAwait(false);
                verificationKeyRing = new PgpKeyRing(verificationKeys);
            }
            else
            {
                verificationKeyRing = new PgpKeyRing(parentKey);
            }

            string name;
            PgpVerificationResult verificationResult;
            try
            {
                name = sessionKey.DecryptAndVerifyText(encryptedName, verificationKeyRing, out verificationResult);
            }
            catch (CryptographicException e)
            {
                throw new NodeMetadataDecryptionException(NodeMetadataPart.Name, e);
            }

            if (verificationResult.Status is not PgpVerificationStatus.Ok)
            {
                client.Logger.LogWarning(
                    "Signature verification failed for name (volume ID: {VolumeId}, file ID: {NodeId}, status: {Status})",
                    volumeId,
                    nodeId,
                    verificationResult.Status);
            }

            return name;
        }
    }

    internal static async Task<PgpKeyRing> GetNodeAndAddressVerificationKeyRingAsync(
        ProtonDriveClient client,
        PgpPrivateKey nodeKey,
        string? signatureEmailAddress,
        CancellationToken cancellationToken)
    {
        var addressVerificationKeys = !string.IsNullOrEmpty(signatureEmailAddress)
            ? await client.Account.GetAddressPublicKeysAsync(signatureEmailAddress, cancellationToken).ConfigureAwait(false)
            : [];

        var verificationKeys = new List<PgpKey>(addressVerificationKeys.Count + 1);
        verificationKeys.AddRange(addressVerificationKeys.Select(x => (PgpKey)x));
        verificationKeys.Add(nodeKey);

        return new PgpKeyRing(verificationKeys);
    }

    private static async Task<ArraySegment<byte>> DecryptPassphraseAsync(
        ProtonDriveClient client,
        VolumeId volumeId,
        LinkId nodeId,
        PgpPrivateKey parentKey,
        PgpArmoredMessage encryptedPassphrase,
        PgpArmoredSignature? signature,
        string? signatureEmailAddress,
        ISecretsCache secretsCache,
        CancellationToken cancellationToken)
    {
        PgpSessionKey sessionKey;
        try
        {
            sessionKey = parentKey.DecryptSessionKey(encryptedPassphrase);
        }
        catch (CryptographicException e)
        {
            throw new NodeMetadataDecryptionException(NodeMetadataPart.Passphrase, e);
        }

        using (sessionKey)
        {
            secretsCache.Set(GetPassphraseSessionKeyCacheKey(volumeId, nodeId), sessionKey.Export().Token);

            ArraySegment<byte> passphrase;
            if (signature is null)
            {
                client.Logger.LogWarning("Missing passphrase signature (volume ID: {VolumeId}, node ID: {NodeId})", volumeId, nodeId);

                try
                {
                    passphrase = sessionKey.Decrypt(encryptedPassphrase);
                }
                catch (CryptographicException e)
                {
                    throw new NodeMetadataDecryptionException(NodeMetadataPart.Passphrase, e);
                }
            }
            else
            {
                PgpKeyRing verificationKeyRing;
                if (!string.IsNullOrEmpty(signatureEmailAddress))
                {
                    var verificationKeys = await client.Account.GetAddressPublicKeysAsync(signatureEmailAddress, cancellationToken).ConfigureAwait(false);
                    verificationKeyRing = new PgpKeyRing(verificationKeys);
                }
                else
                {
                    verificationKeyRing = new PgpKeyRing(parentKey);
                }

                try
                {
                    passphrase = sessionKey.DecryptAndVerify(
                        encryptedPassphrase,
                        signature.Value.Bytes.Span,
                        verificationKeyRing,
                        out var verificationResult);

                    if (verificationResult.Status is not PgpVerificationStatus.Ok)
                    {
                        client.Logger.LogWarning("Signature verification failed for passphrase (volume ID: {VolumeId}, node ID: {NodeId})", volumeId, nodeId);
                    }
                }
                catch (CryptographicException e)
                {
                    throw new NodeMetadataDecryptionException(NodeMetadataPart.Passphrase, e);
                }
            }

            return passphrase;
        }
    }

    private static void GetNameParameters(
        string name,
        PgpPrivateKey parentFolderKey,
        ReadOnlySpan<byte> parentFolderHashKey,
        PgpSessionKey nameSessionKey,
        PgpPrivateKey signingKey,
        out ArraySegment<byte> encryptedName,
        out ArraySegment<byte> nameHashDigest)
    {
        var maxNameByteLength = Encoding.UTF8.GetByteCount(name);
        var nameBytes = MemoryProvider.GetHeapMemoryIfTooLargeForStack<byte>(maxNameByteLength, out var nameHeapMemoryOwner)
            ? nameHeapMemoryOwner.Memory.Span
            : stackalloc byte[maxNameByteLength];

        using (nameHeapMemoryOwner)
        {
            var nameByteLength = Encoding.UTF8.GetBytes(name, nameBytes);
            nameBytes = nameBytes[..nameByteLength];

            encryptedName = PgpEncrypter.EncryptAndSignText(name, new EncryptionSecrets(parentFolderKey, nameSessionKey), signingKey);

            nameHashDigest = HMACSHA256.HashData(parentFolderHashKey, nameBytes);
        }
    }

    private static async Task<PgpPrivateKey> GetParentKeyAsync(ProtonDriveClient client, ShareId shareId, Link link, CancellationToken cancellationToken)
    {
        if (link.ParentId is null)
        {
            return await Share.GetKeyAsync(client, shareId, cancellationToken).ConfigureAwait(false);
        }

        var volumeId = new VolumeId(link.VolumeId);

        if (!client.SecretsCache.TryUse(GetNodeKeyCacheKey(volumeId, new LinkId(link.ParentId)), (bytes, _) => PgpPrivateKey.Import(bytes), out var key))
        {
            var linkAncestry = new Stack<Link>(8);

            var currentId = new LinkId(link.ParentId);

            while (currentId is not null)
            {
                var response = await client.LinksApi.GetLinkAsync(shareId, currentId, cancellationToken).ConfigureAwait(false);

                if (client.SecretsCache.TryUse(
                    GetNodeKeyCacheKey(volumeId, new LinkId(response.Link.Id)),
                    (bytes, _) => PgpPrivateKey.Import(bytes),
                    out key))
                {
                    break;
                }

                var ancestorLink = response.Link;

                linkAncestry.Push(ancestorLink);

                currentId = ancestorLink.ParentId is not null ? new LinkId(ancestorLink.ParentId) : null;

                if (currentId is null)
                {
                    key = await Share.GetKeyAsync(client, shareId, cancellationToken).ConfigureAwait(false);
                }
            }

            while (linkAncestry.TryPop(out var ancestorLink))
            {
                var ancestorKeyPassphrase = key.Decrypt(ancestorLink.Passphrase);

                key = PgpPrivateKey.ImportAndUnlock(ancestorLink.Key, ancestorKeyPassphrase);

                client.SecretsCache.Set(GetNodeKeyCacheKey(volumeId, new LinkId(ancestorLink.Id)), key.ToBytes());
            }
        }

        return key;
    }

    private static async Task<PgpSessionKey> GetNameSessionKeyAsync(
        ProtonDriveClient client,
        NodeIdentity nodeIdentity,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetNameSessionKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId);

        if (!client.SecretsCache.TryUse(cacheKey, (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256), out var nameKey))
        {
            await GetAsync(client, nodeIdentity.ShareId, nodeIdentity.NodeId, cancellationToken).ConfigureAwait(false);

            if (!client.SecretsCache.TryUse(cacheKey, (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256), out nameKey))
            {
                throw new ProtonApiException($"Could not get name session key for {nodeIdentity.NodeId}");
            }
        }

        return nameKey;
    }

    private static async Task<PgpSessionKey> GetPassphraseSessionKeyAsync(
        ProtonDriveClient client,
        NodeIdentity nodeIdentity,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetPassphraseSessionKeyCacheKey(nodeIdentity.VolumeId, nodeIdentity.NodeId);

        if (!client.SecretsCache.TryUse(cacheKey, (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256), out var passphraseKey))
        {
            await GetAsync(client, nodeIdentity.ShareId, nodeIdentity.NodeId, cancellationToken).ConfigureAwait(false);

            if (!client.SecretsCache.TryUse(cacheKey, (token, _) => PgpSessionKey.Import(token, SymmetricCipher.Aes256), out passphraseKey))
            {
                throw new ProtonApiException($"Could not get passphrase session key for {nodeIdentity.NodeId}");
            }
        }

        return passphraseKey;
    }
}
