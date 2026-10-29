# Correcciones de integración

## 1. `scripts/scripts`

`AppDomain.CurrentDomain.BaseDirectory` puede apuntar ya al directorio del script. La resolución de ruta ahora comprueba si el directorio base se llama `scripts`; si es así, escribe directamente allí. Si el directorio base es la raíz del juego, añade `scripts`. La primera lectura migra el INI anterior desde la carpeta duplicada; el registro también se mueve o se combina con el nuevo.

## 2. Prioridad de «Cabalga conmigo»

El código original de enRAGE (`NemesisMod.UpdateSocial(Ped)`) registra `Darle unos dólares (5 $)` o `Tocarse el sombrero` con el mismo control de interacción y grupo de entidad que usa el prompt de acompañante. El parche anterior dejaba que `UpdateSocial` volviera a habilitar esos prompts, por lo que podían ocultar la acción de reclutamiento.

Ahora la extensión intercepta `UpdateSocial` únicamente cuando `SocialTarget(Ped)` devuelve el NPC que el juego tiene seleccionado y ese NPC supera las exclusiones de seguridad para reclutar. En ese caso conserva la actualización de la animación del sombrero, oculta el prompt social y pasa ese NPC como objetivo prioritario para el prompt de reclutamiento. Para los NPC que no son reclutables, `UpdateSocial` original sigue funcionando normalmente.

La comprobación ya no rechaza una entidad únicamente porque `IS_ENTITY_A_MISSION_ENTITY` sea verdadero; enRAGE puede marcar civiles interactuables de esa manera. Se mantienen los filtros explícitos para personajes de historia, eventos, niños, agentes de la ley y entidades gestionadas por el juego.

## 3. Miembros reclutados en la banda Van der Linde

La extensión reutiliza un `NemesisRecord` existente cuando el NPC ya está en el roster. Para civiles sin registro, crea uno directamente para evitar las matrices de nombres vacías de `NewRecord`, lo añade a `NemesisMod.Roster`, con `Gang = "vdl"`, y llama a `SaveRoster`.

El prompt de un recluta ya incorporado cambia a «Dejar de seguirme». La acción de despedida detiene el seguimiento, pero no elimina su registro del roster. La ubicación donde fue reclutado no se considera un campamento real. `vdl` es una facción de historia en enRAGE, de modo que esta versión agrega pertenencia al roster, no una banda territorial simulada ni un creador de bandas.

## Estado

Se modificó el código fuente del parche, no el `NemesisMod.dll` ni el `NemesisMod.cs` instalado. La compilación y la prueba dentro de RDR2 deben hacerse con el ScriptHookRDRNetAPI.dll de la instalación del usuario.


## 4. Error `Roster record preparation warning: IndexOutOfRangeException`

The earlier implementation called enRAGE's `NewRecord`. That factory indexes the chosen gang's first-name, surname, and title arrays without guarding against an empty array, which can throw `IndexOutOfRangeException` for some model/faction combinations. The extension now constructs `NemesisRecord` directly, sets the required ID/name/model/alive/gang fields, then adds it to `NemesisMod.Roster` and calls `SaveRoster` after adoption succeeds. The generated display name reuses enRAGE's already-known name when available; otherwise it uses a unique `Aliado N` label.


## 5. Registros guardados que no aparecían en la vista F11 de Van der Linde

`NemesisMod.Roster` y `NemesisMod.StoryRoster` son colecciones distintas. `vdl` es una banda de historia; guardar un `NemesisRecord` solo en `Roster` no basta para que la vista de banda de historia lo enumere. Esta revisión sincroniza los registros persistentes con `StoryRoster` y repite la sincronización periódicamente para reconstruirla después de cargar la partida. El registro persistente sigue en `Roster`; se añade la misma instancia a la lista de historia para evitar generar un segundo ID. Los reclutas no se marcan como personajes guionizados (`Story=false`), ya que eso los enviaba al perfil de historia que mostraba mente e historial como no descubiertos.

