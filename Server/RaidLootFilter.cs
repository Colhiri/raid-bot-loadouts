using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace RaidBotLoadouts;

internal sealed class RaidLootFilter(LoadoutSettings settings, TemplateTable templates, ItemHelper items)
{
    private readonly HashSet<string> weaponIds = Ids(settings.Roles.Select(role => role.Weapon));
    private readonly HashSet<string> armorIds = Ids(settings.Roles.Select(role => role.Armor));
    private readonly MongoId[] weaponCategories = Ids(settings.Roles.Select(role => role.WeaponCategory))
        .Where(MongoId.IsValidMongoId).Select(id => new MongoId(id)).ToArray();
    private readonly HashSet<int> armorClasses = settings.Roles.Select(role => role.ArmorClass).OfType<int>()
        .Where(grade => grade is >= 1 and <= 6).ToHashSet();

    private bool FilterWeapons => settings.FilterRaidLoot && (weaponIds.Count > 0 || weaponCategories.Length > 0);
    private bool FilterArmor => settings.FilterRaidLoot && (armorIds.Count > 0 || armorClasses.Count > 0);
    internal bool Enabled => FilterWeapons || FilterArmor;

    private static HashSet<string> Ids(IEnumerable<string?> ids) => ids.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal IEnumerable<SpawnpointTemplate> FilterLocation(IEnumerable<SpawnpointTemplate> loot)
    {
        var result = new List<SpawnpointTemplate>();
        foreach (var point in loot)
        {
            if (point.Items is null)
            {
                result.Add(point);
                continue;
            }

            var gear = point.Items.ToList();
            Filter(gear, keep: item => item.Template == ItemTpl.MACHINEGUN_NSV_UTYOS_127X108_HEAVY_MACHINE_GUN
                || item.Template == ItemTpl.MACHINEGUN_AGS30_30X29MM_AUTOMATIC_GRENADE_LAUNCHER);
            if (gear.Count > 0)
            {
                result.Add(point with { Items = gear });
            }
        }
        return result;
    }

    internal void Filter<T>(List<T> gear, Func<T, bool>? keep = null) where T : Item
    {
        if (!Enabled || gear.Count == 0)
        {
            return;
        }

        var frontPlates = new Dictionary<string, MongoId>(StringComparer.Ordinal);
        var armorWithAttachments = new HashSet<string>(StringComparer.Ordinal);
        if (FilterArmor)
        {
            foreach (var item in gear)
            {
                if (GearCatalog.IsFrontPlate(item.SlotId) && item.ParentId is { } armorId)
                {
                    frontPlates.TryAdd(armorId, item.Template);
                }
                if (GearCatalog.IsBodyArmor(item.Template, templates, items) || GearCatalog.Grade(item.Template, templates) > 0)
                {
                    armorWithAttachments.Add(item.Id.ToString());
                }
            }
        }

        var rejected = gear.Where(item => keep?.Invoke(item) != true && !Allowed(item)).Select(item => item.Id);
        RemoveBranches(gear, rejected);

        bool Allowed(Item item)
        {
            var id = item.Template;
            if (templates.Items.GetValueOrDefault(id)?.Properties?.QuestItem == true)
            {
                return true;
            }
            if (FilterWeapons && items.IsOfBaseclass(id, BaseClasses.WEAPON))
            {
                return weaponIds.Contains(id.ToString()) || weaponCategories.Any(category => items.IsOfBaseclass(id, category));
            }
            if (!FilterArmor)
            {
                return true;
            }

            var grade = GearCatalog.BuiltInGrade(id, templates);
            var bodyArmor = GearCatalog.IsBodyArmor(id, templates, items);
            if (grade == 0 && !bodyArmor)
            {
                return true;
            }
            // Installed parts go with the armor. A loose plate in the armor's storage grid is separate loot.
            if (!bodyArmor && item.ParentId is { } parent && armorWithAttachments.Contains(parent)
                && item.SlotId is { } slot && !slot.Equals("main", StringComparison.OrdinalIgnoreCase) && item.Location is null)
            {
                return true;
            }
            if (frontPlates.TryGetValue(item.Id.ToString(), out var plate))
            {
                grade = GearCatalog.Grade(plate, templates);
            }

            return armorIds.Contains(id.ToString()) || armorClasses.Contains(grade);
        }
    }

    internal static void RemoveBranches<T>(List<T> inventory, IEnumerable<MongoId> roots) where T : Item
    {
        var removed = roots.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0)
        {
            return;
        }

        var children = inventory.Where(item => item.ParentId is not null).ToLookup(item => item.ParentId!);
        var pending = new Queue<string>(removed);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var item in children[parent])
            {
                var id = item.Id.ToString();
                if (removed.Add(id))
                {
                    pending.Enqueue(id);
                }
            }
        }
        inventory.RemoveAll(item => removed.Contains(item.Id.ToString()));
    }
}
