# EnRAGE Companions Extension — prototype

This project is a **separate ScriptHookRDR2DotNet V2 script** intended to integrate with the runtime internals of `NemesisSystem.NemesisMod`. It does not compile against `NemesisMod.dll`; it finds the loaded type and calls its private methods/fields through reflection, then uses Harmony to patch the relevant methods in memory.

## What this version attempts

- Re-activates the existing enRAGE companion update cycle by patching `UpdateCompanion` and calling enRAGE's own `UpdateAsk`, `WaitToJoin`, `Ride`, and `GoingHome` methods.
- Keeps enRAGE's original story-companion prompt unchanged. Generic NPCs get a separate native prompt created through enRAGE's own `MakePrompt` method. When enRAGE's `UpdateSocial` would show its $5/hat-tip interaction for the same recruitable NPC, the extension suppresses that social prompt and prioritizes `Cabalga conmigo`.
- Routes generic recruitment directly to `RecruitGeneric`; it does not write a fake key into `_askPed`/`_askKey` and recursively call the story-only `UpdateAsk` method.
- Intercepts `Ask(Ped, string, Ped)` for extension-only keys, avoiding the original `CampPeople[key]` lookup.
- Creates enRAGE's private `Companion` object and calls enRAGE's own `Adopt` and `Setup` methods.
- Adds each recruited pedestrian to enRAGE's persistent `Roster` under the faction configured by `RecruitGangId`. The default is Van der Linde (`vdl`): because this built-in faction is story-only and uses a separate `StoryRoster` for its F11 view, the extension mirrors `vdl` records into `StoryRoster`. Existing ordinary/custom factions use the normal `Roster` list filtered by `Gang`; the extension does not create the faction itself, so its ID must already exist in enRAGE. Existing records are reused; new `NemesisRecord` objects are created directly with the required roster fields to avoid `NewRecord` index errors for models/factions with empty name arrays.
- Keeps a recruited companion's roster identity when the live ped dies: the record is retained as persistently incapacitated, and enRAGE's automatic replacement spawn is blocked until the player explicitly uses the recovery prompt.
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
EnableIndividualOrders=true
EnableWaitMode=true
WaitRadius=8.0
EnableInjurySystem=true
InjuryDamageThresholdPercent=10
InjuryRecoverySeconds=180
IncapacitatedHealthPercent=35
MedicineHealingPercent=100
MedicineRecoveryReductionSeconds=60
RecruitMedicineCostCents=100
```

- `MaxCompanions`: total active-companion limit, clamped to 1–7. The extension also checks enRAGE's original story-companion recruitment path against this configured cap.
- `RecruitGangId`: the **existing** enRAGE faction ID assigned to new recruited roster records. Default: `vdl` (Van der Linde). This setting does not create a new enRAGE faction; custom factions must already be defined/loaded by enRAGE.
- `RecruitRosterId`: for normal/custom factions, set this to the same ID as `RecruitGangId` so roster actions (notably expulsion) apply to that faction. Only when both keys are `vdl` does the extension use the special built-in Van der Linde `StoryRoster`; other factions appear in enRAGE's normal faction roster through `Roster` records whose `Gang` equals the configured ID. The legacy `PersistentNemesisGangId` key remains accepted as an alias for `RecruitGangId`.
- `AllowFriendlyFire` and `PreventTargeting`: retain their existing damage/targeting behavior settings.
- `EnableAffinity`: enables persistent affinity tracking. For extension recruits, the saved 0–100 affinity is synchronized to enRAGE's native `Like` score, a candidate backing value for the roster's “Hacia ti” relationship display when exposed by the installed build. No second affinity meter or test-only affinity actions are added. The exact in-game display still requires testing against the installed enRAGE build.
- `AffinityTravelIntervalSeconds`: seconds between travel checks (clamped to 30–7200). Travel gain is awarded when the companion remains nearby and the player has moved at least 100 metres during the interval.
- `AffinityTravelGain`, `AffinityCombatGain`, `AffinityTreatmentGain`: gains for travel, combat (at most once per 30 seconds of an encounter), and a successful paid treatment. Affinity is clamped to 0–100 and updates the recruit's native `Like` relationship score when the field is available.
- `EnableIndividualOrders`: enables contextual order prompts when aiming at an active extension companion. With default controls the primary order uses the context-no action (F), incapacitation recovery uses E, and medicine/stance prompts use a separate action. They are not added to the roster sheet and do not intentionally rebind the native G action.
- `EnableWaitMode`: controls `Quédate aquí`; when enabled the companion returns to the saved anchor if it exceeds `WaitRadius`, then remains there instead of running enRAGE's default follow task.
- `WaitRadius`: radius in metres around the saved wait position (clamped to 1–50 m; default 8).
- `EnableInjurySystem`: enables limited health-delta detection for active recruited companions; it does not infer injury from every animation or visible wound.
- `InjuryDamageThresholdPercent`: cumulative health loss from the tracked baseline, as a percentage of max health, required to register an injury (1–50; default 10).
- `InjuryRecoverySeconds`: injury recovery countdown in seconds (15–86400; default 180), persisted periodically.
- `IncapacitatedHealthPercent`: health restored during an attempted incapacitation recovery (10–75; default 35).
- `MedicineHealingPercent`: percentage of max health added by a paid treatment (1–100; default 100, capped at max health).
- `MedicineRecoveryReductionSeconds`: recovery time removed by a treatment (0–86400; default 60).
- `RecruitMedicineCostCents`: treatment price in cents (0–100000; default 100 = $1.00). A short per-record transaction window guards against duplicate prompt activations.
- `KeepPlayerSideAfterDismissal`: deprecated. Dismissed extension recruits stay on the player's friendly relationship side regardless of their enRAGE faction.

## Translating extension text

On first launch, the extension creates `scripts/EnRAGECompanionsExtension.texts.ini` with editable labels and messages. Change only the text after the first `=`; leave the keys unchanged. Keep placeholders such as `{0}`, `{1}`, `{2}` and `${0}` intact in formatted messages. Missing keys are appended automatically when the extension updates, and blank/invalid entries fall back to the built-in Spanish text. Restart the game/script after editing so the catalog reloads. The repository also contains `EnRAGECompanionsExtension.texts.ini.example` as a template.

## Scope and verification

The roster integration now supports any already-defined enRAGE faction ID. New recruits receive an `extcomp_` ID and remain ordinary character sheets (`Story=false`); the old `extvdl_` prefix is still recognized for saves created by previous versions. When `RecruitGangId=RecruitRosterId=vdl`, records are also mirrored to the special Van der Linde `StoryRoster` and re-synchronized after loading. When a custom/normal faction ID is configured for both settings, records stay in the normal `Roster` and show in that faction's page because their `Gang` field matches its ID. The extension does not create the faction definition itself. Records are seeded through enRAGE's `RollNewLife`/`EnsureMind`/`EnsurePast` path and generated past entries are marked known. A per-session ped-handle cache and record matching prevent repeated recruitment from creating a new alias/duplicate entry. A dead recruit is retained as incapacitated instead of being marked dead and automatically replaced by enRAGE; the player can explicitly attempt recovery through the incapacitation prompt. After a full game restart the original live entity cannot be recovered from runtime cache; `Llamarlo` uses the saved model hash to create a replacement only when no matching live entity exists.

The `Llamarlo` roster action and the direct proximity hold prompt use the existing stable roster ID. A dismissed recruit can be re-adopted from nearby without opening the roster, provided that the original pedestrian still exists in the session; the extension reuses the roster record and enforces the active-companion cap. This change is implemented at source level and still needs in-game verification.

The native focus label and enRAGE roster name are set by separate systems. This revision updates both. Base-game voiced greeting lines and their built-in stranger/persona behavior are not rewritten; those are controlled by RDR2's dialogue system.

This is source code, not a compiled or tested binary. The available environment did not include a C# compiler or the user's installed game API DLL, so compilation and in-game execution could not be verified here. The relevant signatures were checked against the supplied decompiled `NemesisMod.cs`; the installed `NemesisMod.dll` may differ. Keep a backup of the save before testing changes to the roster. The INI and log are stored in the active ScriptHook `scripts` directory, not `scripts/scripts`.


## Latest additions: ambient dismissal, medicine, and roster expulsion

- `Dejar de seguirme` now preserves the released pedestrian entity instead of letting enRAGE's `Cleanup` delete a spawned replacement. It clears the companion wrapper and blip, removes the NPC from the player group, and gives the pedestrian a standard wander task. If mounted, it requests a dismount and starts wandering once on foot. The recruit remains associated with the saved roster record for possible later recall.
- To treat an extension recruit, aim directly at the NPC in the world and hold the contextual `Dar remedio` prompt. Cost, immediate healing percentage, and recovery reduction use the INI settings. The handler revalidates the exact targeted roster ID before charging; confirmed-dead/exiled records and stale targets are rejected, and a short per-record transaction lock guards against duplicate debits. An NPC at full health can still receive treatment if a persisted injury countdown remains.
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

La rama `phase2-persistent-companion-state` añade persistencia versionada por ID de registro. No sustituye ni elimina los hooks de reclutamiento existentes.

- `src/CompanionPersistentState.cs`: conserva seguimiento/espera, preferencia de montura, postura de combate, posición guardada, heridas, afinidad y orden del roster.
- `src/CompanionStateStore.cs`: carga y guardado JSON versionado, validación de valores, guardado diferido por cambios, archivo temporal, copia de respaldo y recuperación desde el último backup válido.
- `src/CompanionStateRuntime.cs`: inicializa el almacén al arrancar el script sin introducir una referencia a `NemesisMod.dll`.
- `src/EntryPoint.cs`: inicializa el almacén al construir el script.

Archivo de ejecución: `scripts/EnRAGECompanionsExtension.companions.json`. Los datos se guardan por ID estable del registro, nunca por el handle temporal del ped. El esquema nuevo es la versión 3 y sigue leyendo el esquema versionado anterior; los campos de funciones retiradas dejan de usarse y no se vuelven a guardar. El formato de desarrollo `{"Version":1,"Records":[...]}` conserva los datos antiguos de orden, postura, montura, posición, lesión y afinidad; el antiguo estado/temporizador de descanso se normaliza a seguimiento y no se migra como función activa. Si existe un archivo auxiliar de versiones anteriores, el código nuevo lo ignora y no lo elimina del disco.

## Fase 3 — órdenes individuales y espera

La fase 3 reutiliza `CompanionPersistentState` y el ciclo de enRAGE en vez de sustituir el sistema de reclutamiento. Al apuntar a un compañero activo aparecen prompts contextuales:

- `Quédate aquí`: guarda la posición actual del compañero, lo baja del caballo si corresponde y mantiene su IA de espera separada del ciclo `WaitToJoin/Ride`. La extensión bloquea las tareas nativas `WaitToJoin` y `Ride` mientras está en espera, para que no sobrescriban el anclaje. Si sale del radio configurado, recibe una tarea para regresar al punto guardado.
- `Sígueme`: elimina el anclaje de espera, restablece el estado persistente a `Following` y devuelve el control de movimiento al ciclo normal de enRAGE.
- La orden de descanso se retiró después de un cierre del juego reportado al activarla. La orden `Quédate aquí` sigue disponible.
- Los prompts de órdenes son contextuales y no se agregan botones a la ficha. En espera el wrapper permanece en `State=1` para conservar el prompt G nativo, y el controlador bloquea la tarea `Ride` que sobrescribiría la orden. La orden se guarda por ID estable; `Llamarlo` vuelve a `Following`.
- Los registros nuevos y antiguos de la extensión con `TimesKilledPlayer=0` usan `Origin=Greeted` y `CanHunt=false`: `GangMember` fuerza `Intent.Hunts` en enRAGE, por lo que no sirve como origen neutral. Los registros con un contador real de muertes no se alteran.
- La llamada desde la ficha ya no fuerza una tarea de montar al caballo después de `Adopt`; enRAGE conserva el control de sus tareas de aproximación/seguimiento.

## Fase 4 — heridas, incapacitación y remedios

- Se rastrean pérdidas de salud significativas en compañeros activos y se guarda una gravedad/temporizador por ID estable. La recuperación progresa mientras el compañero está vivo y no incapacitado.
- La incapacitación persistente es distinta de una muerte confirmada. Si las señales del ped y del daño reciente encajan con una caída, se intenta la vía de reanimación nativa; apuntar al compañero incapacitado muestra `Ayudar a levantarse`. El prompt intenta una tarea de levantarse y conserva la ficha del roster.
- El remedio usa coste, curación inmediata y reducción de recuperación configurables; excluye muertos confirmados y expulsados y aplica una ventana por registro para reducir los cobros duplicados.
- Es una aproximación de código fuente al comportamiento de incapacitación de enRAGE, no una reproducción idéntica de su gestor de combate. Si el ped ya entra en muerte real, la extensión conserva el registro como incapacitado y bloquea el respawn automático; el prompt de ayuda puede intentar una recuperación explícita. La apariencia física del ped y los wrappers de tareas/resurrección deben probarse en la versión instalada.

## Fase 5 — posturas de combate

- El prompt de postura aparece al apuntar a un compañero activo solo cuando se detecta un NPC que está atacando al jugador. Las posturas se guardan por roster; `Fuego a mi señal` requiere un disparo actual o reciente.

Las órdenes, las heridas, la recuperación por descanso y los prompts siguen siendo cambios de código fuente, sin validación dentro de RDR2. Esta revisión tampoco modifica `main` ni fusiona el PR.