No se ha podido compilar ni probar en el juego desde este entorno. Verificar dentro de RDR2 que el miembro aparece en la vista F11 de Van der Linde y que su estado cambia a muerto si fallece.


## 6. Re-recruitment, friendly dismissal and full character sheet

This revision keeps an in-session mapping from the original ped handle to the saved `NemesisRecord`, reuses legacy `vdl` roster records when matching a dismissed NPC, and adds an `extvdl_` ID prefix to new recruits. It prevents repeated recruitment of the same entity from generating a fresh `Aliado N` entry each time. It does not automatically delete duplicate records created by earlier versions because two different NPCs may share a model hash.

La despedida reemplaza la restauración de relaciones de `RestoreReal` de enRAGE para los reclutas de la extensión: limpia tareas de seguimiento/combate, lo separa del grupo de seguimiento y lo asigna a un grupo de relación propio de miembros Van der Linde. La extensión establece la relación de compañero en ambas direcciones con el grupo actual del jugador y vuelve a aplicar el grupo a los reclutas despedidos si el juego lo cambia durante la sesión. El NPC no debería volver a tratar al jugador como enemigo simplemente por quedar libre. La clave INI antigua `KeepPlayerSideAfterDismissal` se conserva por compatibilidad, pero su valor ya no desactiva este comportamiento.

Los reclutas siguen apareciendo en la lista F11 de Van der Linde, pero no se marcan como personajes del guion. La extensión inicializa sus fichas con `RollNewLife`, `EnsurePast` y `EnsureMind` de enRAGE y marca los eventos generados del pasado como conocidos/públicos, para que su ficha normal pueda mostrar personalidad, rasgos, mente e historia personal en vez del estado «no descubierto» del perfil exclusivo de personajes de historia. Los registros creados por versiones anteriores se reparan durante la sincronización periódica del roster.


## 7. El recluta despedido seguía respondiendo con hostilidad

La causa no era únicamente el grupo de relaciones. El método `Setup` de enRAGE activa los atributos de combate 5 y 46 para que un compañero pueda combatir. Según la ruta de despedida usada, esos atributos podían seguir provocando que un NPC genérico respondiera a una amenaza como si todavía estuviera configurado para combatir.

La extensión ahora, al despedirlo y durante el mantenimiento de los reclutas despedidos, desactiva los atributos 5 y 46, vuelve a asignar la relación `Companion` tanto al grupo actual del jugador como al grupo nativo `PLAYER`, y cancela únicamente las tareas de combate dirigidas contra el jugador. No lo vuelve invulnerable: si el jugador decide dispararle, el NPC todavía puede morir y su ficha puede marcarse como muerta, tal como exige el roster persistente.

La modificación es de código fuente y no se compiló aquí porque el entorno no incluye MSBuild ni `ScriptHookRDRNetAPI.dll`. Debe compilarse con la misma API de ScriptHookRDR2DotNet V2 instalada en el equipo del usuario y probarse dentro del juego.


## 8. Un recluta despedido aún huía, contraatacaba o recibía prompts sociales

La última revisión usa el `NemesisMod.cs` compartido como referencia para cubrir dos rutas distintas:

- `UpdateSocial(Ped)` llama a `SocialTarget(Ped)` y crea acciones como `Darle unos dólares (5 $)`, invitar a un trago, tocarse el sombrero o estrechar la mano. El prefijo ahora reconoce los NPC asociados a una ficha reclutada antes de las verificaciones de límite/combate, oculta el prompt social de enRAGE y no altera el comportamiento social de los NPC ajenos a la extensión.
- `Setup(Companion, Ped, Ped)` coloca al recluta en el grupo del jugador y activa los atributos de combate 5/46 para que pueda combatir mientras sigue al jugador. Tras despedirlo, la extensión vuelve a aplicar un perfil pasivo: relación amistosa bidireccional, atributos 5/46 desactivados, `BlockPermanentEvents=true`, y los config flags 297/301 que `Setup` usa para el targeting en RDR2. El mantenimiento elimina tareas de huida ya iniciadas y tareas de combate dirigidas contra el jugador.

