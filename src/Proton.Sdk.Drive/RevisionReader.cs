using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Proton.Cryptography.Pgp;
using Proton.Sdk.Drive.Files;

namespace Proton.Sdk.Drive;

public sealed class RevisionReader : IDisposable
{
    public const int BlockPageSize = 10;
    public const int MinBlockIndex = 1;
    private const int _maxParallelism = 4;

    private readonly ProtonDriveClient _client;
    private readonly INodeIdentity _fileIdentity;
    private readonly IRevisionForTransfer _revision;
    private readonly PgpPrivateKey _fileKey;
    private readonly PgpSessionKey _contentKey;
    private readonly RevisionResponse _revisionResponse;
    private readonly (int BlockNumber, int BlockIndex) _startBlockIndex;
    private readonly Action<int> _releaseBlockListingAction;

    private readonly SemaphoreSlim _blockSemaphore = new(_maxParallelism, _maxParallelism);

    internal RevisionReader(
        ProtonDriveClient client,
        INodeIdentity fileIdentity,
        IRevisionForTransfer revision,
        PgpPrivateKey fileKey,
        PgpSessionKey contentKey,
        RevisionResponse revisionResponse,
        (int BlockNumber, int BlockIndex) startBlockIndex,
        Action<int> releaseBlockListingAction)
    {
        _client = client;
        _fileIdentity = fileIdentity;
        _revision = revision;
        _fileKey = fileKey;
        _contentKey = contentKey;
        _revisionResponse = revisionResponse;
        _startBlockIndex = startBlockIndex;
        _releaseBlockListingAction = releaseBlockListingAction;
    }

