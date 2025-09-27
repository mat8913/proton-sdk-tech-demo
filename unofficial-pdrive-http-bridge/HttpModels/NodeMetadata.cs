using System.Text.Json.Serialization;
using HandlebarsDotNet;

namespace unofficial_pdrive_http_bridge.HttpModels;

public sealed class NodeMetadata : IHttpModel
{
    private static readonly HandlebarsTemplate<object, object> _htmlTemplate = HB.Compile("""
        <style>
            td {
                border: 1px solid;
            }
        </style>
        <table>
            <tr>
                <td>Name</td>
                <td>{{Name}}</td>
            </tr>
            <tr>
                <td>ParentId</td>
                <td>{{ParentId}}</td>
            </tr>
            <tr>
                <td>State</td>
                <td>{{State}}</td>
            </tr>
            <tr>
                <td>ActiveRevisionId</td>
                <td>{{ActiveRevisionId}}</td>
            </tr>
            <tr>
                <td>Size</td>
                <td>{{Size}}</td>
            </tr>
            <tr>
                <td>Type</td>
                <td>{{Type}}</td>
            </tr>
        </table>
    """);

    public string? NodeId { get; set; }

    public string? Name { get; set; }

    public string? ParentId { get; set; }

    public string? State { get; set; }

    public string? ActiveRevisionId { get; set; }

    public long? Size { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<NodeType>))]
    public NodeType Type { get; set; }

    public string? Url { get; set; }

    public string ToHtml() => _htmlTemplate(this);
}
