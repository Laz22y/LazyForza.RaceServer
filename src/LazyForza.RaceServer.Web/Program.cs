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
builder.Services.AddSingleton<RaceRuleTemplateStore>();
builder.Services.AddSingleton<RaceEventProjectStore>();
builder.Services.AddSingleton<RaceBroadcastService>();
builder.Services.AddSingleton<RaceWebSocketHandler>();
builder.Services.AddSingleton(new AdminSessionStore(configurationStore.AdminPasswordMatches));
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RaceBroadcastService>());
builder.Services.AddHostedService<RaceClockService>();
builder.Services.AddHostedService<RaceEventProjectSyncService>();

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

app.MapGet("/api/admin/event-projects", (
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects) =>
    Authorized(context, sessions)
        ? Results.Ok(new { projects = projects.List(), maximumProjects = RaceEventProjectStore.MaximumProjects })
        : Results.Unauthorized());

app.MapGet("/api/admin/event-projects/{projectId:guid}", (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects) =>
    Authorized(context, sessions)
        ? projects.Find(projectId) is { } project
            ? Results.Ok(new { project })
            : Results.NotFound(new { error = "赛事项目不存在。" })
        : Results.Unauthorized());

app.MapPost("/api/admin/event-projects", async (
    RaceEventProjectSaveRequest request,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator,
    HostedTrackPackageStore trackPackages,
    HostedOrganizerLogoStore organizerLogos,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        projects.SyncActive(coordinator.Results(), coordinator.Events(500));
        var assets = await ReadCurrentProjectAssets(
            trackPackages, organizerLogos, coordinator.RoomSettings(), cancellationToken);
        var project = projects.Create(
            request, coordinator.RoomSettings(), coordinator.Results(), coordinator.Events(500),
            assets.TrackMetadata, assets.TrackBytes, assets.LogoMetadata, assets.LogoBytes);
        return Results.Ok(new { project });
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPut("/api/admin/event-projects/{projectId:guid}", async (
    Guid projectId,
    RaceEventProjectSaveRequest request,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator,
    HostedTrackPackageStore trackPackages,
    HostedOrganizerLogoStore organizerLogos,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        projects.SyncActive(coordinator.Results(), coordinator.Events(500));
        var assets = await ReadCurrentProjectAssets(
            trackPackages, organizerLogos, coordinator.RoomSettings(), cancellationToken);
        var project = projects.Capture(
            projectId, request, coordinator.RoomSettings(), coordinator.Results(), coordinator.Events(500),
            assets.TrackMetadata, assets.TrackBytes, assets.LogoMetadata, assets.LogoBytes);
        return Results.Ok(new { project });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "赛事项目不存在。" }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/admin/event-projects/{projectId:guid}/copy", (
    Guid projectId,
    RaceEventProjectCopyRequest request,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try { return Results.Ok(new { project = projects.Copy(projectId, request.Name) }); }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "赛事项目不存在。" }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/admin/event-projects/{projectId:guid}/activate", async (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator,
    RaceServerConfigurationStore settings,
    HostedTrackPackageStore trackPackages,
    HostedOrganizerLogoStore organizerLogos,
    RaceBroadcastService broadcasts,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        if (coordinator.Snapshot().Phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Finished))
            return Results.BadRequest(new { error = "练习赛、排位赛或正赛进行期间不能切换赛事项目。请先返回大厅。" });
        var project = projects.Find(projectId) ?? throw new KeyNotFoundException();
        var assets = projects.ReadAssets(projectId);
        var applied = coordinator.ApplyRoomSettings(ToRoomCommand(project.Room));
        if (!applied.IsAccepted) return Results.BadRequest(new { error = applied.Error });

        if (project.TrackPackage is not null && assets.TrackPackage is not null)
        {
            await using var trackStream = new MemoryStream(assets.TrackPackage, writable: false);
            await trackPackages.SaveAsync(trackStream, project.TrackPackage.FileName, cancellationToken);
        }
        else await trackPackages.DeleteAsync(cancellationToken);

        if (project.OrganizerLogo is not null && assets.OrganizerLogo is not null)
        {
            await using var logoStream = new MemoryStream(assets.OrganizerLogo, writable: false);
            await organizerLogos.SaveAsync(
                logoStream, project.OrganizerLogo.FileName, project.OrganizerLogo.MimeType, cancellationToken);
        }
        else await organizerLogos.DeleteAsync(cancellationToken);

        settings.SaveRoomSettings(coordinator.RoomSettings());
        project = projects.Activate(projectId);
        broadcasts.Queue(coordinator.Snapshot());
        return Results.Ok(new { project, room = coordinator.RoomSettings() });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "赛事项目不存在。" }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/admin/event-projects/{projectId:guid}/complete", (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator) =>
    ChangeProjectStatus(projectId, RaceEventProjectStatus.Completed, context, sessions, projects, coordinator));

app.MapPost("/api/admin/event-projects/{projectId:guid}/archive", (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator) =>
    ChangeProjectStatus(projectId, RaceEventProjectStatus.Archived, context, sessions, projects, coordinator));

