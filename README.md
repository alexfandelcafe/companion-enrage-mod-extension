# EnRAGECompanionsExtension

Extensión independiente para Red Dead Redemption 2 (PC), ScriptHookRDR2 .NET V2 y enRAGE. Se integra en tiempo de ejecución mediante reflexión y Harmony; no modifica `NemesisMod.dll` ni necesita compilar contra ese ensamblado.

**Estado de esta rama:** integración en desarrollo. El código fuente incluye los módulos nuevos descritos abajo, pero no se ha compilado ni probado dentro del juego en este entorno. No hay una DLL de esta revisión en el repositorio.

## Requisitos

- Windows y Red Dead Redemption 2 para PC.
- ScriptHookRDR2 + ScriptHookRDR2 .NET V2, con `ScriptHookRDRNetAPI.dll` correspondiente a la instalación.
- Visual Studio/MSBuild con el paquete de destino .NET Framework 4.8.
- Acceso a NuGet para restaurar `Lib.Harmony` 2.4.3.

El proyecto es x64 y produce `EnRAGECompanionsExtension.dll`. La ruta de juego predeterminada en el proyecto es `D:\Red Dead Redemption 2`.

## Compilar e instalar

1. Abre PowerShell para desarrolladores de Visual Studio en Windows.
2. Ejecuta desde esta carpeta:

   ```powershell
   .\build.ps1
   ```

   Si el juego está en otra ruta, usa `-RDR2GameDir "X:\ruta\al\juego"`.
3. Copia la DLL compilada y la DLL de Harmony generada/copieda por MSBuild a la carpeta `scripts` del juego.
4. Mantén las DLL de ScriptHook de la misma instalación; no reemplaces la API del juego con una versión de otra instalación.
5. Inicia RDR2 con enRAGE y ScriptHook cargados. Revisa `EnRAGECompanionsExtension.log` si la integración no se instala.

El punto de entrada comprueba periódicamente si enRAGE ya está cargado. Si sus tipos o firmas privados no coinciden con la versión decompilada auditada, la extensión registra el fallo y evita aplicar los hooks obligatorios.

## Funciones existentes que se conservan

- Reclutamiento contextual de NPCs masculinos con **«Cabalga conmigo»**.
- Creación/reutilización de registros en el roster y conservación de la identidad mediante el ID de registro.
- Integración con Van der Linde y otras facciones de enRAGE que ya existan y tengan un ID válido.
- Seguimiento y combate reactivo para los compañeros gestionados por la extensión.
- **«Dejar de seguirme»** sin eliminar la ficha del roster.
- **«Llamarlo»** desde las acciones del roster, con comprobación de miembro activo y límite de compañeros.
- Registro de muertes, restricciones de objetivos de combate, supresión de algunas interacciones sociales en reclutas y tratamiento contextual.
- Confirmación de expulsión y exclusión persistente de miembros expulsados.
- Compatibilidad de lectura con IDs antiguos `extvdl_` además de `extcomp_`.
- Persistencia de hash de outfit existente en `EnRAGECompanionsExtension.outfits.txt`, conservada durante la migración.

No se crea una facción enRAGE automáticamente. Para una facción personalizada, utiliza el ID exacto que ya esté definido y cargado en enRAGE.

## Sistemas nuevos de esta rama

### Órdenes contextuales

Al apuntar a un recluta reconocido, la extensión crea prompts vinculados a la entidad objetivo y al ID persistente del roster. Según el estado, puede ofrecer:

- **Esperar aquí / Seguirme**.
- **Quedarse montado / Desmontar / Volver a montar**, cuando la entidad de caballo del compañero sigue disponible y pasa las comprobaciones.
- Ciclar la postura de combate.
- **Guardar posición / Actualizar posición / Regresar a posición**.
- **Llamarlo** para un recluta despedido reconocido.

La posición se guarda por ID de recluta, no por handle de ped. Regresar a posición depende de que la entidad esté cargada y de que ScriptHook exponga la firma esperada de la tarea de movimiento. Si no puede comprobarse que una montura esté libre, la orden de montar falla de forma segura. La selección de posición manual se limita a guardar la posición actual del compañero; no lee un waypoint elegido por el jugador.

### Espera y descanso

Con `EnableWaitMode=true`, despedir a un compañero registra su posición, intenta desmontarlo y lo mantiene esperando cerca. La alternativa base es permanecer de pie; no se ha añadido ninguna animación de descanso no verificada. Si se desactiva la espera, se conserva el comportamiento ambiental anterior basado en deambular.

La transición a activo cancela el estado de espera. Despedir no elimina la ficha, y expulsar sigue siendo una acción separada.

### Heridas y tratamiento

El sistema observa cambios de salud a intervalos, acumula daño en una ventana de 10 segundos y asigna gravedad 1 o 2 cuando se supera `InjuryDamageThreshold`. La herida queda vinculada al ID del roster y no se borra por una simple desaparición del ped.

El remedio valida registro, entidad, vida y expulsión antes de cobrar. Con la configuración predeterminada cuesta $1, cura la salud completa y elimina la herida persistente. Puede configurarse para curación parcial. La recuperación por descanso reduce un nivel de gravedad tras el intervalo configurado. Las heridas no resucitan miembros que el roster registra como fallecidos.

La ralentización usa resolución reflectiva de `PED.SET_PED_MOVE_RATE_OVERRIDE(int, float)`; si no se encuentra el wrapper compatible, la herida persiste pero no se aplica la ralentización. Este comportamiento necesita prueba con la DLL instalada.

