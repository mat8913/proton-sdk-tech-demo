using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proton.Sdk;
using Proton.Sdk.Drive;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;

namespace unofficial_pdrive_http_bridge;

public sealed class Program : IHostedService, IDisposable
{
    private const string APP_NAME = "macos-drive@1.0.0-alpha.1+rclone";
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<Settings> _settings;
    private readonly PersistenceManager _persistenceManager;
    private readonly SessionStorage _sessionStorage;
    private readonly WebUiPasswordStorage _webUiPasswordStorage;
    private WebserverLite? _webserver;
    private Session? _session;
    private int _connectionCount;

    public Program(ILoggerFactory loggerFactory, IOptions<Settings> settings, PersistenceManager persistenceManager, SessionStorage sessionStorage, WebUiPasswordStorage webUiPasswordStorage)
    {
        _loggerFactory = loggerFactory;
        _settings = settings;
        _persistenceManager = persistenceManager;
        _sessionStorage = sessionStorage;
        _webUiPasswordStorage = webUiPasswordStorage;
    }

    public static async Task Main(string[] argv)
    {
        var hostSettings = new HostApplicationBuilderSettings
        {
            Args = argv,
        };

        var hostBuilder = Host.CreateApplicationBuilder(hostSettings);

        // Configuration
        hostBuilder.Configuration.Sources.RemoveAll(x =>
            x is EnvironmentVariablesConfigurationSource envSource && envSource.Prefix is null);
        hostBuilder.Configuration.AddEnvironmentVariables("PDRIVE_");

        // Logging
        hostBuilder.Logging
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
            .AddConsole();

        // PersistenceManager
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var dataDir = Path.Join(appData, "unofficial-pdrive-http-bridge");
        var dbFile = Path.Join(dataDir, "data.db");
        Directory.CreateDirectory(dataDir);
        hostBuilder.Services
            .AddSingleton<PersistenceManager>(s => new(s.GetRequiredService<ILoggerFactory>(), dbFile));

        // IOptions<Settings>
        hostBuilder.Services
            .AddOptions()
            .AddSingleton<IOptionsChangeTokenSource<Settings>>(s => new ConfigurationChangeTokenSource<Settings>(null, s.GetRequiredService<IConfiguration>()))
            .AddSingleton<IConfigureOptions<Settings>>(s => new NamedConfigureFromConfigurationOptions<Settings>(null, s.GetRequiredService<IConfiguration>(), null));

        hostBuilder.Services
            .AddSingleton<SessionStorage>()
            .AddSingleton<WebUiPasswordStorage>()
            .AddHostedService<Program>();

        using var host = hostBuilder.Build();

        var settings = host.Services.GetRequiredService<IOptions<Settings>>();
        var config = host.Services.GetRequiredService<IConfiguration>();

        await host.RunAsync();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using (var db = _persistenceManager.GetProgramDbContext())
        {
            await db.Database.MigrateAsync(ct);
            await db.SaveChangesAsync(ct);
        }

        await EnsurePassword(ct);

        var apiSession = await ResumeSession(_persistenceManager, _sessionStorage, true, ct);
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

        var webserverLogger = _loggerFactory.CreateLogger<WebserverLite>();
        WebserverSettings settings = new WebserverSettings(_settings.Value.Hostname ?? "127.0.0.1", _settings.Value.Port ?? 9000);
        settings.Debug.Requests = true;
        settings.Debug.Responses = true;
        _webserver = new WebserverLite(settings, OnDefaultRoute);
        _webserver.Events.Logger = msg => webserverLogger.LogInformation("{msg}", msg);
        _webserver.Events.ExceptionEncountered += (_, ex) =>
            webserverLogger.LogError(ex.Exception, "Exception handling {url}: {ex}", ex.Url, ex.Exception);

        _webserver.Routes.AuthenticateRequest = OnAuthenticateRequest;

        _webserver.Routes.PostAuthentication.Static.Add(
            HttpMethod.GET,
            "/volumes",
            ToHandler(OnGetVolumesRequest));

        _webserver.Routes.PostAuthentication.Parameter.Add(
            HttpMethod.GET,
            "/volumes/{volumeId}/shares/{shareId}/node-metadata/by-id/{nodeId}",
            ToHandler(OnGetNodeMetadataByIdRequest));

        _webserver.Routes.PostAuthentication.Parameter.Add(
            HttpMethod.GET,
            "/volumes/{volumeId}/shares/{shareId}/node-content/by-id/{nodeId}",
            ToHandler(OnGetNodeContentByIdRequest));

        _webserver.Start(ct);
        Console.WriteLine($"Server started on {settings.Hostname}:{settings.Port}");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _webserver?.Stop();
        Console.WriteLine("Server stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _webserver?.Dispose();
    }

    private async Task OnDefaultRoute(HttpContextBase ctx)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.Send("Not found.");
    }

