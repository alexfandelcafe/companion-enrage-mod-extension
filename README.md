# EnRAGE Companions Extension — prototype

This project is a **separate ScriptHookRDR2DotNet V2 script** intended to integrate with the runtime internals of `NemesisSystem.NemesisMod`. It does not compile against `NemesisMod.dll`; it finds the loaded type and calls its private methods/fields through reflection, then uses Harmony to patch the relevant methods in memory.

## What this version attempts

- Re-activates the existing enRAGE companion update cycle by patching `UpdateCompanion` and calling enRAGE's own `UpdateAsk`, `WaitToJoin`, `Ride`, and `GoingHome` methods.
- Keeps enRAGE's original story-companion prompt unchanged. Generic NPCs get a separate native prompt created through enRAGE's own `MakePrompt` method. When enRAGE's `UpdateSocial` would show its $5/hat-tip interaction for the same recruitable NPC, the extension suppresses that social prompt and prioritizes `Cabalga conmigo`.
- Routes generic recruitment directly to `RecruitGeneric`; it does not write a fake key into `_askPed`/`_askKey` and recursively call the story-only `UpdateAsk` method.
- Intercepts `Ask(Ped, string, Ped)` for extension-only keys, avoiding the original `CampPeople[key]` lookup.
- Creates enRAGE's private `Companion` object and calls enRAGE's own `Adopt` and `Setup` methods.
- Adds each recruited pedestrian to enRAGE's persistent `Roster` under the faction configured by `RecruitGangId`. The default is Van der Linde (`vdl`): because this built-in faction is story-only and uses a separate `StoryRoster` for its F11 view, the extension mirrors `vdl` records into `StoryRoster`. Existing ordinary/custom factions use the normal `Roster` list filtered by `Gang`; the extension does not create the faction itself, so its ID must already exist in enRAGE. Existing records are reused; new `NemesisRecord` objects are created directly with the required roster fields to avoid `NewRecord` index errors for models/factions with empty name arrays.
- Marks the recruited roster record as dead if the tracked pedestrian dies during following or after dismissal while that entity is still loaded in the current session.
- Changes the recruited companion's interaction from `Mandar al campamento` to `Dejar de seguirme`. Dismissing an extension recruit stops the active follow state but does not remove the persistent roster record. The extension also prevents the original recruitment location from being treated as a real camp.
- Allows eligible ordinary **male** NPCs and enRAGE live-camp peds. Candidate detection and the final recruitment entry point both check the model-gender helper (`NemesisSystem.Models.Female`); female models never receive the recruitment prompt and cannot be recruited through the extension. A live-camp ped is removed from the matching internal camp list before adoption. Other enRAGE-managed roles are excluded in this prototype because their controllers need role-specific handoff logic.
- Intercepts `SpawnCompanion` for extension recruits so the extension can respawn the original model by its stored model hash rather than trying to reverse-hash a model-name string.
- Keeps dismissed recruits friendly to the player using both the active player relationship group and the canonical `PLAYER` group, reasserted during session maintenance. The extension resets their enRAGE `Hate`/`Fear` values and blocks hostile ambient event reactions while preserving their explicitly assigned wander task, reducing retaliatory behavior if the player stares at or bumps into them.
- After dismissal, detaches the enRAGE wrapper without deleting the pedestrian, clears companion/group tasks, restores a friendly player-side relationship independent of the roster faction, and issues an ambient wander task. The released NPC keeps its explicit wander task while `BlockPermanentEvents=true` suppresses ambient anger/retaliation events; if he was mounted, the extension asks him to dismount before starting to wander. Recruits remain damageable and can die.
- Suppresses enRAGE's `$5`, drink, hat-tip, and handshake social prompt for any recruited pedestrian still mapped to its roster record, regardless of whether the recruit is currently following the player. The ordinary social prompt resumes for unrelated NPCs.
- Suppresses enRAGE's interrogation eligibility and clears its short-lived cached prompt for a recruited NPC, so `Interrogar` does not replace the companion interaction. The companion prompt for extension recruits reads `Dejar de seguirme`; selecting it dismisses follow behavior but retains the roster entry.
- Adds `Llamarlo` to the existing character-sheet action list for extension roster records (`extcomp_` IDs, legacy `extvdl_` IDs, and records cached as recruits). Before spawning anything, it searches the exact roster-record-to-ped cache and enRAGE's nearby `PedSnapshot`/`RecordAt` data; if that same living pedestrian still exists, it re-adopts that entity and does not clone another NPC. It only uses the existing spawn/setup path when no matching live entity can be found. The companion horse is created through `Setup`, and the member is ordered to mount and ride toward the player. The action is available for living members only and respects `MaxCompanions`.
- Reapplies the roster name to the RDR2 native focus/aim prompt (`_SET_PED_PROMPT_NAME`) after both recruitment and re-adoption. It uses enRAGE's persistent-string helper and registers the same name in enRAGE's `_named` table with `Met=true`, preventing its own name-resolution path from falling back to an unrelated alias or `a stranger`.
- Replaces the stock broad hated-target combat loop for extension recruits only. They remain passive until a nearby pedestrian is actually in combat with the player; merely hostile or passing NPCs are not selected. Surrendered, fleeing, downed, hogtied, ragdolling, writhing, or knocked-out NPCs are excluded. enRAGE's original story companions keep their original combat behavior.
- Overrides enRAGE's random starting `Hate` value after profile generation, setting recruited roster members to `Hate=0` and `Fear=0`, setting an optional `Like` score to 100 if that build exposes one, and attempting to set `Disposition=Friendly` when writable. A maintenance pass keeps the profile values at that level; the native player/recruit group relation remains `Companion` in both directions.