### Posturas de combate

La postura individual se guarda en el estado persistente. Se aplican filtros adicionales de objetivo a los compañeros de la extensión, manteniendo la política restrictiva existente: no se seleccionan automáticamente objetivos muertos, incapacitados, rendidos o que simplemente sean NPCs hostiles/pasajeros. Los compañeros originales de enRAGE no reciben estos filtros nuevos.

**Fuego a mi señal** es conservador: depende del wrapper `PED.IS_PED_SHOOTING(int)` para detectar disparos del jugador y solo permite seleccionar amenazas que ya estén combatiendo contra él. Si no se puede detectar el disparo, la postura no autoriza ataques. Requiere pruebas en RDR2 para comprobar que el wrapper de la instalación exponga la firma que busca el módulo.

### Afinidad

La confianza se guarda por ID estable en una escala de **0 a 100**, comienza en 50 para registros nuevos y sube en intervalos de viaje válido, eventos de combate y tratamientos. La ganancia por viaje requiere tiempo válido (300 segundos por defecto), al menos 50 m acumulados de movimiento mientras el compañero está activo y cerca del jugador, y ausencia de combate. Esto evita que quedarse quieto al lado del NPC aumente la confianza.

En esta rama, la confianza se muestra como una acción seleccionable **«Confianza: n/100»** en la lista de acciones de la ficha; al seleccionarla se muestra el valor en un subtítulo. No se afirma que se haya insertado una fila dentro del panel gráfico original de `RosterMenu.DrawDetail()`, cuyo dibujo interno no se ha modificado.

### Ropa y equipamiento

La migración copia los hashes válidos del archivo antiguo a `EnRAGECompanionsExtension.companions.json` sin borrar `outfits.txt`. El código sigue pudiendo capturar/restaurar el hash meta-outfit cuando los wrappers requeridos están disponibles.

**Todavía no está implementada la persistencia efectiva de prendas individuales, sombreros o armas.** Sus campos y opciones de configuración son una base para una fase posterior. No se equipan hashes de armas o componentes sin validación específica.

## Configuración INI

Copia `EnRAGECompanionsExtension.ini.example` como `scripts/EnRAGECompanionsExtension.ini`. La extensión conserva las claves antiguas (`MaxCompanions`, `RecruitGangId`, `RecruitRosterId`, `AllowFriendlyFire`, `PreventTargeting`) y añade las nuevas si faltan.

- `EnableIndividualOrders`: habilita los prompts individuales.
- `EnableWaitMode`, `WaitRadius`: espera y radio para conservar o recuperar la posición guardada.
- `EnableInjurySystem`, `InjuryDamageThreshold`, `InjuryMoveRateMultiplier`, `InjuryRecoverySeconds`: umbral, velocidad y recuperación.
- `MedicineCostCents`, `MedicineInjuryReduction`, `MedicineRestoresFullHealth`, `MedicineHealthRestoreFraction`: coste y eficacia del tratamiento.
- `EnableAffinity`, `AffinityTravelIntervalSeconds`, `AffinityTravelGain`, `AffinityCombatGain`, `AffinityTreatmentGain`: afinidad.
- `EnableCombatStances`, `ProtectAreaRadius`, `FireOnSignalOnly`, `FireSignalWindowMs`: posturas y radio defensivo.
- `EnableAppearancePersistence`, `PreserveSavedOutfit`: hash del outfit legado.
- `EnableClothingComponentPersistence`, `EnableHatPersistence`, `EnableWeaponPersistence`: reservadas, desactivadas por defecto; activar estas claves no significa que esas categorías ya tengan implementación funcional.

Los valores se limitan a rangos conservadores. Los IDs personalizados de facción no se alteran.

## Archivos auxiliares

Todos están bajo la carpeta `scripts` activa del juego:

| Archivo | Uso |
|---|---|
| `EnRAGECompanionsExtension.ini` | Configuración |
| `EnRAGECompanionsExtension.log` | Diagnóstico |
| `EnRAGECompanionsExtension.outfits.txt` | Formato antiguo de outfit: `ID|HASH` hexadecimal |
| `EnRAGECompanionsExtension.companions.json` | Datos individuales versionados por ID de roster |
| `EnRAGECompanionsExtension.companions.json.bak` | Copia anterior tras una sustitución correcta |
| `EnRAGECompanionsExtension.companions.json.corrupt-*` | Copia de un archivo JSON que no se pudo leer |
| `EnRAGECompanionsExtension.pending-heals.txt` | Compatibilidad con el estado antiguo de curaciones |
| `EnRAGECompanionsExtension.exiled-roster.txt` | IDs expulsados |
| `EnRAGECompanionsExtension.exiled-story-roster.txt` | Archivo de expulsiones legado que todavía se lee |

No borres archivos auxiliares si quieres conservar la afinidad, las heridas, las órdenes o la apariencia registrada.

## Verificación y límites conocidos

Esta rama no se ha compilado en el entorno de desarrollo empleado para generar el cambio: no estaban disponibles MSBuild/compilador C# ni el `ScriptHookRDRNetAPI.dll` de la instalación objetivo. Tampoco se ha ejecutado RDR2. La integración de prompts, estados, nativos, tareas de caballo y UI debe considerarse **código fuente pendiente de prueba**, no una compilación validada.

La API privada de enRAGE y los wrappers de nativos pueden diferir de los recursos decompilados. Haz una copia de tus partidas guardadas antes de probar la extensión.