app.MapDelete("/api/admin/event-projects/{projectId:guid}", (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        return projects.Delete(projectId)
            ? Results.Ok()
            : Results.NotFound(new { error = "赛事项目不存在。" });
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/admin/event-projects/{projectId:guid}/export", (
    Guid projectId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        projects.SyncActive(coordinator.Results(), coordinator.Events(500));
        var project = projects.Find(projectId) ?? throw new KeyNotFoundException();
        return Results.File(
            projects.Export(projectId),
            "application/vnd.lazyforza.event-project",
            RaceEventProjectStore.SafeExportFileName(project));
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "赛事项目不存在。" }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/admin/event-projects/import", async (
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "请使用表单上传 .lfzevent 文件。" });
    try
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { error = "请选择要导入的 .lfzevent 文件。" });
        var bytes = await ReadBoundedAsync(file, RaceEventProjectStore.MaximumPackageBytes, cancellationToken);
        return Results.Ok(new { project = projects.Import(bytes) });
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/admin/rule-templates", (
    HttpContext context,
    AdminSessionStore sessions,
    RaceRuleTemplateStore templates) =>
    Authorized(context, sessions)
        ? Results.Ok(new { templates = templates.List(), maximumTemplates = RaceRuleTemplateStore.MaximumTemplates })
        : Results.Unauthorized());

app.MapPost("/api/admin/rule-templates", (
    RaceRuleTemplateSaveRequest request,
    HttpContext context,
    AdminSessionStore sessions,
    RaceRuleTemplateStore templates) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try { return Results.Ok(new { template = templates.Create(request) }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPut("/api/admin/rule-templates/{templateId:guid}", (
    Guid templateId,
    RaceRuleTemplateSaveRequest request,
    HttpContext context,
    AdminSessionStore sessions,
    RaceRuleTemplateStore templates) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try { return Results.Ok(new { template = templates.Update(templateId, request) }); }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "规则模板不存在。" }); }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/admin/rule-templates/{templateId:guid}/apply", (
    Guid templateId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceRuleTemplateStore templates,
    RaceCoordinator coordinator,
    RaceServerConfigurationStore settings) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    var template = templates.Find(templateId);
    if (template is null) return Results.NotFound(new { error = "规则模板不存在。" });
    var result = coordinator.ApplyRoomSettings(RaceRuleTemplateStore.MergeWithRoom(template, coordinator.RoomSettings()));
    if (!result.IsAccepted) return Results.BadRequest(new { error = result.Error });
    settings.SaveRoomSettings(coordinator.RoomSettings());
    return Results.Ok(new { template, room = coordinator.RoomSettings() });
});

app.MapDelete("/api/admin/rule-templates/{templateId:guid}", (
    Guid templateId,
    HttpContext context,
    AdminSessionStore sessions,
    RaceRuleTemplateStore templates) =>
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    return templates.Delete(templateId)
        ? Results.Ok()
        : Results.NotFound(new { error = "规则模板不存在。" });
});

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

static IResult ChangeProjectStatus(
    Guid projectId,
    RaceEventProjectStatus status,
    HttpContext context,
    AdminSessionStore sessions,
    RaceEventProjectStore projects,
    RaceCoordinator coordinator)
{
    if (!Authorized(context, sessions)) return Results.Unauthorized();
    try
    {
        projects.SyncActive(coordinator.Results(), coordinator.Events(500));
        return Results.Ok(new { project = projects.SetStatus(projectId, status) });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "赛事项目不存在。" }); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}

static RaceAdminRoomSettingsCommand ToRoomCommand(RaceRoomSettingsSnapshot room) => new(
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
    room.DisconnectedLapRecoveryEnabled);

static async Task<(
    HostedTrackPackageMetadata? TrackMetadata,
    byte[]? TrackBytes,
    HostedOrganizerLogoMetadata? LogoMetadata,
    byte[]? LogoBytes)> ReadCurrentProjectAssets(
    HostedTrackPackageStore trackPackages,
    HostedOrganizerLogoStore organizerLogos,
    RaceRoomSettingsSnapshot room,
    CancellationToken cancellationToken)
{
    var track = trackPackages.Matching(room.TrackId, room.TrackRevision, room.TrackPackageHash);
    var logo = organizerLogos.Current;
    var trackBytes = track is null
        ? null
        : await trackPackages.ReadAsync(track.TrackId, track.TrackRevision, track.TrackPackageHash, cancellationToken);
    var logoBytes = logo is null ? null : await organizerLogos.ReadAsync(cancellationToken);
    return (track, trackBytes, logo, logoBytes);
}

static async Task<byte[]> ReadBoundedAsync(
    IFormFile file,
    long maximumBytes,
    CancellationToken cancellationToken)
{
    if (file.Length is <= 0 || file.Length > maximumBytes)
        throw new InvalidDataException("赛事项目包为空或超过 4 MiB 上限。");
    await using var source = file.OpenReadStream();
    await using var output = new MemoryStream();
    var buffer = new byte[32 * 1024];
    while (true)
    {
        var read = await source.ReadAsync(buffer, cancellationToken);
        if (read == 0) break;
        if (output.Length + read > maximumBytes)
            throw new InvalidDataException("赛事项目包超过 4 MiB 上限。");
        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }
    return output.ToArray();
}

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
