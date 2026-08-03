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
    SectorCount = initialRoom.SectorCount,
    AutomaticYellowEnabled = initialRoom.AutomaticYellowEnabled,
    SlowSpeedKph = initialRoom.SlowSpeedKph,
    SlowDurationSeconds = initialRoom.SlowDurationSeconds,
    SevereLateralOffsetMeters = initialRoom.SevereLateralOffsetMeters,
    RecoveryDurationSeconds = initialRoom.RecoveryDurationSeconds,
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

app.MapGet("/.well-known/lazyforza-race.json", (RaceCoordinator coordinator) =>
{
    var snapshot = coordinator.Snapshot();
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
        snapshot.SectorCount));
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
        room.TrackPackageHash));
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

app.MapPost("/api/admin/session", (RaceAdminSessionCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplySessionCommand(command)));

app.MapPost("/api/admin/flag", (RaceAdminFlagCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyFlagCommand(command)));

app.MapPost("/api/admin/penalty", (RaceAdminPenaltyCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyPenalty(command)));

app.MapPost("/api/admin/participant", (RaceAdminParticipantCommand command, HttpContext context, AdminSessionStore sessions, RaceCoordinator coordinator) =>
    AdminResult(context, sessions, () => coordinator.ApplyParticipantCommand(command)));

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
