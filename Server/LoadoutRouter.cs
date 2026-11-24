using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace RaidBotLoadouts;

public sealed class SaveLoadoutsRequest : IRequestData
{
    public LoadoutSettings? Settings { get; set; }
}

[Injectable]
public sealed class LoadoutRouter(JsonUtil json, HttpResponseUtil response, LoadoutService loadouts) : StaticRouter(json,
[
    new RouteAction<EmptyRequestData>(LoadoutProtocol.Editor, (_, _, sessionId, _, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(response.NoBody(loadouts.OpenEditor(sessionId)));
    }),
    new RouteAction<SaveLoadoutsRequest>(LoadoutProtocol.Save, (_, request, sessionId, _, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(response.NoBody(loadouts.Save(sessionId, request.Settings)));
    })
]);
