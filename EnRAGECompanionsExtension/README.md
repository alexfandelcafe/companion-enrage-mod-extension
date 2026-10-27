# EnRAGE Companions Extension — prototype

This project is a **separate ScriptHookRDR2DotNet V2 script** intended to integrate with the runtime internals of `NemesisSystem.NemesisMod`. It does not compile against `NemesisMod.dll`; it finds the loaded type and calls its private methods/fields through reflection, then uses Harmony to patch the relevant methods in memory.

## What this version attempts

- Re-activates the existing enRAGE companion update cycle by patching `UpdateCompanion` and calling enRAGE's own `UpdateAsk`, `WaitToJoin`, `Ride`, and `GoingHome` methods.
- Keeps enRAGE's existing recruitment prompt, with ordinary nearby NPCs added as fallback candidates when its original scan did not pick someone.
- Intercepts `Ask(Ped, string, Ped)` for extension-only keys, avoiding the original `CampPeople[key]` lookup.
- Creates enRAGE's private `Companion` object and calls enRAGE's own `Adopt` and `Setup` methods.
- Allows eligible ordinary NPCs and enRAGE live-camp peds. A live-camp ped is removed from the matching internal camp list before adoption. Other enRAGE-managed roles are excluded in this prototype because their controllers need role-specific handoff logic.
- Intercepts `SpawnCompanion` for extension recruits so the extension can respawn the original model by its stored model hash rather than trying to reverse-hash a model-name string.
- Optionally retains the player's relationship group after dismissal.

## Build requirements

- Windows + Visual Studio 2022 (or a compatible MSBuild installation) with the .NET Framework 4.8 targeting pack.
- The exact `ScriptHookRDRNetAPI.dll` from the user's installed ScriptHookRDR2DotNet V2 setup.
- NuGet restore access for `Lib.Harmony` 2.4.3.

Set `RDR2GameDir` to the root folder of the game, then build:

```powershell
msbuild .\EnRAGECompanionsExtension.csproj /p:Configuration=Release /p:RDR2GameDir="D:\\Games\\Red Dead Redemption 2"
```

Copy `EnRAGECompanionsExtension.dll` and the copied `0Harmony.dll`/Harmony assembly from the build output into the game's `scripts` folder. Keep the ScriptHook RDR2 .NET API files from the same installed runtime build; do not copy another API version from this project. ScriptHookRDRNetAPI.dll is normally in the game root beside RDR2.exe, while script DLLs go in `scripts/`.

## Configuration

The first launch creates `scripts/EnRAGECompanionsExtension.ini`:

```ini
MaxCompanions=3
KeepPlayerSideAfterDismissal=false
PersistentNemesisGangId=
```

- `MaxCompanions`: total active-companion limit, clamped to 1–7. The extension also checks enRAGE's original story-companion recruitment path against this configured cap.
- `KeepPlayerSideAfterDismissal`: when `true`, the relationship group is set back to the player's current relationship group after the original companion restoration routine runs. This is a live game relationship change.
- `PersistentNemesisGangId`: optional enRAGE gang ID (for example `vdl`) to write into an existing non-story `NemesisRecord` at recruitment. This does not create records for random pedestrians and is not a guarantee of simulated warfare membership; enRAGE's `vdl` entry is story-only. Leave blank unless you deliberately want roster-level changes. `KeepPlayerSideAfterDismissal=true` is also needed if the recruited live ped should remain allied after you dismiss it.

## Important verification status

This is a **first implementation draft, not a tested binary**. The available environment did not include a C# compiler or the user's installed game API DLL, so compilation and in-game execution could not be verified here. The target names and signatures were checked against the supplied decompiled `NemesisMod.cs`; the actual installed `NemesisMod.dll` may differ. The log file is `scripts/EnRAGECompanionsExtension.log`.

Test first with a backup of saves and the INI set to `KeepPlayerSideAfterDismissal=false`. Check that a normal civilian can be recruited, follows into combat, is removed correctly on dismissal, and that an NPC from an enRAGE camp no longer gets re-claimed by that camp. The roster-level migration of a NemesisRecord to a persistent new gang is deliberately not done in this prototype; it should be added only after confirming how the installed enRAGE build saves and re-evaluates `NemesisRecord.Gang`, `CampId`, and `BossId`.
