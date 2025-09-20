using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace unofficial_pdrive_http_bridge;

public sealed class ProtonSessionManager(
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    PersistenceManager persistenceManager,
    SessionStorage sessionStorage)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly PersistenceManager _persistenceManager = persistenceManager;
    private readonly SessionStorage _sessionStorage = sessionStorage;

    public ProtonSession? ProtonSession { get; private set; }

    public async Task Start(CancellationToken ct)
    {
        if (ProtonSession is not null)
            return;

        var apiSession = await TryResumeSession(ct);
        if (apiSession is null)
            return;

        await SetSession(apiSession, ct);
    }

    private async Task SetSession(ProtonApiSession apiSession, CancellationToken ct)
    {
        var client = new ProtonDriveClient(apiSession);
        var nodeMetadataCacher = ActivatorUtilities.CreateInstance<NodeMetadataCacher>(_serviceProvider, client);
        var session = new ProtonSession(apiSession, client, nodeMetadataCacher);
        await session.Start(ct);
        ProtonSession = session;
    }

    private async Task<ProtonApiSession?> TryResumeSession(CancellationToken ct)
    {
        var savedSessionN = await _sessionStorage.TryLoadSession(ct);

        if (savedSessionN is null)
            return null;

        var savedSession = savedSessionN.Value;

        var secretsCache = new DbSecretsCache(_persistenceManager);

        var options = new ProtonClientOptions
        {
            AppVersion = Constants.APP_NAME,
            SecretsCache = secretsCache,
            LoggerFactory = new WarnLoggerFactory(_loggerFactory)
        };

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
            _sessionStorage.UpdateTokens(accessToken, refreshToken);
        };

        return session;
    }
}
