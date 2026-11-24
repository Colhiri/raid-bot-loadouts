using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using EFT.UI.Matchmaker;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace RaidBotLoadouts;

[BepInPlugin(LoadoutProtocol.ModId, "Raid Bot Loadouts", LoadoutProtocol.Version)]
public sealed class LoadoutPlugin : BaseUnityPlugin
{
    private const int RowHeight = 25;
    private static readonly Color PanelColor = new(0.08f, 0.10f, 0.12f, 1f);
    private static MatchmakerOfflineRaidScreen? preRaidScreen;
    private ConfigEntry<KeyboardShortcut> shortcut = null!;
    private Harmony harmony = null!;
    private CanvasGroup? blockedScreen;
    private bool previousRaycastSetting;
    private bool open;
    private Task<EditorReply>? loading;
    private Task<SaveReply>? saving;
    private EditorReply? editor;
    private string? error;
    private RoleLoadout? selectedRole;
    private string selectionTitle = "";
    private bool pickingWeapon;
    private bool pickingModel;
    private string search = "";
    private GearChoice[] modelMatches = [];
    private Vector2 scroll;

    private void Awake()
    {
        shortcut = Config.Bind("Menu", "Open loadout editor", new KeyboardShortcut(KeyCode.F10),
            "Open the bot loadout editor on the pre-raid settings screen.");
        harmony = new Harmony(LoadoutProtocol.ModId);
        var show = AccessTools.GetDeclaredMethods(typeof(MatchmakerOfflineRaidScreen))
            .Single(method => method.Name == nameof(MatchmakerOfflineRaidScreen.Show) && method.GetParameters().Length == 1);
        harmony.Patch(show, postfix: new HarmonyMethod(typeof(LoadoutPlugin), nameof(OnPreRaidScreenShown)));
        Logger.LogInfo("Raid Bot Loadouts is ready.");
    }

    private static void OnPreRaidScreenShown(MatchmakerOfflineRaidScreen __instance) => preRaidScreen = __instance;

    private static bool OnPreRaidScreen => preRaidScreen != null && preRaidScreen.gameObject.activeInHierarchy;

    private void Update()
    {
        if (loading is { IsCompleted: true } load)
        {
            editor = Result(load);
            error = editor?.Error ?? (editor?.Settings is null ? "Серверная часть мода недоступна." : null);
            loading = null;
        }

        if (saving is { IsCompleted: true } save)
        {
            var reply = Result(save);
            saving = null;
            if (reply?.Saved == true)
            {
                Close();
            }
            else
            {
                error = reply?.Error ?? "Сохранение не подтверждено сервером. Попробуй ещё раз.";
            }
        }

        if (!OnPreRaidScreen)
        {
            if (open)
            {
                Close();
            }
            return;
        }

        if (saving is null && shortcut.Value.IsDown())
        {
            if (open) Close();
            else Open();
        }
    }

    private T? Result<T>(Task<T> request) where T : class
    {
        if (request.Status == TaskStatus.RanToCompletion)
        {
            return request.Result;
        }

        Logger.LogWarning(request.Exception?.GetBaseException().Message ?? "Loadout request cancelled.");
        return null;
    }

    private void Open()
    {
        open = true;
        blockedScreen = preRaidScreen!.GetComponent<CanvasGroup>() ?? preRaidScreen.gameObject.AddComponent<CanvasGroup>();
        previousRaycastSetting = blockedScreen.blocksRaycasts;
        blockedScreen.blocksRaycasts = false;
        BeginLoad();
    }

    private void BeginLoad()
    {
        editor = null;
        error = null;
        selectedRole = null;
        loading ??= Load();
    }

    private void Close()
    {
        open = false;
        if (blockedScreen != null)
        {
            blockedScreen.blocksRaycasts = previousRaycastSetting;
            blockedScreen = null;
        }
    }

    private void OnDestroy()
    {
        Close();
        harmony?.UnpatchSelf();
    }

    private static async Task<EditorReply> Load() => JsonConvert.DeserializeObject<EditorReply>(
        await RequestHandler.GetJsonAsync(LoadoutProtocol.Editor)) ?? new EditorReply();

