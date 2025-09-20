using System.Threading;
using System.Threading.Tasks;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public sealed class ProtonSession(
    ProtonApiSession protonApiSession,
    ProtonDriveClient protonDriveClient,
    NodeMetadataCacher nodeMetadataCacher)
{
    public ProtonApiSession ProtonApiSession { get; set; } = protonApiSession;
    public ProtonDriveClient ProtonDriveClient { get; set; } = protonDriveClient;
    public NodeMetadataCacher NodeMetadataCacher { get; set; } = nodeMetadataCacher;

    public async Task Start(CancellationToken ct)
    {
        await NodeMetadataCacher.StartAsync(ct);
    }
}
