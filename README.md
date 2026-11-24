# Raid Bot Loadouts

Configure the AI loadouts for Scavs, PMCs, and bosses from the pre-raid screen in SPT 4.1.6. Choose a weapon category and an armor protection class, or pick individual models. The optional ushanka setting replaces AI headwear.

[Инструкция на русском](README.ru.md) · [Downloads](https://github.com/Colhiri/raid-bot-loadouts/releases/latest)

## Install

1. Close the game and the SPT server.
2. Download the release ZIP and extract it beside `EscapeFromTarkov.exe`. Merge the `BepInEx` and `SPT_Runtime` folders.
3. Start the server and the game. Choose a map and continue to the raid settings screen.

This release is for **SPT 4.1.6**. It uses the client and server libraries included with SPT.

## Use

Press **F10** or click **Экипировка ботов** (Bot Loadouts). The interface uses Russian labels.

| In the menu | Meaning |
| --- | --- |
| Дикие / ЧВК / Боссы | Scavs / PMCs / Bosses |
| Категория / Класс брони / Модель | Weapon category / Armor class / Item model |
| Ушанки у всех ботов | Give all AI bots ushankas |
| Только выбранное оружие и броня в рейде | Restrict raid equipment and loot to the combined selection |
| По умолчанию / Сохранить | Default / Save |

Select a category or protection class to get a random matching model. For armor with removable plates, the front plate determines its class. The model list scrolls and accepts Russian and English search terms.

With the filter enabled, ground loot, containers, airdrops and bot inventories use the combined gear selection across the three groups. Unconfigured bots can draw from that selection. An equipment type with no selections uses standard generation. Mounted weapons and quest items are exempt. SPT handles the bot spawns.

Select **Сохранить** (Save) before starting the raid. Settings are per profile. The client exchanges settings and catalog data with your SPT server. The mod has no external service or telemetry.

## Remove

Close the game and server and remove `BepInEx/plugins/RaidBotLoadouts` and `SPT_Runtime/user/mods/RaidBotLoadouts`. The server folder also holds the mod's saved settings.

## Build

Use the .NET 10 SDK and an SPT 4.1.6 installation.

```powershell
dotnet build Client -c Release -p:SptRoot=C:/Games/SPT-4.1.6
dotnet build Server -c Release -p:SptRoot=C:/Games/SPT-4.1.6
```

[MIT License](LICENSE)