El recluta sigue siendo vulnerable: el perfil no impide que el jugador le haga daño o lo mate, y su ficha puede pasar a muerta. Durante el seguimiento activo, la extensión quita el bloqueo de eventos para que enRAGE pueda controlar al compañero y hacerlo combatir. Mientras está despedido, se prioriza que permanezca pasivo y amistoso, en lugar de comportarse como un civil corriente.

Esta es una corrección de código fuente; no fue compilada ni ejecutada en RDR2 aquí. Debe verificarse en el juego tanto la desaparición de los prompts sociales del recluta como que apuntarle no lo haga huir y que no contraataque si el jugador le dispara.


## 9. Prioridad de interacción y llamada desde la ficha

- Se interceptan `NemesisMod.Interrogable(Ped, Ped, float)` y `UpdateInterrogation(Ped)`: además de excluir al recluta, se limpia el caché temporal de `Interrogar` que enRAGE puede volver a mostrar durante 0,7–1,2 segundos. Esto permite que la interacción de compañero de enRAGE sea la relevante al acercarse.
- Se añade `Llamarlo` a las acciones de `RosterMenu.DetailActions` para fichas de reclutas identificadas por el caché de la sesión o por el prefijo persistente `extvdl_`. La activación crea un objeto `Companion` de enRAGE y reutiliza `SpawnCompanion`/`Setup`; el caballo se genera en `Setup` y se solicita montar al NPC.
- No se pudo compilar ni ejecutar el juego en este entorno. La acción requiere comprobación en RDR2; si la versión instalada cambia las firmas privadas de `RosterMenu`, el log debe mostrar el aviso correspondiente.


## 10. Reutilizar al NPC existente al llamarlo y corregir el odio inicial

La acción `Llamarlo` ya no asume que siempre debe crear una copia. Primero busca el `Ped` vivo asociado exactamente al ID del registro que se eligió en el roster. Si esa asociación de sesión no está disponible, revisa los peds del `PedSnapshot` cercano y consulta `RecordAt`; no adopta otro civil solo porque tenga el mismo modelo. Cuando encuentra al recluta original, lo entrega a `NemesisMod.Adopt` y `Setup`, por lo que vuelve a seguir al jugador sin duplicar el personaje. Solo crea una entidad nueva si no se encuentra una coincidencia viva. Las nuevas entidades también se asocian al registro, para que las llamadas posteriores reutilicen esa misma instancia mientras siga cargada.

El valor alto de odio aparecía porque `RollNewLife` de enRAGE genera los valores de perfil y puede asignar `Hate` de 70–80. Después de esa inicialización, y al guardar el registro, la extensión ahora fija `Hate=0` y `Fear=0`, pone `Like=100` si esa versión de `NemesisRecord` dispone de ese campo e intenta asignar `Disposition=Friendly` si admite escritura. Además, durante la sesión reimpone esos valores a los registros reclutados. La relación nativa entre el recluta y el jugador continúa configurada como `Companion` en ambas direcciones. No se modifican las fichas de personajes originales de la historia.

**Verificación pendiente:** el código fuente fue actualizado y el archivo ZIP puede validarse, pero este entorno no tiene MSBuild/C# ni el `ScriptHookRDRNetAPI.dll` instalado, así que no fue posible compilar ni probarlo dentro de RDR2. En el juego, verifica que al despedir y llamar al mismo recluta vuelve la misma persona y que su ficha deja de mostrar `Hate 70/80`.


## Identity and combat behavior (current source revision)

