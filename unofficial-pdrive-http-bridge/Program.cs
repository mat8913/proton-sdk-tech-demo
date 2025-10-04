using System;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proton.Sdk.Drive;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;

namespace unofficial_pdrive_http_bridge;

public sealed class Program(
    ILoggerFactory loggerFactory,
    IOptions<Settings> settings,
    PersistenceManager persistenceManager,
    ProtonSessionManager protonSessionManager,
    WebUiPasswordStorage webUiPasswordStorage,
    NodeMetadataCache nodeCache)
    : IHostedService, IDisposable
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IOptions<Settings> _settings = settings;
    private readonly PersistenceManager _persistenceManager = persistenceManager;
    private readonly ProtonSessionManager _protonSessionManager = protonSessionManager;
    private readonly WebUiPasswordStorage _webUiPasswordStorage = webUiPasswordStorage;
    private readonly NodeMetadataCache _nodeCache = nodeCache;
    private WebserverLite? _webserver;
    private int _connectionCount;

    private ProtonSession? ProtonSession => _protonSessionManager.ProtonSession;

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
            .AddSingleton<NodeMetadataCache>()
            .AddSingleton<ProtonSessionManager>()
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

        if (_settings.Value.ResetCache)
        {
            await _nodeCache.Reset(ct);
        }

        await _protonSessionManager.Start(ct);

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
            "/login",
            ToHandler(OnGetLoginRequest));

        _webserver.Routes.PostAuthentication.Static.Add(
            HttpMethod.POST,
            "/login",
            ToHandler(OnPostLoginRequest));

        _webserver.Routes.PostAuthentication.Dynamic.Add(
            HttpMethod.GET,
            new Regex(@"^\/files(\/.*)?$"),
            ToHandler(OnGetNodeContentByPathRequest));

        _webserver.Routes.PostAuthentication.Dynamic.Add(
            HttpMethod.HEAD,
            new Regex(@"^\/files(\/.*)?$"),
            ToHandler(OnGetNodeContentByPathRequest));

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

    private async Task<HttpModels.LoginForm?> OnGetLoginRequest(HttpContextBase ctx)
    {
        return new HttpModels.LoginForm();
    }

    private async Task<HttpModels.LoginResult?> OnPostLoginRequest(HttpContextBase ctx)
    {
        var qs = HttpUtility.ParseQueryString(ctx.Request.DataAsString);
        var username = qs["username"] ?? "";
        var password = qs["password"] ?? "";
        var otp = qs["otp"] ?? "";

        try
        {
            await _protonSessionManager.Login(username, password, otp, ctx.Token);
        }
        catch (Exception ex)
        {
            return new HttpModels.LoginResult
            {
                Message = $"There was an error logging in.",
                Error = $"{ex.Message}\n{ex.StackTrace}",
            };
        }

        return new HttpModels.LoginResult
        {
            Message = "Logged in.",
        };
    }

    private async Task<HttpModels.NodeChildren?> OnGetNodeContentByPathRequest(HttpContextBase ctx)
    {
        if (ProtonSession is null)
        {
            // TODO: Redirect to login
            throw new InvalidOperationException("Session not initialized");
        }

        // TODO: Decode manually to allow '+' to remain instead of being decoded as ' '.
        var path = ctx.Request.Url.Elements.Skip(1);
        var rootNodeIdentity = ProtonSession.RootNodeIdentity;
        var nodeMetadata = await ProtonSession.GetNodeMetadataByPathAsync(path, rootNodeIdentity, ctx.Token);

        if (nodeMetadata is null || nodeMetadata.IsFile && ctx.Request.Url.Full.EndsWith('/'))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.Send("Not found");
            return null;
        }

        if (!nodeMetadata.IsFile && !ctx.Request.Url.Full.EndsWith('/'))
        {
            var redir = ctx.Request.Url.Full + '/';
            ctx.Response.StatusCode = 302;
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Location"] = redir;
            await ctx.Response.Send(redir);
            return null;
        }

        if (nodeMetadata.IsFile)
        {
            await ServeFile(ctx, nodeMetadata, new(rootNodeIdentity.ShareId));
            return null;
        }

        var children = (await ProtonSession.NodeMetadataCacher
            .GetChildren(nodeMetadata.VolumeId, nodeMetadata.NodeId, rootNodeIdentity.ShareId.Value, ctx.Token))
            .Select(Converters.DbModelNodeMetadataToHttpModel);

        if (path.Any())
        {
            var parentNode = new HttpModels.NodeMetadata
            {
                Name = "(Parent Directory)",
                Type = HttpModels.NodeType.Folder,
                Url = "../",
            };
            children = children.Prepend(parentNode);
        }

        return new()
        {
            Path = '/' + string.Join('/', path),
            Children = children.ToArray(),
        };
    }

    private async Task ServeFile(HttpContextBase ctx, DbModels.NodeMetadata nodeMetadata, ShareId shareId)
    {
        var nodeIdentity = new NodeIdentity(shareId, new(nodeMetadata.VolumeId), new(nodeMetadata.NodeId));

        long totalSize = nodeMetadata.Size!.Value;
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
        ctx.Response.ContentType = nodeMetadata.MediaType ?? "application/octet-stream";

        if (nodeMetadata.ModificationTime.HasValue)
        {
            ctx.Response.Headers["Last-Modified"] = DateTimeOffset
                .FromUnixTimeSeconds(nodeMetadata.ModificationTime.Value)
                .UtcDateTime
                .ToString("r", CultureInfo.InvariantCulture);
        }

        if (ctx.Request.Method == HttpMethod.HEAD)
        {
            ctx.Response.ContentLength = contentSize;
            await ctx.Response.Send();
            return;
        }

        using var downloader = await ProtonSession!.ProtonDriveClient.WaitForFileDownloaderAsync(ctx.Token);
        var pipe = new Pipe(new(
            pauseWriterThreshold: RevisionWriter.DefaultBlockSize * 2,
            resumeWriterThreshold: RevisionWriter.DefaultBlockSize));
        await using var writerStream = pipe.Writer.AsStream();
        await using var readerStream = pipe.Reader.AsStream();

        var revision = await ProtonSession.ProtonDriveClient.GetFileRevisionAsync(nodeIdentity, new(nodeMetadata.ActiveRevisionId!), ctx.Token);
        var downloadTask = Task.Run(() => downloader.DownloadAsync(nodeIdentity, revision, writerStream, (_, _) => { }, ctx.Token, startPos));
        var senderTask = ctx.Response.Send(contentSize, readerStream);
        await foreach (var t in Task.WhenEach(downloadTask, senderTask))
        {
            await t;
        }
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
                    ctx.Response.ContentType = "text/html";
                    await ctx.Response.Send(model.ToHtml());
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
}
