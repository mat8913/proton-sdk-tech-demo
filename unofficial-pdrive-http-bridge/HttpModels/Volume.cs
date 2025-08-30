namespace unofficial_pdrive_http_bridge.HttpModels;

public sealed class Volume
{
    public string? Id { get; set; }
    public string? RootShareId { get; set; }
    public string? State { get; set; }
    public long? MaxSpace { get; set; }
}