- On recruitment and roster re-call, the extension now sets RDR2's native focus/aim label to the roster name using `_SET_PED_PROMPT_NAME` and enRAGE's persistent-string allocator. It also marks the ped as known in enRAGE's `_named` table. This fixes the separate UI label when aiming/focusing the adopted NPC; stock voiced greeting dialogue is not replaced.
- The extension hooks `NemesisMod.CompanionFight` for extension-created followers only. They no longer use enRAGE's broad `TASK_COMBAT_HATED_TARGETS_AROUND_PED` behavior; they respond only to nearby peds actually in combat with the player. Surrendered, fleeing, downed, hogtied, ragdolling, writhing, and knocked-out peds are filtered out. Original enRAGE story companions are not modified.
- These changes are source-level and require a local build plus in-game testing.


## 11. Despedida ambient, remedio por $1 y expulsión del roster

La ruta de despedida ya no llama a la limpieza de enRAGE con el `Ped` asignado al wrapper, porque `Cleanup` elimina toda entidad con `IsReal=false`. Antes de limpiar el wrapper, la extensión restaura lo que puede, guarda localmente la referencia al NPC y vacía los campos `Ped`/`Real` del objeto `Companion`; así `Cleanup` retira el blip y las reservas internas, pero no elimina al NPC que debe seguir existiendo. Luego lo separa del grupo del jugador, le devuelve un perfil de peatón ambient y solicita `TASK_WANDER_STANDARD`. Si está montado, solicita desmontar y reintenta la tarea de paseo después de bajar del caballo.

El remedio no aparece en la ficha del roster. Al apuntar directamente a un recluta vivo en el mundo, la extensión crea el prompt contextual `Dar remedio ($1)` en el grupo de interacción de esa entidad. Al mantenerlo, comprueba que el objetivo siga siendo el recluta registrado, verifica que tenga menos que su salud máxima, cobra exactamente 100 centavos y ajusta su `Health` a `MaxHealth`. Si la entidad dejó de ser válida, no cobra; si ya está a salud máxima, tampoco cobra. No se aplica una cura remota desde el roster.

Los miembros de Van der Linde del roster pueden recibir la acción `Echar de la banda`, incluidos los registros nuevos no asociados a IDs `vdl_`. No se permite expulsar a alguien mientras está siguiendo activamente al jugador; primero hay que despedirlo. Los reclutas de la extensión que ya fueron despedidos pueden llamarse o expulsarse. La activación inicial solo arma una confirmación y muestra el aviso de irreversibilidad/consecuencias futuras. `Confirmar expulsión` quita la afiliación y `Cancelar expulsión` descarta la operación; salir con la tecla Back también cancela la confirmación pendiente. Los IDs confirmados quedan en `EnRAGECompanionsExtension.exiled-story-roster.txt`; la rutina de sincronización respeta la lista y evita volver a añadirlos a `StoryRoster`.

No se pudo compilar ni probar en RDR2 desde este entorno. Verificar especialmente que un NPC invocado no desaparezca al despedirlo, que el prompt de remedio aparezca al apuntar al recluta, cueste $1 y llene su salud y que la confirmación de expulsión se mantenga al cambiar de pestaña/recargar la partida.


## 12. Correcciones de interacción del último paquete

