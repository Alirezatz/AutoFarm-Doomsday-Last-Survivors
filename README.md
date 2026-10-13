# Festical Auto Farm

Festical Auto Farm is a .NET 6 / x64 MelonLoader mod for **Doomsday: Last
Survivors**. It scans locally loaded world-map resource nodes, lets you
configure resource amounts and save squads, and can dispatch and track resource
gathering marches. Press **F8** to open or close the mod panel.

> **Warning:** Starting the farm sends real in-game marches and uses troops.
> Stop the farm to prevent additional dispatches; marches already sent are not
> recalled.

## Installation

1. Install MelonLoader if it is not installed yet. Use the
   [MelonLoader project](https://github.com/lavagang/melonloader) for its
   installer and setup instructions.
2. Start the game once after installing MelonLoader. The first launch may take
   longer while the loader initializes and creates its files.
3. Close the game completely.
4. Download
   [AutoFarm-Doomsday-Last-Survivors-v1.0.0.zip from the v1.0.0 release](https://github.com/Alirezatz/AutoFarm-Doomsday-Last-Survivors/releases/tag/v1.0.0).
   The ZIP is a release download, not a file in the source repository.
5. Extract the ZIP directly into the game's `Mods` folder. Make sure
   `Festical Auto Farm.dll` is directly inside `Mods`, not inside an extra
   nested folder.
6. Launch the game and check the MelonLoader log for:

   - `Festical Auto Farm v1.0.0 loaded. Press F8 to toggle the panel.`

Once loaded, press **F8** to open or close the panel.

## Using the farm

1. Open the panel with **F8**. The mod scans the initially loaded world-map
   area automatically; use **Scan + write log** on the map view to refresh the
   saved area when needed.
2. In **Farm setup**, enable the resource slots you want and set the requested
   amounts.
3. In **Army setup**, use **Load Squad** for each enabled slot. The game opens
   its deployment panel; select heroes and troops, then use the game's
   **March** button to save that squad.
4. Select **Start farm** to dispatch the saved squads, or **Stop** to prevent
   further dispatches.

The mod uses the saved map snapshot while farming, avoids reusing active
marches, and bounds requests by game capacity and node stock. The game remains
authoritative for march validation and acceptance.

## Build from source

Building requires the **.NET 6 SDK** and a local Doomsday: Last Survivors
installation with MelonLoader initialized. The project references MelonLoader
dependencies from `MelonLoader\net6` and game/Unity assemblies from
`MelonLoader\Il2CppAssemblies`.

From the project directory (the one containing the `.csproj` file), run:

```powershell
dotnet build ".\Festical Auto Farm.csproj" -c Release
```

By default, the project expects the Steam installation at
`C:\Program Files (x86)\Steam\steamapps\common\Doomsday Last Survivors`. To use
a different game directory, pass its path with `GameDir`:

```powershell
dotnet build ".\Festical Auto Farm.csproj" -c Release -p:GameDir="D:\Games\Doomsday Last Survivors"
```

When the game's `Mods` directory exists, a successful build deploys the DLL
there automatically. To build without deploying:

```powershell
dotnet build ".\Festical Auto Farm.csproj" -c Release -p:DeployToMods=false
```

## Releases

Installable ZIP files belong in GitHub Releases and are not committed to the
source repository. The
[v1.0.0 release](https://github.com/Alirezatz/AutoFarm-Doomsday-Last-Survivors/releases/tag/v1.0.0)
contains `AutoFarm-Doomsday-Last-Survivors-v1.0.0.zip` with
`Festical Auto Farm.dll` at its root so it extracts directly into the game's
`Mods` folder.

## Community

Join the [Doomsday FT Telegram group](https://t.me/DoomsdayFTGroup).