## Build requirements

- Windows + Visual Studio 2022 (or a compatible MSBuild installation) with the .NET Framework 4.8 targeting pack.
- The exact `ScriptHookRDRNetAPI.dll` from the user's installed ScriptHookRDR2DotNet V2 setup.
- NuGet restore access for `Lib.Harmony` 2.4.3.

The game root defaults to `D:\Red Dead Redemption 2` (the script checks that `ScriptHookRDRNetAPI.dll` exists there). Build from a Visual Studio Developer PowerShell:

```powershell
.\build.ps1
```

Copy `EnRAGECompanionsExtension.dll` and the copied `0Harmony.dll`/Harmony assembly from the build output into the game's `scripts` folder. Keep the ScriptHook RDR2 .NET API files from the same installed runtime build; do not copy another API version from this project. ScriptHookRDRNetAPI.dll is normally in the game root beside RDR2.exe, while script DLLs go in `scripts/`.

## Configuration

The first launch creates `EnRAGECompanionsExtension.ini` in the active ScriptHook `scripts` directory. The extension detects whether `AppDomain.BaseDirectory` already ends in `scripts`, avoids creating `scripts/scripts`, and migrates its previous nested INI/log files when possible:

```ini
MaxCompanions=3
RecruitGangId=vdl
RecruitRosterId=vdl
AllowFriendlyFire=true
PreventTargeting=false
EnableAffinity=true
AffinityTravelIntervalSeconds=300
AffinityTravelGain=1
AffinityCombatGain=2
AffinityTreatmentGain=3
EnableAppearancePersistence=true
PreserveSavedOutfit=true
EnableIndividualOrders=true
EnableWaitMode=true
WaitRadius=8.0
RestDurationSeconds=60
```

