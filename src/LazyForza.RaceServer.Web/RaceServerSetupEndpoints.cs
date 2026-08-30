using Microsoft.AspNetCore.Routing;

namespace LazyForza.RaceServer.Web;

public static class RaceServerSetupEndpoints
{
    public static IEndpointRouteBuilder MapNativeSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/status", (RaceServerConfigurationStore settings) => Results.Ok(new
        {
            isConfigured = settings.IsConfigured,
            setupMode = "terminal",
            defaults = settings.InitialRoomSettings
        }));
        return endpoints;
    }
}