    private async Task<HttpModels.VolumeList?> OnGetVolumesRequest(HttpContextBase ctx)
    {
        if (!_session.HasValue)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        var volumes = await _session.Value.ProtonDriveClient.GetVolumesAsync(ctx.Token);
        var modelVolumes = volumes
            .Select(volume => new HttpModels.Volume
            {
                Id = volume.Id.Value,
                RootShareId = volume.RootShareId.Value,
                State = volume.State.ToString(),
                MaxSpace = volume.MaxSpace,
            })
            .ToArray();
        var modelVolumeList = new HttpModels.VolumeList
        {
            Volumes = modelVolumes,
        };

        return modelVolumeList;
    }

    private async Task<HttpModels.NodeMetadata?> OnGetNodeMetadataByIdRequest(HttpContextBase ctx)
    {
        if (!_session.HasValue)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        var volumeId = ctx.Request.Url.Parameters["volumeId"] ?? throw new ArgumentNullException("volumeId");
        var shareId = ctx.Request.Url.Parameters["shareId"] ?? throw new ArgumentNullException("shareId");
        var nodeId = ctx.Request.Url.Parameters["nodeId"] ?? throw new ArgumentNullException("nodeId");

        var node = await _session.Value.ProtonDriveClient.GetNodeAsync(new(shareId), new(nodeId), ctx.Token);

        var metadata = GetNodeMetadata(volumeId, shareId, node);

        return metadata;
    }

    private static HttpModels.NodeMetadata GetNodeMetadata(string volumeId, string shareId, INode node)
    {
        var metadata = new HttpModels.NodeMetadata
        {
            NodeId = node.NodeIdentity.NodeId.Value,
            Name = node.Name,
            ParentId = node.ParentId?.Value,
            State = node.State.ToString(),
            Url = $"/volumes/{volumeId}/shares/{shareId}/node-content/by-id/{node.NodeIdentity.NodeId.Value}",
        };

        if (node is FileNode fileNode)
        {
            metadata.ActiveRevisionId = fileNode.ActiveRevision?.RevisionId?.Value;
            metadata.Size = fileNode.ActiveRevision?.Size;
            metadata.Type = HttpModels.NodeType.File;
        }
        else if (node is FolderNode)
        {
            metadata.Type = HttpModels.NodeType.Folder;
        }
        else
        {
            throw new InvalidOperationException($"Unknown node type: {node.GetType()}");
        }

        return metadata;
    }

    private async Task<HttpModels.NodeChildren?> OnGetNodeContentByIdRequest(HttpContextBase ctx)
    {
        if (!_session.HasValue)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        var volumeId = ctx.Request.Url.Parameters["volumeId"] ?? throw new ArgumentNullException("volumeId");
        var shareId = ctx.Request.Url.Parameters["shareId"] ?? throw new ArgumentNullException("shareId");
        var nodeId = ctx.Request.Url.Parameters["nodeId"] ?? throw new ArgumentNullException("nodeId");

        var node = await _session.Value.ProtonDriveClient.GetNodeAsync(new(shareId), new(nodeId), ctx.Token);
        var nodeIdentity = new NodeIdentity(new(shareId), new(volumeId), new(nodeId));

        if (node is FileNode fileNode)
        {
            using var downloader = await _session.Value.ProtonDriveClient.WaitForFileDownloaderAsync(ctx.Token);
            var pipe = new Pipe(new(
                pauseWriterThreshold: RevisionWriter.DefaultBlockSize * 2,
                resumeWriterThreshold: RevisionWriter.DefaultBlockSize));
            await using var writerStream = pipe.Writer.AsStream();
            await using var readerStream = pipe.Reader.AsStream();

            long totalSize = fileNode.ActiveRevision.Size;
            long startPos = 0;
            if (RangeHeaderValue.TryParse(ctx.Request.Headers["Range"], out var range))
            {
                if (range.Ranges.Count > 1)
                {
                    throw new NotImplementedException("multi range not implemented");
                }
                startPos = range.Ranges.FirstOrDefault()?.From ?? startPos;
            }
            long endPos = long.Max(totalSize - 1, 0);
            long contentSize = long.Max(totalSize - startPos, 0);

            if (startPos == 0)
            {
                ctx.Response.StatusCode = 200;
            }
            else
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {startPos}-{endPos}/{totalSize}";
            }
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.ContentType = fileNode.MediaType ?? "application/octet-stream";
            var downloadTask = Task.Run(() => downloader.DownloadAsync(nodeIdentity, fileNode.ActiveRevision, writerStream, (_, _) => { }, ctx.Token, startPos));
            var senderTask = ctx.Response.Send(contentSize, readerStream);
            await foreach (var t in Task.WhenEach(downloadTask, senderTask))
            {
                await t;
            }

            return null;
        }

