using Microsoft.Extensions.Logging;

namespace Proton.Sdk.Drive;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly ProtonDriveClient _client;
    private volatile int _remainingNumberOfBlocksToList;

    internal FileDownloader(ProtonDriveClient client, int expectedNumberOfBlocks)
    {
        _client = client;
        _remainingNumberOfBlocksToList = expectedNumberOfBlocks;
    }

    internal ILogger Logger => _client.Logger;

    public async Task<VerificationStatus> DownloadAsync(
        INodeIdentity fileIdentity,
        IRevisionForTransfer revision,
        Stream contentOutputStream,
        Action<long, long> onProgress,
        CancellationToken cancellationToken,
        long startPos = 0,
        long? endPos = null)
    {
        using var revisionReader = await Revision.OpenForReadingAsync(_client, fileIdentity, revision, ReleaseBlockListing, cancellationToken, startPos, endPos)
            .ConfigureAwait(false);

        return await revisionReader.ReadAsync(contentOutputStream, onProgress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerificationStatus> DownloadAsync(
        INodeIdentity fileIdentity,
        IRevisionForTransfer? revision,
        string targetFilePath,
        Action<long, long> onProgress,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        IRevisionForTransfer revisionForTransfer;
        if (revision is not null)
        {
            revisionForTransfer = revision;
        }
        else
        {
            var revisions = await _client.GetFileRevisionsAsync(fileIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (!revisions.Any())
            {
                throw new ProtonApiException($"No revisions found for file {fileIdentity.NodeId.Value}");
            }

            revisionForTransfer = revisions.First();
        }

        using var revisionReader = await Revision.OpenForReadingAsync(_client, fileIdentity, revisionForTransfer, ReleaseBlockListing, cancellationToken, 0, null, operationId)
            .ConfigureAwait(false);

        FileStream fileStream;
        try
        {
            fileStream = File.Open(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        await using (fileStream.ConfigureAwait(false))
        {
            return await revisionReader.ReadAsync(fileStream, onProgress, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_remainingNumberOfBlocksToList <= 0)
        {
            return;
        }

        _client.BlockListingSemaphore.Release(_remainingNumberOfBlocksToList);
        _remainingNumberOfBlocksToList = 0;
    }

    private void ReleaseBlockListing(int numberOfBlockListings)
    {
        var newRemainingNumberOfBlocks = Interlocked.Add(ref _remainingNumberOfBlocksToList, -numberOfBlockListings);

        var amountToRelease = Math.Max(newRemainingNumberOfBlocks >= 0 ? numberOfBlockListings : newRemainingNumberOfBlocks + numberOfBlockListings, 0);

        _client.BlockListingSemaphore.Release(amountToRelease);
    }
}
