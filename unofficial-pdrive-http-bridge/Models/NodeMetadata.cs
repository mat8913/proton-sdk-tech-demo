namespace unofficial_pdrive_http_bridge;

public sealed class NodeMetadata
{
    public string? Name { get; set; }
    public string? ParentId { get; set; }
    public string? State { get; set; }
    public string? ActiveRevisionId { get; set; }
    public long? Size { get; set; }
    public string? Type { get; set; }
}
