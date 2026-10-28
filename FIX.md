# Correcciones, compatibilidad y limitaciones conocidas

Este documento resume las protecciones heredadas y las limitaciones de la rama `companion-systems-20261028`. Los cambios descritos son de código fuente; las pruebas in-game siguen pendientes.

## Protección de la implementación existente

- La extensión sigue siendo una DLL independiente de enRAGE y usa reflexión/Harmony para interactuar con sus métodos privados.
- El punto de entrada sondea si enRAGE está cargado antes de instalar los hooks obligatorios. Ante firmas ausentes, la integración registra el error en lugar de continuar con referencias nulas.
- Los IDs `extcomp_` y los registros antiguos `extvdl_` siguen siendo reconocidos.
- La recluta genérica solo acepta NPCs masculinos según `NemesisSystem.Models.Female(uint)`; si el helper no puede resolverse, el reclutamiento falla de forma conservadora.
- La extensión diferencia la banda narrativa Van der Linde (`StoryRoster`) de los registros normales/custom de `Roster` usando los IDs configurados. No crea facciones.
- El remedio verifica el ID exacto del registro conectado al ped que se está apuntando; comprueba la muerte y la expulsión antes del cobro.
- La expulsión requiere confirmación y no está permitida mientras un compañero gestionado siga activo. Los archivos antiguos de expulsión aún se leen.
- El filtro de combate no sustituye el comportamiento de los compañeros originales de enRAGE. Para reclutas de la extensión excluye objetivos que enRAGE o el ped reconocen como muertos, rendidos, derribados, hogtied, ragdoll o fuera de combate válido.
- Los archivos auxiliares se resuelven respecto a la carpeta `scripts` activa. La lógica previa intenta migrar archivos creados en la ruta accidental `scripts/scripts`.

## Sistemas añadidos en esta rama

### Estado persistente por ficha

- Nuevo `src/CompanionPersistentState.cs`: esquema JSON versionado, clave estable por ID de roster, estados de orden/postura/montura, posición, gravedad de herida, afinidad y campos reservados para personalización.
- El guardado escribe un temporal, intenta una sustitución que conserva la copia anterior y genera respaldo `.bak`. Si el JSON está corrupto, conserva el archivo original y deja una copia de diagnóstico. Si la versión del esquema es futura, el archivo se abre solo para lectura para evitar sobrescribirlo.
- El archivo legado `EnRAGECompanionsExtension.outfits.txt` se importa sin eliminarlo.
- Nuevo `EnRAGECompanionsExtension.companions.json` en la carpeta `scripts`.

### Órdenes y descanso

- Los prompts de órdenes se crean únicamente cuando se apunta a una entidad reconocida por el registro persistente y se ocultan al cambiar de objetivo.
- La espera usa una posición guardada, intenta mantener al NPC en pie y limita la frecuencia de tareas de retorno; no añade animaciones de descanso sin verificar.
- La orden de montaje necesita una montura viva, de la instancia activa del compañero y a distancia limitada. Si no es posible comprobar que esté libre, la orden falla en vez de intentar montar a ciegas.
- «Regresar a posición» usa la posición actual guardada, no un waypoint arbitrario del mapa.
- Al desactivar `EnableWaitMode`, la despedida utiliza el comportamiento ambiental anterior de deambular.

### Heridas y tratamiento

- El daño se observa en intervalos, no se provoca daño artificial para detectar la herida.
- El umbral se calcula a partir de cambios de salud dentro de una ventana temporal; los impactos repetidos no reinician un temporizador indefinidamente.
- Las heridas están asociadas al ID persistente y sobreviven a la pérdida temporal del ped. El código no trata un registro muerto como recuperable.
- El tratamiento se cobra como máximo una vez por interacción después de validar al objetivo. Puede configurarse para salud parcial o completa.
- La ralentización se intenta únicamente si la reflexión encuentra el wrapper de movimiento compatible. Si no está disponible, la herida se conserva y el log indica que la ralentización no se pudo aplicar.

### Posturas y afinidad

- Las posturas se conservan en el estado persistente. Los filtros se aplican a los reclutas de la extensión, no globalmente.
- «Fuego a mi señal» depende de consultar el estado de disparo del jugador. Si el wrapper no está disponible, el sistema falla de forma conservadora y no autoriza la ofensiva.
- La afinidad está limitada a 0–100 y sus incrementos de viaje, combate y tratamiento tienen límites temporales o de evento.
- La ficha presenta la afinidad como acción seleccionable `Confianza: n/100` y muestra el dato en un subtítulo. No se ha alterado el método gráfico `RosterMenu.DrawDetail()`.

## Limitaciones pendientes

1. **No compilado ni ejecutado en RDR2.** En el entorno usado no estaban disponibles MSBuild/compilador C# ni el `ScriptHookRDRNetAPI.dll` instalado. No debe interpretarse este paquete como una DLL de producción validada.
2. **Ropa, sombrero y armas.** Solo la persistencia de hash meta-outfit existente y su migración están conectadas. La persistencia independiente de componentes de ropa, sombreros y armas sigue pendiente. Los campos/opciones correspondientes son reservados y están desactivados por defecto.
3. **Integración visual de afinidad.** El valor se ve en la lista de acciones de la ficha, no dentro del panel gráfico original. Insertar una fila en el dibujo original requeriría otro parche y prueba visual.
4. **Native wrappers.** Los módulos resuelven algunas funciones opcionales por reflexión. Los parámetros/retornos deben comprobarse contra la API V2 instalada. Montar y regresar a una posición requieren pruebas con el juego y los nativos reales.
5. **Comportamiento de IA.** El estado evita reemitir algunas tareas constantemente, pero las tareas de caballo/espera pueden variar por modelo, animación o versión de juego. Validar que el NPC no quede bloqueado y que la IA de enRAGE no sobrescriba la orden.
6. **Waypoint del jugador.** La orden de posición guarda o actualiza la ubicación del compañero; no lee el waypoint del mapa. Es un fallback explícito.
7. **Compatibilidad de esquema.** El JSON actual usa versión 1. Las migraciones posteriores deben incrementar la versión y conservar los datos legados.

## Comprobaciones antes de usar

1. Compilar en Windows con la DLL de ScriptHook de la instalación objetivo.
2. Confirmar en el log que se resolvió `NemesisSystem.NemesisMod` y se instalaron todos los hooks obligatorios.
3. Probar en una partida secundaria: reclutar, despedir, llamar, cambiar facción, expulsar y volver a cargar la partida.
4. Probar espera y montaje con varios compañeros, con la montura ocupada, muerta, alejada o temporalmente inaccesible.
5. Probar heridas en salud parcial, recuperación, tratamiento con dinero suficiente/insuficiente, muerte, expulsión y reaparición.
6. Comprobar que los NPCs rendidos y civiles no reciben órdenes ofensivas automáticas y que `Fuego a mi señal` permanece bloqueado si el estado de disparo no puede detectarse.
7. Verificar afinidad y el JSON después de reiniciar el juego.
8. Verificar visualmente la captura/restauración de outfit. No considerar componentes, sombreros o armas persistentes hasta que haya una prueba específica.

Antes de probar, haz una copia del guardado de RDR2 y conserva los archivos auxiliares existentes.
