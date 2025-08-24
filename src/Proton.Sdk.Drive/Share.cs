using System.Buffers.Text;
using System.Security.Cryptography;
using Proton.Cryptography.Pgp;
using Proton.Sdk.Cryptography;
using Proton.Sdk.Drive.Links;
using Proton.Sdk.Drive.Shares;

namespace Proton.Sdk.Drive;

public sealed partial class Share : IShare
{
    // Formula taken from Base64.GetMaxEncodedToUtf8Length()
    internal const int PassphraseMaxUtf8Length = ((PassphraseRandomBytesLength + 2) / 3) * 4;

    private const int PassphraseRandomBytesLength = 32;
    private const string CacheValueHolderName = "drive.share";
    private const string CacheShareKeyValueName = "key";

    public ShareMetadata Metadata()
    {
        return new ShareMetadata
        {
            ShareId = ShareId,
            MembershipEmailAddress = MembershipEmailAddress,
            MembershipAddressId = MembershipAddressId,
        };
    }

    internal static async Task<Share> GetAsync(ProtonDriveClient client, ShareId shareId, CancellationToken cancellationToken)
    {
        var fetchedShare = await client.SharesApi.GetShareAsync(shareId, cancellationToken).ConfigureAwait(false);

        var addressId = new AddressId(fetchedShare.AddressId);
        var addressKeys = await client.Account.GetAddressKeysAsync(addressId, cancellationToken).ConfigureAwait(false);

        var passphrase = new PgpPrivateKeyRing(addressKeys).Decrypt(fetchedShare.Passphrase);

        var key = PgpPrivateKey.ImportAndUnlock(fetchedShare.Key, passphrase);
        client.SecretsCache.Set(GetShareKeyCacheKey(shareId), key.ToBytes());

        var fetchedAddress = await client.Account.GetAddressAsync(addressId, cancellationToken).ConfigureAwait(false);

        return new Share
        {
            ShareId = shareId,
            VolumeId = new VolumeId(fetchedShare.VolumeId),
            RootNodeId = new LinkId(fetchedShare.RootLinkId),
            MembershipAddressId = fetchedAddress.Id,
            MembershipEmailAddress = fetchedAddress.EmailAddress,
        };
    }

    internal static async Task<ShareId[]> GetShareIdsAsync(ProtonDriveClient client, CancellationToken cancellationToken)
    {
        var fetchedShares = await client.SharesApi.GetSharesAsync(cancellationToken);

        return fetchedShares.Shares.Select(x => new ShareId(x.Id)).ToArray();
    }

    internal static async Task DeleteFromTrashAsync(SharesApiClient client, ShareId shareId, IEnumerable<LinkId> nodeIds, CancellationToken cancellationToken)
    {
        var parameters = new MultipleLinkActionParameters { LinkIds = nodeIds.Select(x => x.Value) };

        await client.DeleteFromTrashAsync(shareId, parameters, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PgpPrivateKey> GetKeyAsync(ProtonDriveClient client, ShareId id, CancellationToken cancellationToken)
    {
        var cacheKey = GetShareKeyCacheKey(id);

        if (!client.SecretsCache.TryUse(cacheKey, (bytes, _) => PgpPrivateKey.Import(bytes), out var key))
        {
            await GetAsync(client, id, cancellationToken).ConfigureAwait(false);

            if (!client.SecretsCache.TryUse(cacheKey, (bytes, _) => PgpPrivateKey.Import(bytes), out key))
            {
                throw new ProtonApiException($"Could not get passphrase session key for {id}");
            }
        }

        return key;
    }

    internal static CacheKey GetShareKeyCacheKey(ShareId shareId) => new(CacheValueHolderName, shareId.Value, CacheShareKeyValueName);

    internal static ReadOnlySpan<byte> GeneratePassphrase(Span<byte> destination)
    {
        var randomBytes = destination[..PassphraseRandomBytesLength];
        RandomNumberGenerator.Fill(randomBytes);
        Base64.EncodeToUtf8InPlace(destination, PassphraseRandomBytesLength, out var length);
        return destination[..length];
    }
}
