using System.Text.Json;
using System.Text.Json.Serialization;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var configured = builder.Configuration.GetSection("RaceServer").Get<RaceServerOptions>() ?? new RaceServerOptions();
var serverOptions = configured.Normalize();
var configurationStore = new RaceServerConfigurationStore(serverOptions);
var initialRoom = configurationStore.InitialRoomSettings;
serverOptions = serverOptions with
{
    SessionName = initialRoom.SessionName,
    TotalRaceLaps = initialRoom.TotalRaceLaps,
    MinimumRequiredPitStops = initialRoom.MinimumRequiredPitStops,
    SectorCount = initialRoom.SectorCount,
    AutomaticYellowEnabled = initialRoom.AutomaticYellowEnabled,
    AutomaticCollisionInvestigationsEnabled = initialRoom.AutomaticCollisionInvestigationsEnabled,
    DisconnectedLapRecoveryEnabled = initialRoom.DisconnectedLapRecoveryEnabled,
    SlowSpeedKph = initialRoom.SlowSpeedKph,
    SlowDurationSeconds = initialRoom.SlowDurationSeconds,
    SevereLateralOffsetMeters = initialRoom.SevereLateralOffsetMeters,
    RecoveryDurationSeconds = initialRoom.RecoveryDurationSeconds,
    TrackLimitMode = initialRoom.TrackLimitMode,
    AllowTeams = initialRoom.AllowTeams,
    TeamCount = initialRoom.TeamCount,
    DriversPerTeam = initialRoom.DriversPerTeam,
    Teams = initialRoom.Teams ?? [],
    TrackName = initialRoom.TrackName,
    TrackId = initialRoom.TrackId,
    TrackRevision = initialRoom.TrackRevision,
    TrackPackageHash = initialRoom.TrackPackageHash
};
builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton(configurationStore);
builder.Services.AddSingleton<IRaceStatePersistence, FileRaceStatePersistence>();
builder.Services.AddSingleton(serviceProvider => new RaceCoordinator(
    serverOptions,
    serviceProvider.GetRequiredService<IRaceStatePersistence>(),
    configurationStore.PlayerPasswordMatches));
builder.Services.AddSingleton<RaceWebSocketRegistry>();
builder.Services.AddSingleton<HostedTrackPackageStore>();
builder.Services.AddSingleton<HostedOrganizerLogoStore>();
builder.Services.AddSingleton<RaceBroadcastService>();
builder.Services.AddSingleton<RaceWebSocketHandler>();
builder.Services.AddSingleton(new AdminSessionStore(configurationStore.AdminPasswordMatches));
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RaceBroadcastService>());
builder.Services.AddHostedService<RaceClockService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws: wss:";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", (RaceCoordinator coordinator, RaceWebSocketRegistry sockets) => Results.Ok(new
{
    status = "ok",
    serverTime = DateTimeOffset.UtcNow,
    phase = coordinator.Snapshot().Phase,
    connectedSockets = sockets.Count
}));

app.MapGet("/.well-known/lazyforza-race.json", (
    RaceCoordinator coordinator,
    HostedTrackPackageStore trackPackages,
    HostedOrganizerLogoStore organizerLogos) =>
{
    var snapshot = coordinator.Snapshot();
    var hosted = trackPackages.Matching(snapshot.TrackId, snapshot.TrackRevision, snapshot.TrackPackageHash);
    var logo = organizerLogos.Current;
    return Results.Ok(new RaceServerDescriptor(
        serverOptions.ServerName,
        RaceProtocol.CurrentVersion,
        serverOptions.MaximumParticipants,
        true,
        "/ws",
        "/",
        snapshot.TrackId,
        snapshot.TrackRevision,
        snapshot.Phase,
        snapshot.ServerTime,
        snapshot.TrackName,
        snapshot.TrackPackageHash,
        snapshot.AllowTeams,
        snapshot.SectorCount,
        snapshot.DriversPerTeam,
        snapshot.Teams,
        hosted is not null,
        hosted?.SizeBytes,
        hosted is null ? null : "/api/track-package",
        hosted?.FileSha256,
        logo?.Sha256,
        logo?.MimeType,
        logo is null ? null : "/api/organizer-logo"));
});

app.MapGet("/api/organizer-logo", async (
    HostedOrganizerLogoStore organizerLogos,
    CancellationToken cancellationToken) =>
{
    var logo = organizerLogos.Current;
    if (logo is null) return Results.NotFound();
    var bytes = await organizerLogos.ReadAsync(cancellationToken);
    return bytes is null
        ? Results.NotFound()
        : Results.File(bytes, logo.MimeType, enableRangeProcessing: false,
            lastModified: logo.UploadedAt,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{logo.Sha256}\""));
});

