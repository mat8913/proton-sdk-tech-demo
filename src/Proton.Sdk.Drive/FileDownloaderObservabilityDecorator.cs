using Proton.Sdk.Drive.Instrumentation;

namespace Proton.Sdk.Drive;

public sealed class FileDownloaderObservabilityDecorator : IFileDownloader
{
    private readonly FileDownloader _decoratedInstance;
    private readonly DownloadAttemptRetryMonitor _attemptRetryMonitor;

    internal FileDownloaderObservabilityDecorator(
        FileDownloader decoratedInstance,
        DownloadAttemptRetryMonitor attemptRetryMonitor)
    {
        _decoratedInstance = decoratedInstance;
        _attemptRetryMonitor = attemptRetryMonitor;
    }

    public async Task<VerificationStatus> DownloadAsync(
        INodeIdentity fileIdentity,
        IRevisionForTransfer revision,
        Stream contentOutputStream,
        Action<long, long> onProgress,
        CancellationToken cancellationToken,
        long startPos = 0,
        long? endPos = null)
    {
        try
        {
            var verificationStatus = await _decoratedInstance.DownloadAsync(fileIdentity, revision, contentOutputStream, onProgress, cancellationToken, startPos, endPos)
                .ConfigureAwait(false);

            _attemptRetryMonitor.IncrementSuccess(fileIdentity.VolumeId, fileIdentity.NodeId);

            return verificationStatus;
        }
        catch (ProtonApiException)
        {
            _attemptRetryMonitor.IncrementFailure(fileIdentity.VolumeId, fileIdentity.NodeId);
            throw;
        }
    }

    public async Task<VerificationStatus> DownloadAsync(
        INodeIdentity fileIdentity,
        IRevisionForTransfer? revision,
        string targetFilePath,
        Action<long, long> onProgress,
        CancellationToken cancellationToken,
        byte[]? operationId = null)
    {
        try
        {
            var verificationStatus = await _decoratedInstance.DownloadAsync(fileIdentity, revision, targetFilePath, onProgress, cancellationToken, operationId)
                .ConfigureAwait(false);

            _attemptRetryMonitor.IncrementSuccess(fileIdentity.VolumeId, fileIdentity.NodeId);

            return verificationStatus;
        }
        catch (ProtonApiException)
        {
            _attemptRetryMonitor.IncrementFailure(fileIdentity.VolumeId, fileIdentity.NodeId);
            throw;
        }
    }

    public void Dispose()
    {
        _decoratedInstance.Dispose();
    }
}
