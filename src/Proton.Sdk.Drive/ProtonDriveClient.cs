using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Proton.Sdk.Cryptography;
using Proton.Sdk.Drive.Devices;
using Proton.Sdk.Drive.Files;
using Proton.Sdk.Drive.Folders;
using Proton.Sdk.Drive.Instrumentation;
using Proton.Sdk.Drive.Links;
using Proton.Sdk.Drive.Shares;
using Proton.Sdk.Drive.Storage;
using Proton.Sdk.Drive.Verification;
using Proton.Sdk.Drive.Volumes;

namespace Proton.Sdk.Drive;

public sealed class ProtonDriveClient
{
    private readonly HttpClient _defaultHttpClient;
    private readonly HttpClient _storageHttpClient;
    private readonly UploadAttemptRetryMonitor? _uploadAttemptRetryMonitor;
    private readonly DownloadAttemptRetryMonitor? _downloadAttemptRetryMonitor;

    /// <summary>
    /// Creates a new instance of <see cref="ProtonDriveClient"/>.
    /// </summary>
    /// <param name="session">Authentication session.</param>
    /// <param name="options">Specifies options for <see cref="ProtonDriveClient" /></param>
    public ProtonDriveClient(ProtonApiSession session, in ProtonDriveClientOptions options = default)
    {
        _defaultHttpClient = session.GetHttpClient(ProtonDriveDefaults.DriveBaseRoute, TimeSpan.FromSeconds(15));
        _storageHttpClient = session.GetHttpClient(ProtonDriveDefaults.DriveBaseRoute, TimeSpan.FromMinutes(15));

        if (options.InstrumentationMeter is not null)
        {
            _uploadAttemptRetryMonitor = new UploadAttemptRetryMonitor(options.InstrumentationMeter);
            _downloadAttemptRetryMonitor = new DownloadAttemptRetryMonitor(options.InstrumentationMeter);
        }

        ClientId = options.ClientId ?? Guid.NewGuid().ToString();

        Account = new ProtonAccountClient(session);
        SecretsCache = session.SecretsCache;

        Logger = session.LoggerFactory.CreateLogger<ProtonDriveClient>();

        var maxDegreeOfBlockTransferParallelism = Math.Max(Math.Min(Environment.ProcessorCount / 2, 8), 2);
        var maxDegreeOfBlockProcessingParallelism = maxDegreeOfBlockTransferParallelism + Math.Min(Math.Max(maxDegreeOfBlockTransferParallelism / 2, 2), 4);

        Logger.LogDebug($"ProtonDriveClient initialization: {nameof(maxDegreeOfBlockProcessingParallelism)} {{MaxDegreeOfBlockProcessingParallelism}}", maxDegreeOfBlockProcessingParallelism);
        Logger.LogDebug($"ProtonDriveClient initialization: {nameof(maxDegreeOfBlockTransferParallelism)} {{MaxDegreeOfBlockTransferParallelism}}", maxDegreeOfBlockTransferParallelism);

        BlockListingSemaphore = new FifoFlexibleSemaphore(maxDegreeOfBlockProcessingParallelism, Logger);
        RevisionCreationSemaphore = new FifoFlexibleSemaphore(maxDegreeOfBlockProcessingParallelism, Logger);
        BlockUploader = new BlockUploader(this, maxDegreeOfBlockTransferParallelism);
        BlockDownloader = new BlockDownloader(this, maxDegreeOfBlockTransferParallelism);
    }

    public string ClientId { get; }

    internal static RecyclableMemoryStreamManager MemoryStreamManager { get; } = new();

    internal ProtonAccountClient Account { get; }
    internal ISecretsCache SecretsCache { get; }

    internal VolumesApiClient VolumesApi => new(_defaultHttpClient);
    internal DevicesApiClient DevicesApi => new(_defaultHttpClient);
    internal SharesApiClient SharesApi => new(_defaultHttpClient);
    internal LinksApiClient LinksApi => new(_defaultHttpClient);
    internal FoldersApiClient FoldersApi => new(_defaultHttpClient);
    internal FilesApiClient FilesApi => new(_defaultHttpClient);
    internal StorageApiClient StorageApi => new(_storageHttpClient);
    internal RevisionVerificationApiClient RevisionVerificationApi => new(_defaultHttpClient);

    internal BlockUploader BlockUploader { get; }
    internal BlockDownloader BlockDownloader { get; }
    internal FifoFlexibleSemaphore BlockListingSemaphore { get; }
    internal FifoFlexibleSemaphore RevisionCreationSemaphore { get; }
    internal ILogger<ProtonDriveClient> Logger { get; }

    public Task<Volume[]> GetVolumesAsync(CancellationToken cancellationToken)
    {
        return Volume.GetAllAsync(VolumesApi, cancellationToken);
    }

