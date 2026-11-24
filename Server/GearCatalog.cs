using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace RaidBotLoadouts;

internal sealed record GearEntry(MongoId Id, EquipmentSlots Slot, BotTypeInventory Source);
internal sealed record ArmorVariant(GearEntry Entry, string? PlateSlot, MongoId[] Plates);

internal sealed class GearCatalog
{
    private static readonly (MongoId Id, string Label)[] Categories =
    [
        (BaseClasses.SHOTGUN, "Дробовики"),
        (BaseClasses.ASSAULT_RIFLE, "Штурмовые винтовки"),
        (BaseClasses.ASSAULT_CARBINE, "Штурмовые карабины"),
        (BaseClasses.SMG, "Пистолеты-пулемёты"),
        (BaseClasses.MARKSMAN_RIFLE, "Марксманские винтовки"),
        (BaseClasses.SNIPER_RIFLE, "Снайперские винтовки"),
        (BaseClasses.MACHINE_GUN, "Пулемёты"),
        (BaseClasses.PISTOL, "Пистолеты"),
        (BaseClasses.REVOLVER, "Револьверы"),
        (BaseClasses.GRENADE_LAUNCHER, "Гранатомёты"),
        (BaseClasses.ROCKET_LAUNCHER, "Ракетницы"),
        (BaseClasses.SPECIAL_WEAPON, "Специальное оружие")
    ];

    internal Dictionary<string, GearEntry> WeaponById { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, GearEntry> ArmorById { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, GearEntry[]> WeaponsByCategory { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<int, ArmorVariant[]> ArmorByClass { get; }
    internal GearChoice[] Weapons { get; }
    internal GearChoice[] Armor { get; }
    internal GearChoice[] WeaponCategories { get; }

    internal GearCatalog(BotTable bots, TemplateTable templates, LocaleService locales, ItemHelper items)
    {
        var russian = locales.GetLocaleDb("ru");
        var english = locales.GetLocaleDb("en");
        foreach (var bot in bots.Types.Values.OfType<BotType>())
        {
            var source = bot.BotInventory;
            AddSlot(WeaponById, source, EquipmentSlots.FirstPrimaryWeapon, id => items.IsOfBaseclass(id, BaseClasses.WEAPON));
            AddSlot(WeaponById, source, EquipmentSlots.SecondPrimaryWeapon, id => items.IsOfBaseclass(id, BaseClasses.WEAPON));
            AddSlot(WeaponById, source, EquipmentSlots.Holster, id => items.IsOfBaseclass(id, BaseClasses.WEAPON));
            AddSlot(ArmorById, source, EquipmentSlots.ArmorVest, _ => true);
            AddSlot(ArmorById, source, EquipmentSlots.TacticalVest, id => IsBodyArmor(id, templates, items));
        }

        foreach (var (category, _) in Categories)
        {
            var entries = WeaponById.Values.Where(entry => items.IsOfBaseclass(entry.Id, category)).ToArray();
            if (entries.Length > 0)
            {
                WeaponsByCategory.Add(category.ToString(), entries);
            }
        }

        WeaponCategories = Categories.Where(category => WeaponsByCategory.ContainsKey(category.Id.ToString()))
            .Select(category => new GearChoice { Id = category.Id.ToString(), Name = category.Label }).ToArray();
        ArmorByClass = ArmorById.Values.SelectMany(ArmorVariants)
            .GroupBy(variant => variant.Grade)
            .ToDictionary(group => group.Key, group => group.Select(variant => variant.Variant).ToArray());
        Weapons = Choices(WeaponById);
        Armor = Choices(ArmorById);

        IEnumerable<(int Grade, ArmorVariant Variant)> ArmorVariants(GearEntry entry)
        {
            var properties = templates.Items[entry.Id].Properties;
            var frontSlot = properties?.Slots?.FirstOrDefault(slot => IsFrontPlate(slot.Name));
            if (frontSlot?.Name is { } slotName)
            {
                var plates = frontSlot.Properties?.Filters?.SelectMany(filter => filter.Filter ?? []) ?? [];
                foreach (var group in plates.Distinct().Where(id => Grade(id, templates) is > 0)
                    .GroupBy(id => Grade(id, templates)))
                {
                    yield return (group.Key, new ArmorVariant(entry, slotName, group.ToArray()));
                }
                yield break;
            }

            var grade = BuiltInGrade(entry.Id, templates);
            if (grade > 0)
            {
                yield return (grade, new ArmorVariant(entry, null, []));
            }
        }

        void AddSlot(Dictionary<string, GearEntry> catalog, BotTypeInventory source, EquipmentSlots slot, Func<MongoId, bool> include)
        {
            if (!source.Equipment.TryGetValue(slot, out var pool))
            {
                return;
            }

            foreach (var id in pool.Keys)
            {
                var key = id.ToString();
                if (!catalog.ContainsKey(key) && templates.Items.ContainsKey(id) && include(id))
                {
                    catalog.Add(key, new GearEntry(id, slot, source));
                }
            }
        }

        GearChoice[] Choices(Dictionary<string, GearEntry> catalog) => catalog.Select(pair =>
        {
            var nameKey = $"{pair.Key} Name";
            var shortKey = $"{pair.Key} ShortName";
            var templateName = templates.Items[pair.Value.Id].Name;
            return new GearChoice
            {
                Id = pair.Key,
                Name = russian.GetValueOrDefault(nameKey) ?? english.GetValueOrDefault(nameKey) ?? templateName ?? pair.Key,
                SearchText = string.Join(" ", russian.GetValueOrDefault(shortKey), english.GetValueOrDefault(nameKey),
                    english.GetValueOrDefault(shortKey), templateName, pair.Key)
            };
        }).OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static bool IsFrontPlate(string? slot) => string.Equals(slot, "front_plate", StringComparison.OrdinalIgnoreCase);

    internal static int Grade(MongoId id, TemplateTable templates) =>
        templates.Items.GetValueOrDefault(id)?.Properties?.ArmorClass ?? 0;

    internal static int BuiltInGrade(MongoId id, TemplateTable templates)
    {
        var grade = Grade(id, templates);
        if (grade > 0)
        {
            return grade;
        }

        var slots = templates.Items.GetValueOrDefault(id)?.Properties?.Slots;
        return slots?.Where(slot => string.Equals(slot.Name, "soft_armor_front", StringComparison.OrdinalIgnoreCase))
            .SelectMany(slot => slot.Properties?.Filters ?? [])
            .SelectMany(filter => filter.Filter ?? [])
            .Select(insert => Grade(insert, templates)).DefaultIfEmpty().Max() ?? 0;
    }

    internal static bool IsBodyArmor(MongoId id, TemplateTable templates, ItemHelper items) =>
        items.IsOfBaseclass(id, BaseClasses.ARMOR)
        || (items.IsOfBaseclass(id, BaseClasses.VEST)
            && (BuiltInGrade(id, templates) > 0
                || templates.Items.GetValueOrDefault(id)?.Properties?.Slots?.Any(slot => IsFrontPlate(slot.Name)) == true));
}
