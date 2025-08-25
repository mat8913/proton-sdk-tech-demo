using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Proton.Sdk;
using Proton.Sdk.Drive;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;

namespace unofficial_pdrive_http_bridge;

public sealed class Program
{
    private const string APP_NAME = "macos-drive@1.0.0-alpha.1+rclone";
    private readonly ILoggerFactory _loggerFactory;
    private readonly PersistenceManager _persistenceManager;
    private readonly SessionStorage _sessionStorage;
    private Session? _session;

    public Program(ILoggerFactory loggerFactory, PersistenceManager persistenceManager, SessionStorage sessionStorage)
    {
        _loggerFactory = loggerFactory;
        _persistenceManager = persistenceManager;
        _sessionStorage = sessionStorage;
    }

    public static async Task<int> Main(string[] argv)
    {
        var ct = CancellationToken.None;

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var dataDir = Path.Join(appData, "unofficial-pdrive-http-bridge");
        var dbFile = Path.Join(dataDir, "data.db");
        Directory.CreateDirectory(dataDir);
        PersistenceManager persistenceManager = new(dbFile);
        SessionStorage sessionStorage = new(persistenceManager);

        var program = new Program(loggerFactory, persistenceManager, sessionStorage);
        var retcode = await program.Run(argv, ct);
        return retcode;
    }

    public async Task<int> Run(string[] argv, CancellationToken ct)
    {
        var apiSession = ResumeSession(_persistenceManager, _sessionStorage, false);
        var client = new ProtonDriveClient(apiSession);
        _session = new(apiSession, client);

        /*
        // Q: Does share id change when descending directories
        // A: Seems not to. Different share ids can refer to the same object.
        //    Only the (volumeId, nodeId) seem to identify an object. The shareId seems more
        //    like a ticket to access the object.
        var shareIds = await _session.Value.ProtonDriveClient.GetShareIdsAsync(ct);
        foreach (var shareId in shareIds)
        {
            var share = await _session.Value.ProtonDriveClient.GetShareAsync(shareId, ct);

            Console.WriteLine($"Share: {shareId.Value}, Volume: {share.VolumeId.Value}, Node: {share.RootNodeId.Value}");
            var children = _session.Value.ProtonDriveClient.GetFolderChildrenAsync(new NodeIdentity(shareId, share.VolumeId, share.RootNodeId), ct);
            await foreach (var child in children)
            {
                Console.WriteLine(child.Name);
            }
            Console.WriteLine();
        }
        */

        WebserverSettings settings = new WebserverSettings("127.0.0.1", 9000);
        WebserverBase server = new WebserverLite(settings, OnDefaultRoute);
        server.Routes.AuthenticateRequest = OnAuthenticateRequest;

        server.Routes.PostAuthentication.Static.Add(
            HttpMethod.GET,
            "/volumes",
            OnGetVolumesRequest);

        server.Routes.PostAuthentication.Parameter.Add(
            HttpMethod.GET,
            "/volumes/{volumeId}/shares/{shareId}/node-metadata/by-id/{nodeId}",
            OnGetNodeMetadataByIdRequest);

        await server.StartAsync(ct);
        return 0;
    }

    private async Task OnDefaultRoute(HttpContextBase ctx)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.Send("Not found.");
    }

    private async Task OnGetVolumesRequest(HttpContextBase ctx)
    {
        if (!_session.HasValue)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        // TODO: Build HTML smarter
        // TODO: Add JSON support
        var volumes = await _session.Value.ProtonDriveClient.GetVolumesAsync(ctx.Token);
        ctx.Response.ContentType = "text/html";
        var builder = new StringBuilder();
        builder.Append("<table><tr><th>Id</th><th>RootShareId</th><th>State</th><th>MaxSpace</th></tr>");
        foreach (var volume in volumes)
        {
            builder.Append("<tr><td>");
            builder.Append(volume.Id.Value);
            builder.Append("</td><td>");
            builder.Append(volume.RootShareId.Value);
            builder.Append("</td><td>");
            builder.Append(volume.State);
            builder.Append("</td><td>");
            builder.Append(volume.MaxSpace);
            builder.Append("</td></tr>");
        }
        builder.Append("</table>");
        await ctx.Response.Send(builder.ToString());
    }

    private async Task OnGetNodeMetadataByIdRequest(HttpContextBase ctx)
    {
        if (!_session.HasValue)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        var volumeId = ctx.Request.Url.Parameters["volumeId"];
        var shareId = ctx.Request.Url.Parameters["shareId"] ?? throw new ArgumentNullException("shareId");
        var nodeId = ctx.Request.Url.Parameters["nodeId"] ?? throw new ArgumentNullException("nodeId");

        var node = await _session.Value.ProtonDriveClient.GetNodeAsync(new(shareId), new(nodeId), ctx.Token);

        var metadata = new NodeMetadata
        {
            Name = node.Name,
            ParentId = node.ParentId?.Value,
            State = node.State.ToString(),
        };

        if (node is FileNode fileNode)
        {
            metadata.ActiveRevisionId = fileNode.ActiveRevision?.RevisionId?.Value;
            metadata.Size = fileNode.ActiveRevision?.Size;
            metadata.Type = "File";
        }
        else if (node is FolderNode)
        {
            metadata.Type = "Folder";
        }
        else
        {
            metadata.Type = node.GetType()?.ToString();
        }

        // TODO: Should look at Accept header

        ctx.Response.ContentType = "application/json";

        await ctx.Response.Send(JsonSerializer.SerializeToUtf8Bytes(metadata));
    }

    private async Task OnAuthenticateRequest(HttpContextBase ctx)
    {
        if (ctx.Request.Authorization.Password == "password")
        {
            return;
        }
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"User Visible Realm\", charset=\"UTF-8\"";
        await ctx.Response.Send();
    }

    private ProtonApiSession ResumeSession(
        PersistenceManager persistenceManager,
        SessionStorage sessionStorage,
        bool enableSdkLog)
    {
        var hasStoredSession = sessionStorage.TryLoadSession(out var savedSession);

        var secretsCache = new SqlSecretsCache(persistenceManager);

        var options = new ProtonClientOptions
        {
            AppVersion = APP_NAME,
            SecretsCache = secretsCache,
        };
        if (enableSdkLog)
        {
            options.LoggerFactory = _loggerFactory;
        }

        var sessionResumeRequest = new SessionResumeRequest
        {
            SessionId = new() { Value = savedSession.SessionId },
            Username = savedSession.Username,
            UserId = new() { Value = savedSession.UserId },
            AccessToken = savedSession.AccessToken,
            RefreshToken = savedSession.RefreshToken,
            IsWaitingForSecondFactorCode = savedSession.IsWaitingForSecondFactorCode,
            PasswordMode = (PasswordMode)savedSession.PasswordMode,
            Options = options,
        };
        sessionResumeRequest.Scopes.AddRange(savedSession.Scopes);

        var session = ProtonApiSession.Resume(sessionResumeRequest);

        session.TokenCredential.TokensRefreshed += (accessToken, refreshToken) =>
        {
            sessionStorage.UpdateTokens(accessToken, refreshToken);
        };

        return session;
    }

    private readonly record struct Session(ProtonApiSession ProtonApiSession, ProtonDriveClient ProtonDriveClient);
}