    public async Task<Volume> CreateVolumeAsync(CancellationToken cancellationToken)
    {
        return await Volume.CreateAsync(this, cancellationToken).ConfigureAwait(false);
    }

    public Task<Share> GetShareAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        return Share.GetAsync(this, shareId, cancellationToken);
    }

    public Task<ShareId[]> GetShareIdsAsync(CancellationToken cancellationToken)
    {
        return Share.GetShareIdsAsync(this, cancellationToken);
    }

    public Task DeleteFromTrashAsync(ShareId shareId, IEnumerable<LinkId> nodeIds, CancellationToken cancellationToken)
    {
        return Share.DeleteFromTrashAsync(SharesApi, shareId, nodeIds, cancellationToken);
    }

    public Task<INode> GetNodeAsync(ShareId shareId, LinkId nodeId, CancellationToken cancellationToken)
    {
        return Node.GetAsync(this, shareId, nodeId, cancellationToken);
    }

    public IAsyncEnumerable<INode> GetFolderChildrenAsync(INodeIdentity folderIdentity, CancellationToken cancellationToken, bool includeHidden = false)
    {
        return FolderNode.GetFolderChildrenAsync(this, folderIdentity, includeHidden, cancellationToken);
    }

    public async Task<IFileUploader> WaitForFileUploaderAsync(long size, int numberOfSamples, CancellationToken cancellationToken)
    {
        var expectedNumberOfBlocks = (int)size.DivideAndRoundUp(RevisionWriter.DefaultBlockSize) + numberOfSamples;

        await RevisionCreationSemaphore.EnterAsync(expectedNumberOfBlocks, cancellationToken).ConfigureAwait(false);

        var fileUploader = new FileUploader(this, expectedNumberOfBlocks);

        return _uploadAttemptRetryMonitor is not null
            ? new FileUploaderObservabilityDecorator(fileUploader, _uploadAttemptRetryMonitor)
            : fileUploader;
    }

    public async Task<IFileDownloader> WaitForFileDownloaderAsync(CancellationToken cancellationToken)
    {
        await BlockListingSemaphore.EnterAsync(1, cancellationToken).ConfigureAwait(false);

        var fileDownloader = new FileDownloader(this, expectedNumberOfBlocks: 1);

        return _downloadAttemptRetryMonitor is not null
            ? new FileDownloaderObservabilityDecorator(fileDownloader, _downloadAttemptRetryMonitor)
            : fileDownloader;
    }

    public Task<Revision[]> GetFileRevisionsAsync(INodeIdentity fileIdentity, CancellationToken cancellationToken)
    {
        return FileNode.GetFileRevisionsAsync(this, fileIdentity, cancellationToken);
    }

    public Task<Revision> GetFileRevisionAsync(NodeIdentity nodeIdentity, RevisionId revisionId, CancellationToken cancellationToken)
    {
        return FileNode.GetFileRevisionAsync(this, nodeIdentity, revisionId, cancellationToken);
    }

    public Task DeleteRevisionAsync(ShareBasedRevisionIdentity shareBasedRevisionIdentity, CancellationToken cancellationToken)
    {
        return Revision.DeleteAsync(FilesApi, shareBasedRevisionIdentity, cancellationToken);
    }

    public Task<FolderNode> CreateFolderAsync(IShareForCommand share, INodeIdentity parentFolder, string name, CancellationToken cancellationToken)
    {
        return FolderNode.CreateFolderAsync(this, share, parentFolder, name, cancellationToken);
    }

    public Task TrashNodesAsync(INodeIdentity folderIdentity, IEnumerable<LinkId> nodeIds, CancellationToken cancellationToken)
    {
        return FolderNode.TrashFolderChildrenAsync(FoldersApi, folderIdentity, nodeIds, cancellationToken);
    }

    public Task DeleteNodesAsync(INodeIdentity folderIdentity, IEnumerable<LinkId> nodeIds, CancellationToken cancellationToken)
    {
        return FolderNode.DeleteFolderChildrenAsync(FoldersApi, folderIdentity, nodeIds, cancellationToken);
    }

    public Task MoveNodeAsync(
        ShareId shareId,
        INode node,
        IShareForCommand destinationShare,
        INodeIdentity destinationFolderIdentity,
        string nameAtDestination,
        CancellationToken cancellationToken)
    {
        // TODO: Assert parentFolderID is present?
        return Node.MoveAsync(
            this,
            shareId,
            node,
            destinationShare,
            destinationFolderIdentity,
            nameAtDestination,
            cancellationToken);
    }

    public Task RenameNodeAsync(
        IShareForCommand share,
        INode node,
        string newName,
        string newMediaType,
        CancellationToken cancellationToken)
    {
        return Node.RenameAsync(this, share, node, /*parentFolderId,*/ newName, newMediaType, cancellationToken);
    }
}