- El remedio ahora detecta el recluta al que se apunta consultando tanto `IS_PLAYER_FREE_AIMING_AT_ENTITY` como `IS_PLAYER_TARGETTING_ENTITY`; no exige que `IS_PLAYER_FREE_AIMING` sea verdadero porque puede devolver falso con la asistencia de apuntado. Si el build lo permite, también consulta el objetivo social actual de enRAGE.
- El prompt `Dar remedio ($1)` usa el control `-2074855166`, que en el código de enRAGE también se usa para la interacción del periódico. Así no comparte exactamente el mismo control que `Cabalga conmigo`/`Dejar de seguirme` o `Saludar`, evitando que el prompt de cura quede suprimido por la competencia de prompts. Sigue siendo necesario verificar en el juego qué tecla muestra el binding instalado.
- `Llamarlo` se oculta cuando ya existe un compañero activo con el mismo ID de roster (incluido el caso en que el acompañante fue creado originalmente desde `Cabalga conmigo`).
- La expulsión reconoce miembros que estén en `StoryRoster` con cualquier formato de ID, no solo `vdl_*`; los registros ordinarios se admiten si están en `Roster` y su afiliación es `vdl`. También se pueden expulsar fichas marcadas como fallecidas. Los reclutas de la extensión siguen excluidos.
- Al volver a reclutar un NPC despedido, se elimina su temporizador de paseo y se borran las tareas ambientales anteriores antes de ejecutar `Adopt`, dejando que enRAGE instale su nueva lógica de compañero.

No se pudo compilar ni verificar la ejecución en el juego desde este entorno. Estas modificaciones son correcciones del código fuente y necesitan una prueba local.


## Latest roster, friendship, and gender restrictions

- `Echar de la banda` is now injected for any valid Van der Linde `StoryRoster` entry and any `Roster` record assigned to `vdl`, regardless of ID prefix. Active companions (extension or stock enRAGE) cannot be expelled; dismissed recruits can be called or permanently expelled.
- Dismissed recruits keep the friendly relationship group and have their roster Hate/Fear reset. While the extension's standard wander task runs, permanent ambient events are blocked to prevent retaliation from staring/bumping; attacks against the player are cleared and wandering is resumed. This should be tested in-game because ambient ped AI may vary by activity/model.
- Generic recruitment accepts only male pedestrians as classified by enRAGE's own `NemesisSystem.Models.Female(uint)` helper. If the helper cannot be resolved, recruitment is disabled rather than allowing an unclassified model.

These are source-level changes; no compiler or in-game runtime was available here.


## 12. Facciones personalizadas de enRAGE

La extensión ya no asume que todos los reclutas pertenecen a Van der Linde. `RecruitGangId` determina la afiliación persistente y `RecruitRosterId` determina el ID de facción que se considera para las acciones de roster. El valor predeterminado de ambos sigue siendo `vdl`. Para una facción personalizada ya definida en enRAGE, coloca el mismo ID exacto en ambas claves. La extensión consulta `NemesisSystem.Gangs.ById` para detectar IDs inexistentes y bloquea el reclutamiento si la facción configurada no se puede resolver.

Las facciones normales/custom utilizan `Roster` con `NemesisRecord.Gang` igual al ID configurado, de modo que enRAGE las muestra en su página habitual de facción. Solo el caso especial `RecruitGangId=vdl` + `RecruitRosterId=vdl` se sincroniza con la lista narrativa `StoryRoster`. Los IDs nuevos de las fichas ahora llevan el prefijo neutro `extcomp_`; las fichas antiguas con `extvdl_` siguen siendo reconocidas. La expulsión quita la afiliación de la facción activa y registra el ID de la ficha; se conserva la lectura del archivo de expulsiones antiguo.

La extensión usa la relación de jugador para mantener amistosos a los reclutas despedidos, no una supuesta afiliación VDL, por lo que ese comportamiento también sirve para facciones normales. Este cambio no crea facciones nuevas: la definición debe existir previamente en enRAGE y el ID de la INI debe coincidir exactamente con su ID.

Estado: cambio de código fuente sin compilación ni prueba in-game en este entorno.


## 13. Exclusión del remedio tras expulsión

- Si el ID de roster está en `ExiledRosterIds`, `Dar remedio ($1)` ya no se muestra aunque el NPC expulsado siga físicamente cerca. La ruta que resuelve objetivo, el estado del prompt y el handler de la cura verifican la expulsión; por tanto no se cobra ni se cura a un miembro expulsado.

Esta comprobación está implementada a nivel de código fuente; sigue pendiente su validación dentro de RDR2.