- `MaxCompanions`: total active-companion limit, clamped to 1–7. The extension also checks enRAGE's original story-companion recruitment path against this configured cap.
- `RecruitGangId`: the **existing** enRAGE faction ID assigned to new recruited roster records. Default: `vdl` (Van der Linde). This setting does not create a new enRAGE faction; custom factions must already be defined/loaded by enRAGE.
- `RecruitRosterId`: for normal/custom factions, set this to the same ID as `RecruitGangId` so roster actions (notably expulsion) apply to that faction. Only when both keys are `vdl` does the extension use the special built-in Van der Linde `StoryRoster`; other factions appear in enRAGE's normal faction roster through `Roster` records whose `Gang` equals the configured ID. The legacy `PersistentNemesisGangId` key remains accepted as an alias for `RecruitGangId`.
- `AllowFriendlyFire` and `PreventTargeting`: retain their existing damage/targeting behavior settings.
- `EnableAffinity`: displays the companion's affinity meter and enables affinity changes.
- `AffinityTravelIntervalSeconds`: seconds between travel checks (clamped to 30–7200). Travel gain is awarded when the companion remains nearby and the player has moved at least 100 metres during the interval.
- `AffinityTravelGain`, `AffinityCombatGain`, `AffinityTreatmentGain`: gains for travel, combat (at most once per 30 seconds of an encounter), and a successful paid treatment. Affinity is clamped to 0–100. The detail sheet also exposes explicit `Mejorar afinidad (+10)` and `Reducir afinidad (-10)` actions for controlled testing.
- `EnableAppearancePersistence` and `PreserveSavedOutfit`: capture and reapply a saved meta-ped outfit hash when RDR2 exposes a non-zero hash. Recalled NPCs in the same session are re-adopted as the same pedestrian whenever the runtime cache still has that entity.
- `EnableIndividualOrders`: enables contextual order prompts when aiming at an active extension companion. The prompts are separate from `Dejar de seguirme`; the roster detail sheet no longer contains individual orders.
- `EnableWaitMode`: controls `Quédate aquí`; when enabled the companion returns to the saved anchor if it exceeds `WaitRadius`, then remains there instead of running enRAGE's default follow task.
- `WaitRadius`: radius in metres around the saved wait/rest position (clamped to 1–50 m; default 8).
- `RestDurationSeconds`: timed rest before automatic return to follow mode and full-health recovery (clamped to 10–3600 seconds; default 60). The timer and resting state are stored by stable roster ID.
- `KeepPlayerSideAfterDismissal`: deprecated. Dismissed extension recruits stay on the player's friendly relationship side regardless of their enRAGE faction.

## Scope and verification

The roster integration now supports any already-defined enRAGE faction ID. New recruits receive an `extcomp_` ID and remain ordinary character sheets (`Story=false`); the old `extvdl_` prefix is still recognized for saves created by previous versions. When `RecruitGangId=RecruitRosterId=vdl`, records are also mirrored to the special Van der Linde `StoryRoster` and re-synchronized after loading. When a custom/normal faction ID is configured for both settings, records stay in the normal `Roster` and show in that faction's page because their `Gang` field matches its ID. The extension does not create the faction definition itself. Records are seeded through enRAGE's `RollNewLife`/`EnsureMind`/`EnsurePast` path and generated past entries are marked known. A per-session ped-handle cache and record matching prevent repeated recruitment from creating a new alias/duplicate entry. If a companion dies while its follow state is tracked, its record is marked dead. After a full game restart the original live entity cannot be recovered from runtime cache; `Llamarlo` uses the saved model hash to create a replacement only when no matching live entity exists.

The `Llamarlo` roster action and the direct proximity hold prompt use the existing stable roster ID. A dismissed recruit can be re-adopted from nearby without opening the roster, provided that the original pedestrian still exists in the session; the extension reuses the roster record and enforces the active-companion cap. This change is implemented at source level and still needs in-game verification.

The native focus label and enRAGE roster name are set by separate systems. This revision updates both. Base-game voiced greeting lines and their built-in stranger/persona behavior are not rewritten; those are controlled by RDR2's dialogue system.

This is source code, not a compiled or tested binary. The available environment did not include a C# compiler or the user's installed game API DLL, so compilation and in-game execution could not be verified here. The relevant signatures were checked against the supplied decompiled `NemesisMod.cs`; the installed `NemesisMod.dll` may differ. Keep a backup of the save before testing changes to the roster. The INI and log are stored in the active ScriptHook `scripts` directory, not `scripts/scripts`.


## Latest additions: ambient dismissal, medicine, and roster expulsion