app.MapGet("/api/track-package", async (
    RaceCoordinator coordinator,
    HostedTrackPackageStore trackPackages,
    CancellationToken cancellationToken) =>
{
    var snapshot = coordinator.Snapshot();
    var hosted = trackPackages.Matching(snapshot.TrackId, snapshot.TrackRevision, snapshot.TrackPackageHash);
    if (hosted is null) return Results.NotFound(new { error = "当前房间没有可下载的匹配赛道文件。" });
    var bytes = await trackPackages.ReadAsync(snapshot.TrackId, snapshot.TrackRevision, snapshot.TrackPackageHash, cancellationToken);
    return bytes is null
        ? Results.NotFound(new { error = "托管的赛道文件不存在。" })
        : Results.File(bytes, "application/vnd.lazyforza.estate-track", hosted.FileName, enableRangeProcessing: false,
            lastModified: hosted.UploadedAt, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hosted.FileSha256}\""));
});

app.Map("/ws", (HttpContext context, RaceWebSocketHandler handler) => handler.HandleAsync(context));

app.MapGet("/api/setup/status", (RaceServerConfigurationStore settings) => Results.Ok(new
{
    isConfigured = settings.IsConfigured,
    defaults = settings.InitialRoomSettings
}));

app.MapPost("/api/setup", (
    RaceServerInitialSetupRequest request,
    RaceServerConfigurationStore settings,
    RaceCoordinator coordinator) =>
{
    var setup = settings.ConfigureInitial(request);
    if (!setup.Success) return Results.BadRequest(new { error = setup.Error });
    var room = setup.Settings!;
    var applied = coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
        room.SessionName,
        room.TotalRaceLaps,
        room.SectorCount,
        room.AutomaticYellowEnabled,
        room.SlowSpeedKph,
        room.SlowDurationSeconds,
        room.SevereLateralOffsetMeters,
        room.RecoveryDurationSeconds,
        room.AllowTeams,
        room.TrackName,
        room.TrackId,
        room.TrackRevision,
        room.TrackPackageHash,
        room.TeamCount,
        room.DriversPerTeam,
        room.Teams,
        room.TrackLimitMode,
        room.MinimumRequiredPitStops,
        room.AutomaticCollisionInvestigationsEnabled,
        room.DisconnectedLapRecoveryEnabled));
    return applied.IsAccepted ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = applied.Error });
});

app.MapPost("/api/admin/login", (RaceAdminLoginRequest request, HttpContext context, AdminSessionStore sessions) =>
{
    if (!sessions.PasswordMatches(request.Password)) return Results.Json(new { error = "总控密码不正确。" }, statusCode: 401);
    var token = sessions.Create();
    context.Response.Cookies.Append(AdminSessionStore.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromHours(12),
        Path = "/"
    });
    return Results.Ok(new { serverName = serverOptions.ServerName });
});

app.MapPost("/api/admin/logout", (HttpContext context, AdminSessionStore sessions) =>
{
    context.Request.Cookies.TryGetValue(AdminSessionStore.CookieName, out var token);
    sessions.Revoke(token);
    context.Response.Cookies.Delete(AdminSessionStore.CookieName);
    return Results.Ok();
});

app.MapGet("/api/admin/state", (HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    Authorized(context, sessions) ? Results.Ok(coordinator.Snapshot()) : Results.Unauthorized());

app.MapGet("/api/admin/settings", (HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    Authorized(context, sessions) ? Results.Ok(coordinator.RoomSettings()) : Results.Unauthorized());

app.MapGet("/api/admin/events", (HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator, int? limit, long? after) =>
    Authorized(context, sessions)
        ? Results.Ok(coordinator.Events(Math.Clamp(limit ?? 200, 20, 500), after))
        : Results.Unauthorized());

app.MapGet("/api/admin/results", (HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    Authorized(context, sessions) ? Results.Ok(coordinator.Results()) : Results.Unauthorized());

app.MapGet("/api/admin/investigations/{investigationId:guid}/replay", (
    Guid investigationId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceCoordinator coordinator) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    var replay = coordinator.CollisionReplay(investigationId);
    return replay is null ? Results.NotFound() : Results.Ok(replay);
});

app.MapGet("/api/admin/track-package", (HttpContext context, AdminSessionStore sessions, HostedTrackPackageStore trackPackages) =>
    Authorized(context, sessions) ? Results.Ok(new { package = trackPackages.Current, maximumBytes = HostedTrackPackageStore.MaximumPackageBytes }) : Results.Unauthorized());

app.MapGet("/api/admin/organizer-logo", (
    HttpContext context,
    AdminSessionStore sessions,
    HostedOrganizerLogoStore organizerLogos) =>
    Authorized(context, sessions)
        ? Results.Ok(new { logo = organizerLogos.Current, maximumBytes = HostedOrganizerLogoStore.MaximumLogoBytes })
        : Results.Unauthorized());

app.MapPost("/api/admin/organizer-logo", async (
    HttpContext context,
    AdminSessionStore sessions,
    HostedOrganizerLogoStore organizerLogos,
    RaceCoordinator coordinator,
    RaceBroadcastService broadcasts,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "请使用表单上传赛事 Logo。" });
    try
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { error = "请选择 PNG 或 JPEG 图片。" });
        await using var stream = file.OpenReadStream();
        var metadata = await organizerLogos.SaveAsync(stream, file.FileName, file.ContentType, cancellationToken);
        broadcasts.Queue(coordinator.Snapshot());
        return Results.Ok(new { logo = metadata });
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapDelete("/api/admin/organizer-logo", async (
    HttpContext context,
    AdminSessionStore sessions,
    HostedOrganizerLogoStore organizerLogos,
    RaceCoordinator coordinator,
    RaceBroadcastService broadcasts,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    await organizerLogos.DeleteAsync(cancellationToken);
    broadcasts.Queue(coordinator.Snapshot());
    return Results.Ok();
});

app.MapPost("/api/admin/track-package", async (
    HttpContext context,
    AdminSessionStore sessions,
    HostedTrackPackageStore trackPackages,
    RaceCoordinator coordinator,
    RaceServerConfigurationStore settings,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "请使用表单上传 .lfzestate 文件。" });
    try
    {
        if (coordinator.Snapshot().Phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Finished))
            return Results.BadRequest(new { error = "排位赛或正赛进行期间不能更换赛事赛道。请先返回大厅。" });
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { error = "请选择要托管的 .lfzestate 文件。" });
        await using var stream = file.OpenReadStream();
        var metadata = await trackPackages.SaveAsync(stream, file.FileName, cancellationToken);
        var current = coordinator.RoomSettings();
        var result = coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            current.SessionName,
            current.TotalRaceLaps,
            current.SectorCount,
            current.AutomaticYellowEnabled,
            current.SlowSpeedKph,
            current.SlowDurationSeconds,
            current.SevereLateralOffsetMeters,
            current.RecoveryDurationSeconds,
            current.AllowTeams,
            metadata.TrackName,
            metadata.TrackId,
            metadata.TrackRevision,
            metadata.TrackPackageHash,
            current.TeamCount,
            current.DriversPerTeam,
            current.Teams,
            current.TrackLimitMode,
            current.MinimumRequiredPitStops,
            current.AutomaticCollisionInvestigationsEnabled,
            current.DisconnectedLapRecoveryEnabled));
        if (!result.IsAccepted) return Results.BadRequest(new { error = result.Error });
        settings.SaveRoomSettings(coordinator.RoomSettings());
        return Results.Ok(new { package = metadata, room = coordinator.RoomSettings() });
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapDelete("/api/admin/track-package", async (
    HttpContext context,
    AdminSessionStore sessions,
    HostedTrackPackageStore trackPackages,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    await trackPackages.DeleteAsync(cancellationToken);
    return Results.Ok();
});