## Fase 2 — persistencia y migración

- Añadidos modelos persistentes independientes del handle del ped.
- Añadido `EnRAGECompanionsExtension.companions.json`, esquema versionado (versión 3), normalización de enums, afinidad, posición, lesión y preferencia de montura/postura.
- La escritura se realiza mediante archivo temporal y sustitución con copia de respaldo cuando la plataforma lo permite; si falla la operación principal, se intenta una sustitución alternativa.
- Si falta el JSON principal o no se puede leer, el almacén intenta restaurar el backup `.bak`; el archivo ilegible se conserva en cuarentena cuando el sistema de archivos lo permite.
- El esquema anterior se sigue leyendo para conservar afinidad, postura, montura, posición y lesión. Los estados antiguos `Resting` se normalizan a seguimiento; el temporizador de descanso se ignora. Los campos retirados no se vuelven a guardar y los archivos auxiliares de versiones anteriores se ignoran.
- El almacén se inicializa desde `EntryPoint`; el guardado de migración se realiza solo si hay cambios.

### Fase 3 — órdenes individuales y espera

- Las órdenes individuales aparecen como prompts contextuales solo al apuntar al ped exacto de un recluta activo y no expulsado. `Quédate aquí`/`Sígueme` usa F; E se reserva para ayudar a levantarse estando incapacitado. Mientras espera, el wrapper conserva `State=1`, y los hooks de `WaitToJoin`/`Ride` impiden que la IA de enRAGE sobrescriba el anclaje. Al expulsar al miembro se eliminan los prompts contextuales activos. La orden de descanso se retiró tras un cierre del juego reportado al activarla.
- `Quédate aquí` guarda coordenadas por ID estable de roster, aplica la tarea de anclaje inmediatamente y evita entrar en el estado `Ride` de enRAGE. Si el NPC se aleja más de `WaitRadius`, se le envía de vuelta al anclaje; si está montado, se le ordena desmontar.
- `Sígueme` elimina el anclaje, limpia la tarea de espera y reactiva el ciclo normal de enRAGE.
- Al llamar desde la ficha ya no se lanza una tarea manual de montar después de `Adopt`, porque podía interrumpir el seguimiento. Para un ped existente se restablece `State=0` (`WaitToJoin`); para un reemplazo creado mediante `SpawnCompanion` se conserva el estado inicial asignado por enRAGE.
- Se agregaron las claves `EnableIndividualOrders`, `EnableWaitMode` y `WaitRadius` al INI autogenerado y al ejemplo. La afinidad persistente del recluta se sincroniza con el campo nativo `Like` de enRAGE cuando la versión instalada lo expone, en lugar de añadir un medidor independiente. La apariencia exacta de la barra `Hacia ti` requiere verificación dentro del juego.
- Los registros de extensión con `TimesKilledPlayer=0` usan `Origin=Greeted` y `CanHunt=false`: `GangMember` fuerza `Intent.Hunts` en enRAGE y no es neutral. Si el contador registra una muerte real, el historial se conserva. Las rutas de reclutamiento, roster, expulsión confirmable y remedio no se sustituyen.

### Fase 4 — heridas e incapacitación