- `Dejar de seguirme` now preserves the released pedestrian entity instead of letting enRAGE's `Cleanup` delete a spawned replacement. It clears the companion wrapper and blip, removes the NPC from the player group, and gives the pedestrian a standard wander task. If mounted, it requests a dismount and starts wandering once on foot. The recruit remains associated with the saved roster record for possible later recall.
- To treat an extension recruit, aim directly at the living NPC in the world and hold the contextual `Dar remedio ($1)` prompt. The prompt is not part of the roster sheet. It deducts exactly 100 cents from the game cash balance and sets that exact targeted ped's current health to `MaxHealth`; it refuses to charge if the ped is no longer valid or is already at full health. Expelled roster IDs are excluded from target discovery and the treatment handler, so an expelled NPC standing nearby cannot receive the medicine.
- Outfit hashes are now also stored in `EnRAGECompanionsExtension.companions.json` under the stable roster record ID; the legacy `outfits.txt` remains supported. Capture runs before `Adopt` and before dismissal cleanup can touch the entity, and restoration runs before/after enRAGE's setup where appropriate. Same-session recall reuses the original pedestrian when available, preserving its actual clothing even when the outfit-hash native returns zero. For a replacement spawned after a game restart, a zero/unavailable hash still prevents reliable full-outfit restoration; the log records this limitation rather than fabricating a hash.
- Roster members in the Van der Linde story sheet and regular `Roster` records belonging to the configured `RecruitGangId`/`RecruitRosterId` can have `Echar de la banda`, including custom/new non-story records regardless of ID prefix. A member who is currently following the player as an active companion cannot be expelled; dismiss the companion first. A dismissed extension recruit can then be recalled with `Llamarlo` or permanently expelled. The first selection shows a warning that expulsion cannot be undone and may have future consequences; `Confirmar expulsión` commits the change and `Cancelar expulsión` abandons it; leaving the sheet with Back also cancels the pending confirmation. Expulsions are kept in `EnRAGECompanionsExtension.exiled-story-roster.txt` under the active `scripts` directory so the story-roster sync does not automatically add them back. The persistent file is not meant to be edited during ordinary gameplay.
- The old `EnRAGECompanionsExtension.pending-heals.txt` file may remain from an earlier source revision, but the current contextual prompt does not queue remote cures: it only treats the live NPC currently being aimed at.

These additions are implemented at source level only. The current environment does not include a compiler or the installed ScriptHook API DLL, so a local build and in-game test are still required.


## Latest interaction corrections (source-level)

- Medicine target detection no longer requires `IS_PLAYER_FREE_AIMING`; it checks both free-aim targeting and lock-on targeting and uses enRAGE's current social target as a fallback. Its prompt uses a distinct control (`-2074855166`, already used by enRAGE's paper interaction prompt) so it does not compete for the same button as `Saludar` or `Dejar de seguirme`.
- `Llamarlo` is not added to a roster record while an extension companion with that same record ID is active. Active followers of any type are protected from `Echar de la banda`; dismissed records are eligible for expulsion.
- Expulsion recognizes any record actually present in `StoryRoster`, regardless of ID prefix, and regular `Roster` records whose `Gang` matches `RecruitGangId` or `RecruitRosterId`; dead entries can be removed too. It also allows dismissed extension recruits, but prevents expulsion while any roster member is actively following in the companion system.
- Recruiting a dismissed recruit cancels the stored ambient wander timer and clears the previous wander task before calling enRAGE's `Adopt`, so `Setup` can install its companion-follow behavior without the old task continuing to run.
- Only male generic NPCs qualify for `Cabalga conmigo`. The extension resolves the same `NemesisSystem.Models.Female(uint)` helper used by enRAGE; if that helper cannot be resolved, recruitment fails closed rather than accidentally allowing a female character.
- Dismissed recruits keep their friendly relationship group and zero-hate roster profile. `BlockPermanentEvents` is enabled while their standard wander task runs, to prevent stare/bump events from turning them against the player; actual attacks against the player are interrupted and replaced with wandering. This is intended to preserve their roaming behavior without making them invulnerable.

These are source changes only; they still require building against the user's installed ScriptHook API and in-game testing.


## 13. Compatibility with existing custom enRAGE factions

`RecruitGangId` is validated against enRAGE's `NemesisSystem.Gangs.ById`; `RecruitRosterId` should be set to the same faction ID for normal/custom factions. The default for both remains `vdl`, preserving the Van der Linde setup. For another faction, configure both values with that faction's exact ID, for example:

```ini
RecruitGangId=custom_faction_id
RecruitRosterId=custom_faction_id
```

The example is a placeholder: replace `custom_faction_id` with the exact ID of the faction you want to use. The faction must already be present and loaded in enRAGE; this extension does not create factions or edit enRAGE's faction definitions. Regular/custom factions use ordinary `Roster` records whose `Gang` is the chosen ID; the special `StoryRoster` mirroring remains enabled only when both values are `vdl`. Roster expulsion tests both configured IDs, clears the member's affiliation generically, and persists the exile by record ID. New extension sheets use `extcomp_`; legacy `extvdl_` sheets continue to be recognized. The default relationship group used to keep dismissed recruits friendly to the player is independent of the chosen faction ID.

