using HandlebarsDotNet;

namespace unofficial_pdrive_http_bridge.HttpModels;

public sealed class NodeChildren : IHttpModel
{
    private static readonly HandlebarsTemplate<object, object> _htmlTemplate = HB.Compile("""
        <style>
            td, th {
                border: 1px solid;
            }
        </style>
        <p>Children of {{NodeId}}:</p>
        <table>
            <tr>
                <th>Type</th>
                <th>Name</th>
                <th>Size</th>
            </tr>
        {{#each Children}}
            <tr>
                <td>{{this.Type}}</td>
                <td><a href="{{this.Url}}">{{this.Name}}</a></td>
                <td>{{this.Size}}</td>
            </tr>
        {{/each}}
        </table>
    """);

    public string? VolumeId { get; set; }
    public string? ShareId { get; set; }
    public string? NodeId { get; set; }
    public NodeMetadata[]? Children { get; set; }

    public string ToHtml() => _htmlTemplate(this);
}
