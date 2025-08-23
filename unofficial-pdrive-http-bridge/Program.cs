using Microsoft.Extensions.Logging;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public sealed class Program
{
    private const string APP_NAME = "macos-drive@1.0.0-alpha.1+rclone";
    private readonly ILoggerFactory _loggerFactory;

    public Program(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public static async Task<int> Main(string[] argv)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var program = new Program(loggerFactory);
        var retcode = await program.Run(argv);
        return retcode;
    }

    public async Task<int> Run(string[] argv)
    {
        var ct = CancellationToken.None;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var dataDir = Path.Join(appData, "unofficial-pdrive-http-bridge");
        var dbFile = Path.Join(dataDir, "data.db");
        Directory.CreateDirectory(dataDir);
        PersistenceManager persistenceManager = new(dbFile);
        SessionStorage sessionStorage = new(persistenceManager);

        var session = ResumeSession(persistenceManager, sessionStorage, true);
        var client = new ProtonDriveClient(session);
        var volumes = await client.GetVolumesAsync(ct);

        var mainVolume = volumes[0];
        var share = await client.GetShareAsync(mainVolume.RootShareId, ct);
        var children = client.GetFolderChildrenAsync(new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId), ct);

        await foreach (var child in children)
        {
            Console.WriteLine(child.Name);
        }

        return 0;
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
}