**Verification status:** source-level change only. No C# compiler, `ScriptHookRDRNetAPI.dll`, or in-game runtime is available in this environment; build and test with the exact API used by the installed RDR2/ScriptHook setup.


## Fase 2 — base de estados persistentes

La rama `phase2-persistent-companion-state` añade la base de persistencia versionada para las siguientes fases. No sustituye ni elimina los hooks de reclutamiento existentes.

- `src/CompanionPersistentState.cs`: modelos por ID de registro de enRAGE para seguimiento/espera, preferencia de montura, postura de combate, posición guardada, heridas, afinidad y campos separados de apariencia.
- `src/CompanionStateStore.cs`: carga y guardado JSON versionado, validación de valores, guardado diferido por cambios, archivo temporal y copia de respaldo, y lectura de outfits antiguos.
- `src/CompanionStateRuntime.cs`: inicializa el almacén al arrancar el script sin introducir una referencia a `NemesisMod.dll`.
- `src/EntryPoint.cs`: inicializa el almacén al construir el script.

Archivo de ejecución: `scripts/EnRAGECompanionsExtension.companions.json`. Conserva los datos por el ID persistente del registro, nunca por el handle temporal del ped. El archivo antiguo `scripts/EnRAGECompanionsExtension.outfits.txt` se lee en formato `ID|HASH_HEXADECIMAL`; la migración no lo elimina ni lo reescribe. Los datos antiguos se importan solo cuando el registro nuevo aún no contiene un hash de outfit.

Esta revisión ya conecta la afinidad (barra en la zona de sentimientos de la ficha y cambios por viaje, combate y tratamiento) y la persistencia del hash de vestimenta con los hooks de juego. La base de estado también conserva datos anteriores de postura, preferencia de montura, posición y lesión al migrar el formato antiguo; esos campos no deben considerarse funcionales en el juego solo por existir en el JSON. La persistencia de componentes de ropa, sombrero y armas sigue pendiente porque necesita natives compatibles y verificación dentro de RDR2.

El esquema actual es la versión 1. El formato de desarrollo anterior `{"Version":1,"Records":[...]}` se importa a `SchemaVersion/Companions` sin descartar afinidad, postura, preferencia de montura, posición guardada, lesión, temporizador de descanso ni hashes de apariencia válidos. Los JSON que sí sean ilegibles o incompatibles se apartan con sufijo `.corrupt-<fecha>` para conservarlos y el almacén intenta recuperar los hashes del archivo antiguo. El guardado se realiza al detectar cambios o migración, no en cada frame.


## Fase 3 — órdenes individuales y descanso

La fase 3 reutiliza `CompanionPersistentState` y el ciclo de enRAGE en vez de sustituir el sistema de reclutamiento. Al apuntar a un compañero activo aparecen prompts contextuales:

- `Quédate aquí`: guarda la posición actual del compañero, lo baja del caballo si corresponde y mantiene su IA de espera separada del ciclo `WaitToJoin/Ride`. Si sale del radio configurado, recibe una tarea para regresar al punto guardado.
- `Sígueme`: elimina el anclaje de espera/descanso, restablece el estado persistente a `Following` y devuelve el control de movimiento al ciclo normal de enRAGE.
- `Descansar`: mantiene al compañero en la posición guardada durante `RestDurationSeconds`; al finalizar, limpia el estado de lesión persistente, restaura su salud y reanuda el seguimiento.
- Los prompts de órdenes se mantienen separados de `Dejar de seguirme` y del remedio, y no se agregan botones de órdenes a la ficha. La orden se guarda por ID estable de roster, no por handle del ped; `Llamarlo` vuelve a ponerlo en estado `Following`.
- Los registros nuevos y antiguos de la extensión se normalizan para que el valor por defecto de `Origin` (`Killed`) no invente el antecedente `te mató` cuando `TimesKilledPlayer` es cero. Los registros con un contador real de muertes no se alteran.
- La llamada desde la ficha ya no fuerza una tarea de montar al caballo después de `Adopt`; enRAGE conserva el control de sus tareas de aproximación/seguimiento.

Las órdenes y el temporizador quedan implementados a nivel de código fuente, pero requieren validar en RDR2 la firma de los wrappers nativos de movimiento y el comportamiento del compañero montado. Esta revisión tampoco modifica `main` ni fusiona el PR.