app.MapPost("/api/admin/settings", (
    RaceAdminRoomSettingsCommand command,
    HttpContext context,
    AdminSessionStore sessions,
    RaceCoordinator coordinator,
    RaceServerConfigurationStore settings) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    var result = coordinator.ApplyRoomSettings(command);
    if (!result.IsAccepted) return Results.BadRequest(new { error = result.Error });
    settings.SaveRoomSettings(coordinator.RoomSettings());
    return Results.Ok();
});

app.MapPost("/api/admin/collision-investigations", (
    RaceAdminCollisionInvestigationSettingsCommand command,
    HttpContext context,
    AdminSessionStore sessions,
    RaceCoordinator coordinator,
    RaceServerConfigurationStore settings) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    var result = coordinator.SetAutomaticCollisionInvestigations(command.Enabled);
    if (!result.IsAccepted) return Results.BadRequest(new { error = result.Error });
    settings.SaveRoomSettings(coordinator.RoomSettings());
    return Results.Ok();
});

app.MapPost("/api/admin/session", (RaceAdminSessionCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplySessionCommand(command)));

app.MapPost("/api/admin/flag", (RaceAdminFlagCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyFlagCommand(command)));

app.MapPost("/api/admin/penalty", (RaceAdminPenaltyCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyPenalty(command)));

app.MapPost("/api/admin/penalty/update", (RaceAdminPenaltyUpdateCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.UpdatePenalty(command)));

app.MapPost("/api/admin/investigation", (RaceAdminInvestigationCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ResolveInvestigation(command)));

app.MapPost("/api/admin/participant", (RaceAdminParticipantCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyParticipantCommand(command)));

app.MapPost("/api/admin/disconnect", async (
    RaceAdminDisconnectCommand command,
    HttpContext context,
    AdminSessionStore sessions,
    RaceCoordinator coordinator,
    RaceWebSocketRegistry sockets,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    _ = cancellationToken;
    await sockets.DisconnectAsync(command.ClientId, "Disconnected by race control", CancellationToken.None);
    var result = coordinator.DisconnectAndReleaseClient(command.ClientId);
    return result.IsAccepted ? Results.Ok() : Results.BadRequest(new { error = result.Error });
});

if (!configurationStore.IsConfigured)
    app.Logger.LogWarning("Race server is waiting for first-time setup in the web control panel. Do not expose it before claiming the room.");

app.Run();

static bool Authorized(HttpContext context, AdminSessionStore sessions) =>
    context.Request.Cookies.TryGetValue(AdminSessionStore.CookieName, out var token) && sessions.IsValid(token);

static IResult AdminResult(
    HttpContext context,
    AdminSessionStore sessions,
    Func<RaceCommandResult> command)
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    var result = command();
    return result.IsAccepted ? Results.Ok() : Results.BadRequest(new { error = result.Error });
}

public partial class Program;
