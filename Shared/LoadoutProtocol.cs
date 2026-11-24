namespace RaidBotLoadouts;

internal static class LoadoutProtocol
{
    internal const string ModId = "com.colhirux.raidbotloadouts";
    internal const string Version = "1.1.1";
    internal const string Editor = "/raid-bot-loadouts/editor";
    internal const string Save = "/raid-bot-loadouts/save";
}

public sealed class RoleLoadout
{
    public string? Weapon { get; set; }
    public string? WeaponCategory { get; set; }
    public string? Armor { get; set; }
    public int? ArmorClass { get; set; }
}

public sealed class LoadoutSettings
{
    public RoleLoadout Scavs { get; set; } = new();
    public RoleLoadout Pmcs { get; set; } = new();
    public RoleLoadout Bosses { get; set; } = new();
    public bool Ushankas { get; set; } = true;
    public bool FilterRaidLoot { get; set; } = true;

    internal RoleLoadout[] Roles => [Scavs, Pmcs, Bosses];
}

public sealed class GearChoice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SearchText { get; set; } = "";
}

public sealed class EditorReply
{
    public LoadoutSettings? Settings { get; set; }
    public GearChoice[] Weapons { get; set; } = [];
    public GearChoice[] WeaponCategories { get; set; } = [];
    public GearChoice[] Armor { get; set; } = [];
    public int[] ArmorClasses { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class SaveReply
{
    public bool Saved { get; set; }
    public string? Error { get; set; }
}