    public async Task<VerificationStatus> ReadAsync(Stream contentOutputStream, Action<long, long> onProgress, CancellationToken cancellationToken)
    {
        var downloadTasks = new Queue<Task<BlockDownloadResult>>(_maxParallelism);
        var manifestStream = ProtonDriveClient.MemoryStreamManager.GetStream();

        await using (manifestStream)
        {
            foreach (var sha256Digest in _revision.SamplesSha256Digests)
            {
                manifestStream.Write(sha256Digest.Span);
            }

            try
            {
                await foreach (var (block, _) in GetBlocksAsync(_revisionResponse, cancellationToken).ConfigureAwait(false))
                {
                    if (!await _blockSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    {
                        if (downloadTasks.Count > 0)
                        {
                            await WriteNextBlockAsync(downloadTasks, contentOutputStream, manifestStream, cancellationToken).ConfigureAwait(false);
                            if (contentOutputStream.CanSeek)
                            {
                                onProgress(contentOutputStream.Position, _revisionResponse.Revision.Size);
                            }
                        }

                        await _blockSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var downloadTask = DownloadBlockAsync(block, cancellationToken);

                    downloadTasks.Enqueue(downloadTask);
                }

                while (downloadTasks.Count > 0)
                {
                    await WriteNextBlockAsync(downloadTasks, contentOutputStream, manifestStream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch when (downloadTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(downloadTasks).ConfigureAwait(false);
                }
                finally
                {
                    _blockSemaphore.Release(downloadTasks.Count);
                }

                throw;
            }

            manifestStream.Seek(0, SeekOrigin.Begin);
            return (VerificationStatus)await VerifyManifestAsync(manifestStream, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
    }

    private async Task WriteNextBlockAsync(
        Queue<Task<BlockDownloadResult>> downloadTasks,
        Stream outputStream,
        Stream manifestStream,
        CancellationToken cancellationToken)
    {
        var downloadTask = downloadTasks.Dequeue();

        try
        {
            var downloadResult = await downloadTask.ConfigureAwait(false);

            if (downloadResult.VerificationStatus is not PgpVerificationStatus.Ok)
            {
                _client.Logger.LogWarning(
                    "Verification failed for block #{Index} of file with ID \"{NodeId}\" on volume with ID \"{VolumeId}\": {VerificationStatus}",
                    downloadResult.Index,
                    _fileIdentity.NodeId,
                    _fileIdentity.VolumeId,
                    downloadResult.VerificationStatus);
            }

            manifestStream.Write(downloadResult.Sha256Digest.Span);

            var downloadedStream = downloadResult.Stream;

            await using (downloadResult.Stream.ConfigureAwait(false))
            {
                if (downloadResult.Index == MinBlockIndex + _startBlockIndex.BlockNumber)
                {
                    downloadedStream.Seek(_startBlockIndex.BlockIndex, SeekOrigin.Begin);
                }
                else
                {
                    downloadedStream.Seek(0, SeekOrigin.Begin);
                }

                await downloadedStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _blockSemaphore.Release();
        }
    }

    private async Task<BlockDownloadResult> DownloadBlockAsync(Block block, CancellationToken cancellationToken)
    {
        var blockOutputStream = ProtonDriveClient.MemoryStreamManager.GetStream();

        var signatureVerificationKeyRing = !string.IsNullOrEmpty(block.SignatureEmailAddress)
            ? new PgpKeyRing(await _client.Account.GetAddressPublicKeysAsync(block.SignatureEmailAddress, cancellationToken).ConfigureAwait(false))
            : new PgpKeyRing(_fileKey);

        var (hashDigest, verificationStatus) = await _client.BlockDownloader.DownloadAsync(
            block.Url,
            _contentKey,
            block.EncryptedSignature,
            _fileKey,
            signatureVerificationKeyRing,
            blockOutputStream,
            cancellationToken).ConfigureAwait(false);

        return new BlockDownloadResult(block.Index, blockOutputStream, hashDigest, verificationStatus);
    }

    private async IAsyncEnumerable<(Block Value, bool IsLast)> GetBlocksAsync(RevisionResponse revisionResponse, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            var mustTryNextPageOfBlocks = true;
            var nextExpectedIndex = MinBlockIndex + _startBlockIndex.BlockNumber;
            var outstandingBlock = default(Block);
            var currentPageBlocks = new List<Block>(BlockPageSize);

            while (mustTryNextPageOfBlocks)
            {
                currentPageBlocks.Clear();

                var revision = revisionResponse.Revision;

                cancellationToken.ThrowIfCancellationRequested();

                if (revision.Blocks.Count == 0)
                {
                    break;
                }

                mustTryNextPageOfBlocks = revision.Blocks.Count >= BlockPageSize;

                currentPageBlocks.AddRange(revision.Blocks);
                currentPageBlocks.Sort((a, b) => a.Index.CompareTo(b.Index));

                var blocksExceptLast = currentPageBlocks.Take(currentPageBlocks.Count - 1);
                var blocksToReturn = outstandingBlock is not null ? blocksExceptLast.Prepend(outstandingBlock) : blocksExceptLast;

                outstandingBlock = currentPageBlocks[^1];
                var lastKnownIndex = outstandingBlock.Index;

                foreach (var block in blocksToReturn)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (block.Index != nextExpectedIndex)
                    {
                        throw new ProtonApiException($"Missing block index {nextExpectedIndex}");
                    }

                    ++nextExpectedIndex;

                    yield return (block, false);
                }

                if (mustTryNextPageOfBlocks)
                {
                    revisionResponse =
                        await _client.FilesApi.GetRevisionAsync(
                            _fileIdentity.ShareId,
                            _fileIdentity.NodeId,
                            _revision.RevisionId,
                            lastKnownIndex + 1,
                            BlockPageSize,
                            false,
                            cancellationToken).ConfigureAwait(false);
                }
            }

            if (outstandingBlock is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return (outstandingBlock, true);
            }
        }
        finally
        {
            _releaseBlockListingAction.Invoke(1);
        }
    }

    private async Task<PgpVerificationStatus> VerifyManifestAsync(Stream manifestStream, CancellationToken cancellationToken)
    {
        if (_revision.ManifestSignature is null)
        {
            return PgpVerificationStatus.NotSigned;
        }

        if (string.IsNullOrEmpty(_revision.SignatureEmailAddress))
        {
            return PgpVerificationStatus.NoVerifier;
        }

        var verificationKeys = await _client.Account.GetAddressPublicKeysAsync(_revision.SignatureEmailAddress, cancellationToken).ConfigureAwait(false);

        if (verificationKeys.Count == 0)
        {
            return PgpVerificationStatus.NoVerifier;
        }

        var verificationResult = new PgpKeyRing(verificationKeys).Verify(manifestStream, _revision.ManifestSignature.Span);

        return verificationResult.Status;
    }

    private readonly struct BlockDownloadResult(
        int index,
        Stream stream,
        ReadOnlyMemory<byte> sha256Digest,
        PgpVerificationStatus verificationStatus)
    {
        public int Index { get; } = index;
        public Stream Stream { get; } = stream;
        public ReadOnlyMemory<byte> Sha256Digest { get; } = sha256Digest;
        public PgpVerificationStatus VerificationStatus { get; } = verificationStatus;
    }
}