    private static async Task<SaveReply> Save(LoadoutSettings settings) => JsonConvert.DeserializeObject<SaveReply>(
        await RequestHandler.PostJsonAsync(LoadoutProtocol.Save, JsonConvert.SerializeObject(new { Settings = settings }))) ?? new SaveReply();

    private void OnGUI()
    {
        if (!OnPreRaidScreen)
        {
            return;
        }

        var previousMatrix = GUI.matrix;
        var previousDepth = GUI.depth;
        var previousColor = GUI.color;
        var scale = Mathf.Clamp(Screen.height / 880f, 0.75f, 1.6f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        GUI.depth = -10;
        try
        {
            if (open)
            {
                var height = selectedRole is null ? 258 : 512;
                var bounds = new Rect((Screen.width / scale - 650) / 2, (Screen.height / scale - height) / 2, 650, height);
                GUI.color = PanelColor;
                GUI.DrawTexture(bounds, Texture2D.whiteTexture);
                GUI.color = previousColor;
                GUI.ModalWindow(LoadoutProtocol.ModId.GetHashCode(), bounds, DrawEditor, "Экипировка ботов · следующий рейд");
            }
            else if (GUI.Button(new Rect(22, 76, 206, 32), $"Экипировка ботов  [{shortcut.Value.MainKey}]"))
            {
                Open();
            }
        }
        finally
        {
            GUI.matrix = previousMatrix;
            GUI.depth = previousDepth;
            GUI.color = previousColor;
        }
    }

    private void DrawEditor(int _)
    {
        GUILayout.Space(8);
        if (editor?.Settings is { } settings)
        {
            var previousEnabled = GUI.enabled;
            GUI.enabled = saving is null;
            DrawRow("Группа", () => GUILayout.Label("Оружие"), () => GUILayout.Label("Бронежилет / бронеразгрузка"));
            DrawRole("Дикие", settings.Scavs);
            DrawRole("ЧВК", settings.Pmcs);
            DrawRole("Боссы", settings.Bosses);
            GUILayout.Space(5);
            settings.Ushankas = GUILayout.Toggle(settings.Ushankas, $"Ушанки у всех ботов · {(settings.Ushankas ? "ВКЛ" : "ВЫКЛ")}");
            settings.FilterRaidLoot = GUILayout.Toggle(settings.FilterRaidLoot,
                $"Только выбранное оружие и броня в рейде · {(settings.FilterRaidLoot ? "ВКЛ" : "ВЫКЛ")}");
            GUILayout.Label("С фильтром пустые поля используют выбор других групп.");
            if (selectedRole is not null)
            {
                DrawChoices();
            }
            GUI.enabled = previousEnabled;
        }
        else
        {
            GUILayout.Label(loading is null ? "Каталог недоступен." : "Загрузка оружия и брони…");
        }

        GUILayout.FlexibleSpace();
        if (error is not null)
        {
            GUILayout.Label(error);
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = saving is null;
        if (GUILayout.Button("Закрыть", GUILayout.Width(116), GUILayout.Height(28)))
        {
            Close();
        }
        GUILayout.FlexibleSpace();
        if (editor?.Settings is { } edited && GUILayout.Button(saving is null ? "Сохранить" : "Сохраняю…", GUILayout.Width(144), GUILayout.Height(28)))
        {
            error = null;
            saving = Save(edited);
        }
        else if (editor?.Settings is null && loading is null && GUILayout.Button("Повторить", GUILayout.Width(144), GUILayout.Height(28)))
        {
            BeginLoad();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    private static void DrawRow(string title, Action left, Action right)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, GUILayout.Width(78), GUILayout.Height(25));
        GUILayout.BeginVertical(GUILayout.Width(266));
        left();
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUILayout.Width(266));
        right();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private void DrawRole(string title, RoleLoadout role) => DrawRow(title,
        () => PickButton(title, role, weapon: true), () => PickButton(title, role, weapon: false));

    private void PickButton(string title, RoleLoadout role, bool weapon)
    {
        var id = weapon ? role.WeaponCategory ?? role.Weapon : role.Armor;
        var options = weapon && role.WeaponCategory is not null ? editor!.WeaponCategories : Models(weapon);
        var label = !weapon && role.ArmorClass is { } grade ? $"Класс {grade}" : id is null ? "По умолчанию"
            : options.FirstOrDefault(item => item.Id == id)?.Name ?? "Предмет недоступен";
        if (!GUILayout.Button(new GUIContent(label, label), GUILayout.Height(25)))
        {
            return;
        }

        selectedRole = role;
        pickingWeapon = weapon;
        pickingModel = weapon ? role.Weapon is not null : role.Armor is not null;
        selectionTitle = $"{title} · {(weapon ? "оружие" : "броня")}";
        ResetSearch();
        GUIUtility.ExitGUI();
    }

    private GearChoice[] Models(bool weapon) => weapon ? editor!.Weapons : editor!.Armor;

    private void ResetSearch()
    {
        search = "";
        modelMatches = Models(pickingWeapon);
        scroll = Vector2.zero;
    }

    private void DrawChoices()
    {
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        GUILayout.Label(selectionTitle, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("По умолчанию", GUILayout.Width(128)))
        {
            Select(null);
        }
        GUILayout.EndHorizontal();
        var mode = GUILayout.Toolbar(pickingModel ? 1 : 0, [pickingWeapon ? "Категория" : "Класс брони", "Модель"]);
        if (pickingModel != (mode == 1))
        {
            pickingModel = mode == 1;
            ResetSearch();
            GUIUtility.ExitGUI();
        }

        if (!pickingModel)
        {
            DrawGroups();
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Поиск", GUILayout.Width(62));
        var query = GUILayout.TextField(search, maxLength: 100, GUILayout.ExpandWidth(true));
        if (query != search)
        {
            search = query;
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(SearchKey).Where(term => term.Length > 0).ToArray();
            modelMatches = terms.Length == 0 ? Models(pickingWeapon) : Models(pickingWeapon).Where(item =>
            {
                var key = SearchKey(item.Name + " " + item.SearchText);
                return terms.All(term => key.Contains(term, StringComparison.Ordinal));
            }).ToArray();
            scroll = Vector2.zero;
        }
        GUILayout.Label(modelMatches.Length.ToString(), GUILayout.Width(36));
        GUILayout.EndHorizontal();

        var area = GUILayoutUtility.GetRect(0, 170, GUILayout.ExpandWidth(true));
        var content = new Rect(0, 0, Mathf.Max(1, area.width - 20), Mathf.Max(area.height - 1, modelMatches.Length * RowHeight));
        scroll = GUI.BeginScrollView(area, scroll, content, false, true);
        for (var index = 0; index < modelMatches.Length; index++)
        {
            var item = modelMatches[index];
            if (GUI.Button(new Rect(0, index * RowHeight, content.width, RowHeight - 2), item.Name))
            {
                GUI.EndScrollView();
                Select(item.Id);
            }
        }
        if (modelMatches.Length == 0)
        {
            GUI.Label(new Rect(8, 0, content.width - 8, RowHeight), "Ничего не найдено.");
        }
        GUI.EndScrollView();
    }

    private void DrawGroups()
    {
        var choices = pickingWeapon ? editor!.WeaponCategories
            : editor!.ArmorClasses.Select(grade => new GearChoice { Id = grade.ToString(CultureInfo.InvariantCulture), Name = $"Класс {grade}" }).ToArray();
        for (var index = 0; index < choices.Length; index += 2)
        {
            GUILayout.BeginHorizontal();
            for (var column = index; column < Math.Min(index + 2, choices.Length); column++)
            {
                if (GUILayout.Button(choices[column].Name, GUILayout.Width(312), GUILayout.Height(23)))
                {
                    Select(choices[column].Id);
                }
            }
            GUILayout.EndHorizontal();
        }
    }

    private static string SearchKey(string value)
    {
        var key = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                var lower = char.ToLowerInvariant(character);
                key.Append(lower == 'ё' ? 'е' : lower);
            }
        }
        return key.ToString();
    }

    private void Select(string? id)
    {
        if (pickingWeapon)
        {
            selectedRole!.Weapon = pickingModel ? id : null;
            selectedRole.WeaponCategory = pickingModel ? null : id;
        }
        else
        {
            selectedRole!.Armor = pickingModel ? id : null;
            selectedRole.ArmorClass = !pickingModel && id is not null ? int.Parse(id, CultureInfo.InvariantCulture) : null;
        }
        selectedRole = null;
        GUI.FocusControl(null);
        GUIUtility.ExitGUI();
    }
}
