using System.Text.Json;
using HandlebarsDotNet;

namespace unofficial_pdrive_http_bridge.HttpModels;

public sealed class VolumeList : IHttpModel
{
    private static readonly HandlebarsTemplate<object, object> _htmlTemplate = HB.Compile("""
        <style>
            td, th {
                border: 1px solid;
            }
        </style>
        <table>
            <tr>
                <th>Id</th>
                <th>RootShareId</th>
                <th>State</th>
                <th>MaxSpace</th>
            </tr>
        {{#each Volumes}}
            <tr>
                <td>{{this.Id}}</td>
                <td>{{this.RootShareId}}</td>
                <td>{{this.State}}</td>
                <td>{{this.MaxSpace}}</td>
            </tr>
        {{/each}}
        </table>
    """);

    public Volume[]? Volumes { get; set; }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this);

    public string ToHtml() => _htmlTemplate(this);
}
