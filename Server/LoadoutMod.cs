using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Location;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;

namespace RaidBotLoadouts;

public sealed record Metadata : IModMetadata
{
    public string ModGuid { get; init; } = LoadoutProtocol.ModId;
    public string Name { get; init; } = "Raid Bot Loadouts";
    public string Author { get; init; } = "ColhiruX";
    public SemanticVersioning.Version Version { get; init; } = new(LoadoutProtocol.Version);
    public SemanticVersioning.Range SptVersion { get; init; } = new("=4.1.6");
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";
    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
}

[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public sealed class LoadoutMod(LoadoutService service, LocationController locations, HttpResponseUtil responses,
    ISptLogger<LoadoutMod> logger) : IOnLoad
{
    private static LoadoutMod instance = null!;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        instance = this;
        var harmony = new Harmony(LoadoutProtocol.ModId);
        var target = AccessTools.Method(typeof(BotInventoryGenerator), nameof(BotInventoryGenerator.GenerateInventory));
        harmony.Patch(target, prefix: Patch(nameof(ApplyLoadout)), postfix: Patch(nameof(FinishLoadout)));
        harmony.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot)),
            postfix: Patch(nameof(FilterLocation)));
        harmony.Patch(AccessTools.Method(typeof(LocationCallbacks), nameof(LocationCallbacks.GetAirdropLoot)),
            prefix: Patch(nameof(FilterAirdrop)));
        logger.Info("Raid Bot Loadouts is ready.");
        return Task.CompletedTask;
    }

    private static HarmonyMethod Patch(string name) => new(typeof(LoadoutMod), name) { priority = Priority.Last };

    private static void ApplyLoadout(MongoId sessionId, BotType botJsonTemplate, BotGenerationDetails botGenerationDetails,
        out BotLoadout? __state) => __state = instance.Service.Apply(sessionId, botJsonTemplate, botGenerationDetails);

    private static void FinishLoadout(BotBaseInventory __result, BotLoadout? __state)
    {
        if (__state is not null)
        {
            instance.Service.FinishBot(__result, __state);
        }
    }

    private static void FilterLocation(MongoId sessionId, string name, bool generateLoot, LocationBase __result)
    {
        if (generateLoot && !name.Equals("hideout", StringComparison.OrdinalIgnoreCase) && __result.Loot is { } loot)
        {
            var filter = instance.Service.GetLootFilter(sessionId);
            if (filter.Enabled)
            {
                __result.Loot = filter.FilterLocation(loot);
            }
        }
    }

    private static bool FilterAirdrop(GetAirdropLootRequest? info, MongoId sessionID, ref ValueTask<string> __result)
    {
        var filter = instance.Service.GetLootFilter(sessionID);
        if (!filter.Enabled)
        {
            return true;
        }

        var response = instance.Locations.GetAirDropLoot(info);
        var gear = response.Container.ToList();
        filter.Filter(gear);
        response.Container = gear;
        __result = ValueTask.FromResult(instance.Responses.GetBody(response));
        return false;
    }

    private LoadoutService Service => service;
    private LocationController Locations => locations;
    private HttpResponseUtil Responses => responses;
}