        var children = _session.Value.ProtonDriveClient
            .GetFolderChildrenAsync(nodeIdentity, ctx.Token)
            .Select(child => GetNodeMetadata(volumeId, shareId, child));

        if (node.ParentId is not null)
        {
            var parentNode = new FolderNode
            {
                NodeIdentity = new()
                {
                    NodeId = node.ParentId,
                },
                Name = "(Parent Directory)",
                State = NodeState.Active,
            };
            children = children.Prepend(GetNodeMetadata(volumeId, shareId, parentNode));
        }

        return new()
        {
            VolumeId = volumeId,
            ShareId = shareId,
            NodeId = nodeId,
            Children = await children.ToArrayAsync(ctx.Token),
        };
    }

    private async Task OnAuthenticateRequest(HttpContextBase ctx)
    {
        // Set response timeout
        var stream = Utils.GetResponseStream(ctx.Response);
        stream.WriteTimeout = 5000;

        var (_, expectedPassword) = await _webUiPasswordStorage.GetPassword(ctx.Token);
        var expectedPasswordBytes = Encoding.UTF8.GetBytes(expectedPassword);

        var gotPassword = ctx.Request.Authorization.Password ?? "";
        var gotPasswordBytes = Encoding.UTF8.GetBytes(gotPassword);

        if (!CryptographicOperations.FixedTimeEquals(expectedPasswordBytes, gotPasswordBytes))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"User Visible Realm\", charset=\"UTF-8\"";
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.Send("Incorrect password");
        }
    }

    private Func<HttpContextBase, Task> ToHandler<T>(Func<HttpContextBase, Task<T?>> func)
        where T : class, IHttpModel
    {
        return async ctx =>
        {
            var numConnections = Interlocked.Increment(ref _connectionCount);
            Console.WriteLine($"Connections: {numConnections}");
            try
            {
                var model = await func(ctx);
                if (model is not null)
                {
                    // TODO: match wildcards
                    var accepts = ctx.Request.Headers["Accept"]?.Split(',')
                        .Select(MediaTypeWithQualityHeaderValue.Parse)
                        .OrderByDescending(mt => mt.Quality.GetValueOrDefault(1))
                        .FirstOrDefault(mt => mt.MediaType == "application/json" || mt.MediaType == "text/html");

                    if (accepts is null || accepts.MediaType == "application/json")
                    {
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.Send(model.ToJson());
                    }
                    else
                    {
                        ctx.Response.ContentType = "text/html";
                        await ctx.Response.Send(model.ToHtml());
                    }
                }
            }
            finally
            {
                numConnections = Interlocked.Decrement(ref _connectionCount);
                Console.WriteLine($"Connections: {numConnections}");
            }
        };
    }

    private async Task EnsurePassword(CancellationToken ct)
    {
        bool exists;
        string password;

        if (_settings.Value.ResetPassword)
        {
            exists = false;
            password = await _webUiPasswordStorage.ResetPassword(ct);
        }
        else
        {
            (exists, password) = await _webUiPasswordStorage.GetPassword(ct);
        }

        if (!exists)
        {
            Console.Error.WriteLine("Web UI Password: {0}", password);
        }
    }

    private async Task<ProtonApiSession> ResumeSession(
        PersistenceManager persistenceManager,
        SessionStorage sessionStorage,
        bool enableSdkLog,
        CancellationToken ct)
    {
        var savedSession = await sessionStorage.TryLoadSession(ct) ?? throw new InvalidOperationException("no saved session");

        var secretsCache = new DbSecretsCache(persistenceManager);

        var options = new ProtonClientOptions
        {
            AppVersion = APP_NAME,
            SecretsCache = secretsCache,
        };
        if (enableSdkLog)
        {
            options.LoggerFactory = new WarnLoggerFactory(_loggerFactory);
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