- El controlador fuerza la vulnerabilidad de los reclutas de la extensión frente al estado invulnerable que asignaba enRAGE al adoptar compañeros; evita además que un recluta de poca salud se quede en el estado `GoingHome` de enRAGE. Las lesiones se registran al superar `InjuryDamageThresholdPercent` de pérdida acumulada desde el baseline de salud. Cada lesión tiene gravedad y tiempo de recuperación por ID estable.
- La recuperación avanza en intervalos y se persiste periódicamente; no se guarda el JSON cada frame.
- El estado persistente `IsIncapacitated` se conserva separado de una muerte confirmada del motor. Para reclutas, la extensión trata la muerte detectada como incapacitación de roster, conserva `IsAlive`/`PresumedDead` y bloquea el respawn automático de enRAGE. No llama a `RESURRECT_PED` en segundo plano; ese native solo se intenta si el jugador activa explícitamente `Ayudar a levantarse`. Si el motor ya marcó el ped como muerto, su pose visual no se puede garantizar sin probar el runtime instalado.
- Al apuntar al compañero incapacitado aparece `Ayudar a levantarse`. La orden restaura salud parcial, prueba `TASK_STAND_UP`, limpia la marca persistente de incapacidad y conserva el miembro en el roster. Si el wrapper no está disponible, puede faltar la animación de levantarse.
- El remedio usa `RecruitMedicineCostCents`, `MedicineHealingPercent` y `MedicineRecoveryReductionSeconds`. Antes de cobrar valida el objetivo, excluye muertos confirmados y expulsados, y usa un bloqueo temporal por ID para reducir dobles cobros.

### Fase 5 — posturas de combate

- El prompt de postura aparece al apuntar a un compañero activo solo cuando la detección encuentra un ped que está atacando al jugador. `Fuego a mi señal` requiere disparo del jugador en el momento actual o dentro de una ventana de 2,5 segundos.

### Textos y traducciones de la extensión

- En el primer arranque se genera `scripts/EnRAGECompanionsExtension.texts.ini`. Los valores se pueden traducir sin recompilar el código: conserva cada clave a la izquierda de `=` y cambia el texto a la derecha.
- Los mensajes con formato usan marcadores como `{0}`, `{1}` y `${0}`; no los borres ni los renombres. Las claves nuevas se agregan al archivo existente y el catálogo se recarga al iniciar el script.
- `EnRAGECompanionsExtension.texts.ini.example` contiene la plantilla inicial con las claves de la extensión.


### Pendiente y verificación

No hay compilador C#/MSBuild ni `ScriptHookRDRNetAPI.dll` local en este entorno, por lo que no se pudo generar ni ejecutar el ensamblado. La revisión estática de la fuente confirma la existencia de todos los hooks nuevos y el balance de llaves y paréntesis. Aun así, falta probar dentro de RDR2 que la API instalada exponga los wrappers de `TASK_GO_STRAIGHT_TO_COORD` y `TASK_STAND_STILL` con las firmas que espera la orden, y que un compañero que regresa al roster vuelva a seguir tanto a pie como montado. No se ha compilado ni verificado dentro del juego.


### Endurecimiento adicional — tratamiento, reincorporación y recuperación del JSON

- El tratamiento pagado ahora comprueba que la salud se haya aplicado realmente al ped vivo. Si no se confirma, intenta devolver el coste usando la firma de incremento de efectivo encontrada en el código decompilado de enRAGE; si la devolución falla, deja un error explícito en el log en lugar de anunciar un tratamiento exitoso.
- La asistencia a un compañero incapacitado no borra el estado persistente de incapacitación ni lo marca como recuperado hasta verificar que el ped existe, ya no figura como muerto y tiene salud positiva. Si esa comprobación falla, conserva el estado para permitir otro intento.
- La carga del estado persistente intenta recuperar `EnRAGECompanionsExtension.companions.json.bak` cuando falta el archivo principal o no puede deserializarse. La copia primaria ilegible se conserva como archivo de cuarentena antes de restaurar el backup, cuando el sistema de archivos lo permite.
- Esto es una revisión estática del flujo de error. No se ha compilado ni probado dentro de RDR2; hay que comprobar in-game el reembolso ante fallo, el levantamiento y la recuperación tras un JSON corrupto.


- **Limitación pendiente de detección de daño:** el controlador utiliza un baseline acumulado de salud y un umbral configurable, pero no atribuye exhaustivamente cada pérdida al jugador, a un NPC hostil, a una caída o al entorno. Esta distinción requiere una API de atribución de daño fiable y una prueba dentro de la versión objetivo.
