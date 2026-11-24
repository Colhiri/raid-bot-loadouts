using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Locales;
using Path = System.IO.Path;

namespace RaidBotLoadouts;

internal sealed record BotLoadout(LoadoutSettings Settings, ArmorVariant? Armor);

[Injectable(InjectionType.Singleton)]
public sealed class LoadoutService(
    BotTable bots, TemplateTable templates, LocaleService locales, ItemHelper items, ModHelper modHelper,
    SaveServer profiles, ISptLogger<LoadoutService> logger)
{
    private static readonly LoadoutSettings Defaults = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<MongoId, LoadoutSettings> settings = new();
    private readonly ConditionalWeakTable<LoadoutSettings, RaidLootFilter> lootFilters = new();
    private readonly Lazy<GearCatalog> catalog = new(() => new GearCatalog(bots, templates, locales, items));
    private readonly Lazy<string> settingsDirectory = new(() =>
        Path.Combine(modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "settings"));
    private readonly object saveLock = new();

    internal EditorReply OpenEditor(MongoId sessionId)
    {
        if (!profiles.GetProfiles().ContainsKey(sessionId))
        {
            return new EditorReply { Error = "Открой игру с профилем SPT." };
        }

        return new EditorReply
        {
            Settings = settings.GetOrAdd(sessionId, ReadSettings),
            Weapons = catalog.Value.Weapons,
            Armor = catalog.Value.Armor,
            WeaponCategories = catalog.Value.WeaponCategories,
            ArmorClasses = catalog.Value.ArmorByClass.Keys.Order().ToArray()
        };
    }

    internal SaveReply Save(MongoId sessionId, LoadoutSettings? requested)
    {
        if (!profiles.GetProfiles().ContainsKey(sessionId))
        {
            return new SaveReply { Error = "Открой игру с профилем SPT." };
        }

        if (requested is null || !IsValid(requested.Scavs) || !IsValid(requested.Pmcs) || !IsValid(requested.Bosses))
        {
            return new SaveReply { Error = "В комплекте есть недоступное оружие или броня." };
        }

        lock (saveLock)
        {
            var path = SettingsPath(sessionId);
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(settingsDirectory.Value);
                File.WriteAllText(temporary, JsonSerializer.Serialize(requested, JsonOptions));
                File.Move(temporary, path, overwrite: true);
                settings[sessionId] = requested;
                return new SaveReply { Saved = true };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.Warning($"Could not save bot loadouts: {exception.Message}");
                return new SaveReply { Error = "Серверу не удалось записать настройки." };
            }
        }
    }

    internal BotLoadout? Apply(MongoId sessionId, BotType bot, BotGenerationDetails details)
    {
        if (details.IsPlayerScav)
        {
            return null;
        }

        var current = settings.GetOrAdd(sessionId, ReadSettings);
        var role = details.RoleLowercase;
        RoleLoadout? selection = details.IsPmc ? current.Pmcs : role switch
        {
            "assault" or "assaultgroup" or "cursedassault" or "crazyassaultevent" or "marksman" => current.Scavs,
            "followerbirdeye" or "followerbigpipe" => current.Bosses,
            _ when role.StartsWith("boss", StringComparison.Ordinal) => current.Bosses,
            _ => null
        };

        var weapon = SelectGear(selection, current, SelectWeapon);
        var armor = SelectGear(selection, current, SelectArmor);
        var inventory = bot.BotInventory;
        var chances = bot.BotChances.EquipmentChances;
        if (weapon is not null)
        {
            var slot = weapon.Slot == EquipmentSlots.Holster ? EquipmentSlots.Holster : EquipmentSlots.FirstPrimaryWeapon;
            SetChance(chances, EquipmentSlots.FirstPrimaryWeapon, 0);
            SetChance(chances, EquipmentSlots.SecondPrimaryWeapon, 0);
            SetChance(chances, EquipmentSlots.Holster, 0);
            SetEquipment(inventory, chances, slot, weapon);
            foreach (var (caliber, ammo) in weapon.Source.Ammo)
            {
                if (!inventory.Ammo.ContainsKey(caliber))
                {
                    inventory.Ammo.Add(caliber, new Dictionary<MongoId, double>(ammo));
                }
            }
        }

        if (armor is not null)
        {
            if (armor.Entry.Slot == EquipmentSlots.TacticalVest)
            {
                SetChance(chances, EquipmentSlots.ArmorVest, 0);
            }

            SetEquipment(inventory, chances, armor.Entry.Slot, armor.Entry);
        }

        return new BotLoadout(current, armor);
    }

    private static T? SelectGear<T>(RoleLoadout? preferred, LoadoutSettings current, Func<RoleLoadout, T?> select) where T : class
    {
        var explicitGear = preferred is null ? null : select(preferred);
        if (explicitGear is not null || !current.FilterRaidLoot)
        {
            return explicitGear;
        }

        var allowed = current.Roles.Select(select).OfType<T>().ToArray();
        return allowed.Length == 0 ? null : allowed[Random.Shared.Next(allowed.Length)];
    }

    private GearEntry? SelectWeapon(RoleLoadout role)
    {
        if (role.WeaponCategory is { } category && catalog.Value.WeaponsByCategory.TryGetValue(category, out var weapons))
        {
            return weapons[Random.Shared.Next(weapons.Length)];
        }

        return role.Weapon is { } id ? catalog.Value.WeaponById.GetValueOrDefault(id) : null;
    }

    private ArmorVariant? SelectArmor(RoleLoadout role)
    {
        if (role.ArmorClass is { } grade && catalog.Value.ArmorByClass.TryGetValue(grade, out var armor))
        {
            return armor[Random.Shared.Next(armor.Length)];
        }

        return role.Armor is { } id && catalog.Value.ArmorById.TryGetValue(id, out var entry)
            ? new ArmorVariant(entry, null, []) : null;
    }

    internal void FinishBot(BotBaseInventory inventory, BotLoadout loadout)
    {
        if (inventory.Items is not { } gear || inventory.Equipment is not { } equipmentId)
        {
            return;
        }

        var equipment = equipmentId.ToString();
        var equipped = gear.Where(item => item.ParentId == equipment).ToArray();
        var armor = loadout.Armor;
        if (armor?.PlateSlot is { } plateSlot &&
            equipped.FirstOrDefault(item => item.Template == armor.Entry.Id && item.SlotId == armor.Entry.Slot.ToString()) is { } vest)
        {
            var plate = gear.FirstOrDefault(item => item.ParentId == vest.Id.ToString() && GearCatalog.IsFrontPlate(item.SlotId));
            if (plate is null || !armor.Plates.Contains(plate.Template))
            {
                var replacement = armor.Plates[Random.Shared.Next(armor.Plates.Length)];
                var durability = templates.Items[replacement].Properties?.MaxDurability;
                if (plate is null)
                {
                    plate = new Item { Id = new MongoId(), ParentId = vest.Id.ToString(), SlotId = plateSlot };
                    gear.Add(plate);
                }

                plate.Template = replacement;
                plate.Upd = new Upd { Repairable = new UpdRepairable { Durability = durability, MaxDurability = durability } };
            }
        }

        if (loadout.Settings.Ushankas && templates.Items.TryGetValue(ItemTpl.HEADWEAR_USHANKA_EAR_FLAP_HAT, out var hat))
        {
            var conflicts = hat.Properties?.ConflictingItems;
            var remove = equipped.Where(item => (item.SlotId == nameof(EquipmentSlots.Headwear)
                && item.Template != ItemTpl.HEADWEAR_USHANKA_EAR_FLAP_HAT)
                || conflicts?.Contains(item.Template) == true
                || templates.Items.GetValueOrDefault(item.Template)?.Properties?.ConflictingItems?.Contains(hat.Id) == true);
            RaidLootFilter.RemoveBranches(gear, remove.Select(item => item.Id));
            if (!equipped.Any(item => item.SlotId == nameof(EquipmentSlots.Headwear) && item.Template == hat.Id))
            {
                gear.Add(new Item { Id = new MongoId(), Template = hat.Id, ParentId = equipment, SlotId = nameof(EquipmentSlots.Headwear) });
            }
        }

        var filter = GetLootFilter(loadout.Settings);
        filter.Filter(gear, keep: item => item.ParentId == equipment
            && item.SlotId is not nameof(EquipmentSlots.Headwear) and not nameof(EquipmentSlots.FaceCover));
    }

    internal RaidLootFilter GetLootFilter(MongoId sessionId) => GetLootFilter(settings.GetOrAdd(sessionId, ReadSettings));

    private RaidLootFilter GetLootFilter(LoadoutSettings current) =>
        lootFilters.GetValue(current, value => new RaidLootFilter(value, templates, items));

    private bool IsValid(RoleLoadout? role) => role is not null
        && (role.Weapon is null || (role.WeaponCategory is null && catalog.Value.WeaponById.ContainsKey(role.Weapon)))
        && (role.WeaponCategory is null || catalog.Value.WeaponsByCategory.ContainsKey(role.WeaponCategory))
        && (role.Armor is null || (role.ArmorClass is null && catalog.Value.ArmorById.ContainsKey(role.Armor)))
        && (role.ArmorClass is not { } grade || catalog.Value.ArmorByClass.ContainsKey(grade));

    private string SettingsPath(MongoId sessionId) => Path.Combine(settingsDirectory.Value, $"{sessionId}.json");

    private LoadoutSettings ReadSettings(MongoId sessionId)
    {
        var path = SettingsPath(sessionId);
        if (!File.Exists(path))
        {
            return Defaults;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<LoadoutSettings>(File.ReadAllText(path), JsonOptions);
            if (saved is null)
            {
                return Defaults;
            }

            saved.Scavs ??= new RoleLoadout();
            saved.Pmcs ??= new RoleLoadout();
            saved.Bosses ??= new RoleLoadout();
            return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Warning($"Could not read bot loadouts: {exception.Message}");
            return Defaults;
        }
    }

    private static void SetEquipment(BotTypeInventory inventory, Dictionary<string, double> chances, EquipmentSlots slot, GearEntry gear)
    {
        inventory.Equipment[slot] = new Dictionary<MongoId, double> { [gear.Id] = 1 };
        SetChance(chances, slot, 100);

        // Use the selected item's bot mod pools even when the current role never carries that item by default.
        var pending = new Queue<MongoId>();
        var seen = new HashSet<MongoId>();
        pending.Enqueue(gear.Id);
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id) || !gear.Source.Mods.TryGetValue(id, out var slots))
            {
                continue;
            }

            inventory.Mods[id] = slots.ToDictionary(pair => pair.Key, pair => new HashSet<MongoId>(pair.Value));
            foreach (var mod in slots.Values.SelectMany(mods => mods))
            {
                pending.Enqueue(mod);
            }
        }
    }

    private static void SetChance(Dictionary<string, double> chances, EquipmentSlots slot, double chance) =>
        chances[slot.ToString()] = chance;
}
