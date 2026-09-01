# Voxel Bridge

Herramienta de Editor para convertir mallas de Unity a `.vox`, mantener una escala voxel física coherente entre assets, editarlas en MagicaVoxel y recuperar el resultado como un prefab con chunks y `LODGroup`.

La planificación de combinación de modelos, materiales compartidos, integración DOTS, análisis espacial e impostores definitivos se describe en [ROADMAP.md](ROADMAP.md).

La configuración de las paletas globales de color y superficie se describe en [MATERIALS.md](MATERIALS.md). Esta infraestructura define IDs estables, genera las LUT del material compartido y permite vincular los slots utilizados por un `.vox` con pares `ColorID + SurfaceID`. El mesh de producción y el shader compartido se desarrollan en las fases siguientes de la hoja de ruta.

## Paletas globales

`Tools > Voxel Bridge > Paletas globales` crea o abre las paletas canónicas y permite regenerar sus LUT. Los assets se almacenan en `Assets/VoxelBridge/Palettes`.

- `ColorID` selecciona exclusivamente el color base.
- `SurfaceID` selecciona un perfil PBR compartido, por ejemplo aluminio, hormigón o plástico.
- Los IDs pertenecen al asset y no a la posición visible de una entrada. Reordenar o renombrar una entrada conserva su identidad.
- Eliminar una entrada retira su ID para impedir que un mesh existente cambie de significado de forma silenciosa.

Las LUT son assets generados. La edición debe realizarse en los ScriptableObjects y finalizar con `Regenerar LUT`.

## IDs semánticos en archivos `.vox`

`Tools > Voxel Bridge > Vincular IDs semánticos` abre la tabla de slots utilizados por un `.vox` generado por Voxel Bridge. La misma acción está disponible en el menú contextual del archivo.

Cada fila requiere un `ColorID` y un `SurfaceID`. Los colores que coinciden exactamente con una entrada global se preseleccionan; los demás permanecen sin asignar para evitar conversiones implícitas. Al guardar, la herramienta actualiza el RGBA del slot y escribe metadata semántica versionada en el sidecar `.voxelbridge.json`.

El sidecar conserva las referencias por GUID y ruta a ambas paletas, sus huellas de contenido, la tabla local y una copia RGBA de cada slot. La lectura se detiene ante IDs inexistentes, slots usados sin correspondencia, cambios de RGBA, hashes de tabla inválidos o remapeos `IMAP` no compatibles.

Los `.vox` y sidecars existentes permanecen en modo RGB legacy hasta vincularse explícitamente. Voxel Importer proporciona la previsualización de Unity, pero no se utiliza para reconstruir los IDs semánticos. La reducción manual de un LOD semántico conserva el par mayoritario de cada celda; la duplicación manual conserva el archivo y el sidecar sin reinterpretarlos.

Los chunks `NOTE` y `MATL` existentes se preservan sin convertirlos en fuente de identidad. Cuando dos slots comparten exactamente el mismo RGB pero utilizan superficies distintas, la tabla es válida dentro de Voxel Bridge, aunque su estabilidad después de volver a guardar el archivo en MagicaVoxel todavía requiere una prueba de round-trip específica.

## Flujo recomendado: perfil físico + LODs

1. Abre `Tools > Voxel Bridge > Modelos físicos y LODs`.
2. Crea un `VoxelStyleProfile` desde la ventana o mediante `Create > Voxel Bridge > Perfil de estilo voxel`.
3. Define `Base Voxel Size` en unidades de Unity. Para vóxeles de 10 cm usa `0.1`.
4. Define multiplicadores LOD estrictamente crecientes y en potencias de dos, por ejemplo `1, 2, 4, 8`.
5. Selecciona un FBX, OBJ, prefab u objeto de escena y pulsa el botón de generación. Habilita el impostor final únicamente cuando sea necesario.

La generación de impostores está desactivada de forma predeterminada. En la generación individual, `Generar impostor final` hornea el perfil seleccionado después de crear la familia voxel y añade el resultado como último nivel del `LODGroup`. Cuando también se utiliza `Colocar resultado en escena`, el horneado termina antes de instanciar el prefab.

Para una fuente perteneciente a una escena cargada, `Colocar resultado en escena` instancia el prefab voxel como hermano del GameObject original y conserva su transform, layer, tag y flags Static. `Desactivar objeto original` realiza el reemplazo visual después de completar correctamente la conversión y admite Undo. Si la conversión o el horneado del impostor falla, el objeto original permanece activo. Las fuentes seleccionadas desde Project solo generan assets y no modifican la escena.

La sección `Generación por lotes` procesa los hijos directos de un objeto padre. Cada hijo que contenga una malla —en sí mismo o en cualquiera de sus descendientes— utiliza el mismo perfil voxel, color y carpeta de salida. `Ignorar objetos desactivados` está activo de forma predeterminada: omite los hijos directos desactivados y excluye la geometría desactivada dentro de cada fuente. Al desactivarlo se incluye toda la jerarquía, independientemente de su estado. Los hijos directos sin mallas se omiten. Si un modelo falla, el lote informa el error en la Console y continúa con los demás. La misma operación está disponible en `GameObject > Voxel Bridge > Generar hijos como familias voxel y LOD`.

`Reutilizar prefab de origen` evita voxelizar varias veces instancias equivalentes: las instancias sin overrides del mismo prefab comparten una sola familia `.vox` y el mismo prefab convertido. Esto también funciona cuando las instancias están anidadas dentro de otro prefab; la herramienta resuelve el prefab reutilizable más cercano y conserva las variantes como fuentes distintas de su prefab base. Los cambios normales de posición, rotación y escala del root no cuentan como edición y se recuperan al colocar el resultado.

Para una instancia de prefab con overrides no predeterminados se puede elegir entre `Usar prefab original`, que descarta esos overrides solo en el resultado voxel; `Convertir como fuente separada`, que genera una familia exclusiva desde la jerarquía modificada; o `Ignorar instancia`. Ninguna opción sobrescribe ni modifica el prefab fuente o la instancia original.

El desplegable `Ver plan convertir / reutilizar / ignorar` muestra la decisión tomada para cada hijo antes de iniciar. Una fila `REUTILIZAR` no genera otra familia: al colocar el lote crea otra instancia del mismo prefab voxel con el Transform del objeto original. Una fila `CONVERTIR` sí genera una carpeta independiente.

Antes de voxelizar, `Analizar memoria del lote` calcula las rejillas de todas las fuentes únicas y estima su pico de memoria. El presupuesto predeterminado es 1024 MiB por modelo y debe ajustarse según la memoria disponible y la carga del Editor. `Adaptar unidad inicial` prueba, en orden, los tamaños equivalentes a LOD0, LOD1, LOD2 y hasta el límite seleccionado; utiliza el menor tamaño que produzca una rejilla válida dentro del presupuesto. Por ejemplo, una base de `0.024 m` y un límite LOD2 permite iniciar una familia en `0.024`, `0.048` o `0.096 m`, manteniendo así los tamaños sobre el mismo sistema de potencias de dos. `Máximo de vóxeles importados` limita por separado la ocupación real del LOD0; el valor predeterminado es 4.000.000. La herramienta mide este valor antes de escribir archivos y repite la voxelización con la siguiente base permitida cuando se supera. Si ni el nivel máximo satisface la rejilla, la memoria o la importación, la fuente se excluye y el lote continúa sin crear una familia parcial. Con la adaptación desactivada, `Omitir modelos sobre presupuesto` controla la estimación de memoria, mientras que una fuente que exceda el límite del importador se excluye sin cambiar de resolución.

Durante el proceso, cada familia terminada se registra en `Library/VoxelBridge/BatchCheckpoints`. Si Unity se cierra o el usuario cancela, `Reanudar lote interrumpido` recupera esas familias cuando la fuente, el perfil y las opciones coinciden, y continúa únicamente con las pendientes. El checkpoint se elimina al completar el lote. `Limpiar cada N familias` descarga assets sin uso y fuerza la recolección de memoria. El valor `1` prioriza la estabilidad; los valores `2–4` reducen la frecuencia de limpieza en lotes moderados.

`Generar impostor final` ejecuta una segunda fase después de la conversión voxel. El perfil de calidad seleccionado se aplica una sola vez por familia única; las instancias que reutilizan el mismo prefab comparten también el impostor. Los horneados se realizan en serie y liberan memoria entre familias. Si Amplify falla en una familia, el resultado voxel se conserva, el error se registra en la Console y el proceso continúa con las restantes.

Una familia en construcción contiene una marca privada de Voxel Bridge. Ante una excepción controlada se elimina en el momento; tras un cierre fatal, la siguiente ejecución o el botón `Buscar y limpiar salidas incompletas` elimina solamente carpetas que contengan esa marca. Las familias completas y las carpetas creadas manualmente no se tocan.

`Colocar resultado en escena` crea una raíz hermana con una instancia voxel por cada hijo convertido y conserva sus transforms, estado activo, layer y flags Static. Si `Ignorar objetos desactivados` está activo, los hijos directos omitidos por ese filtro no bloquean la sustitución. Si `Desactivar raíz original` está activo, la sustitución solo ocurre cuando todos los demás hijos directos tienen malla y todos los modelos terminan correctamente; ante cancelaciones, errores, instancias ignoradas o hijos activos sin malla, la nueva raíz queda desactivada y la original permanece activa. Toda la operación admite Undo. La raíz generada contiene la representación visual, pero no copia scripts, colliders ni otros componentes del padre original, así que el reemplazo automático debe usarse con raíces exclusivamente visuales.

Cada LOD automático se voxeliza de nuevo desde la malla fuente. Sin adaptación, LOD0 usa la unidad base, LOD1 usa `base × 2`, LOD2 `base × 4`, etc. En una familia adaptada, el LOD0 local comienza en el nivel elegido por el análisis y los siguientes conservan la progresión relativa; una familia iniciada en `base × 4` utiliza `×4`, `×8`, `×16`, etc. El manifiesto conserva tanto la unidad base global como el multiplicador inicial para que la reducción manual mantenga la misma escala. Las rejillas se alinean al mismo lattice físico para evitar cambios arbitrarios de tamaño o posición entre assets y niveles.

### Transiciones LOD por tamaño

`Modo de transición` controla cómo se aplican las alturas de pantalla definidas en `Lod Screen Heights`:

- `Fija por pantalla` utiliza los porcentajes sin modificarlos.
- `Adaptativa por tamaño` escala toda la curva según el tamaño final calculado por el `LODGroup`.

El modo adaptativo utiliza la relación `tamaño del modelo / tamaño de referencia`, elevada a la `Intensidad de adaptación`. Una intensidad de `0` equivale al modo fijo, `0.5` compensa los extremos manteniendo diferencias naturales entre tamaños y `1` aproxima las transiciones a distancias físicas similares. `Factor mínimo` y `Factor máximo` limitan esta curva base.

Los modelos que superan `Umbral de modelo grande` reciben una segunda adaptación gradual. `Intensidad adicional para grandes` controla su progresión y `Factor máximo para grandes` limita el resultado total. Los modelos iguales o menores que el umbral no cambian. Un factor de pantalla mayor hace que Unity pase al siguiente LOD más cerca de la cámara y reduce el tramo físico ocupado por los primeros niveles.

Para una curva base ajustada con un vehículo grande se recomienda comenzar con un tamaño de referencia de `4 m`, intensidad `0.5` y factores `0.35–2`. La configuración recomendada para estructuras utiliza un umbral de `6 m`, intensidad adicional `0.25` y factor máximo `2.5`. Con transiciones base `0.30 / 0.18 / 0.10`, un modelo de `0.5 m` obtiene aproximadamente `0.106 / 0.064 / 0.035`, un modelo de `6 m` conserva la adaptación original y una estructura muy grande queda limitada a `0.75 / 0.45 / 0.25`.

El manifiesto guarda el tamaño del grupo y las transiciones voxel calculadas por el perfil. Cuando existe un impostor, la construcción final conserva la transición de LOD0 y amplía los rangos posteriores. En modelos que permanecen dentro del factor máximo general, cada LOD voxel intermedio ocupa la mitad del rango original del nivel siguiente y el último LOD voxel ocupa un tercio del rango original del impostor; una curva `37 / 22 / 12 / 1` produce `37 / 17 / 8,33 / 1`.

Las estructuras que superan el factor máximo general utilizan progresivamente intervalos más cercanos y uniformes. La mezcla alcanza su valor completo al llegar al `Factor máximo para grandes`: la frontera del último LOD voxel se sitúa entre sus dos transiciones originales y las fronteras intermedias se distribuyen uniformemente desde LOD0. Con la curva voxel limitada a `75 / 45 / 25`, el LODGroup final utiliza `75 / 55 / 35` antes del impostor. Este reparto evita que LOD1 y LOD2 de estructuras grandes permanezcan activos durante distancias desproporcionadas.

Todos los prefabs generados utilizan `Fade Mode = None`, `Animate Cross-fading` desactivado y anchura de transición cero. Las fronteras representan cambios directos de LOD y permiten evaluar el pop-in con el mismo comportamiento previsto para DOTS.

La familia generada contiene:

- un `.vox` editable por cada LOD;
- un sidecar `.voxelbridge.json` por cada `.vox` con escala, pivote, rejilla y chunks;
- un manifiesto `.voxset.json` que relaciona toda la familia;
- un prefab estable en esa misma carpeta, con el `LODGroup`, un hijo por LOD y todos sus renderers de chunk.

Regenerar o sustituir un LOD actualiza el mismo prefab indicado por el manifiesto; no crea copias sucesivas del prefab.

Cada modelo queda completamente agrupado en `Assets/VoxelBridgeExports/Nombre_VoxelLOD/`: los `.vox`, sidecars, manifiesto y prefab viven juntos. `Reconstruir prefab desde manifiesto` reubica junto a los `.vox` cualquier prefab indicado por el manifiesto que esté fuera de la carpeta de la familia, conservando su GUID y sus referencias.

`Reconstruir prefab desde manifiesto` también resincroniza la escala, el pivote y la transformación de importación de todos los `.vox` de la familia. Úsalo para aplicar correcciones de compatibilidad a familias ya generadas sin volver a voxelizar el modelo fuente.

El menú contextual del prefab, de sus `.vox` y del manifiesto incluye `Voxel Bridge > Editar LODs de la familia`. La ventana muestra cada nivel con accesos para seleccionarlo, abrirlo directamente en MagicaVoxel o usarlo como padre del siguiente LOD manual.

El GameObject raíz y su archivo prefab usan solamente el nombre del modelo original; la carpeta conserva el sufijo `_VoxelLOD` para identificar la familia. En la jerarquía, haz clic derecho sobre un hijo de cualquier nivel y elige `Voxel Bridge > Editar este LOD en MagicaVoxel` para abrir exactamente su `.vox`. La opción `Editar LODs de la familia` abre la lista completa desde cualquier objeto perteneciente al `LODGroup`.

Unity muestra los `.vox` con el icono y la representación de un `GameObject` porque Voxel Importer genera sus mallas durante la importación. El archivo del disco sigue siendo `.vox`; no se reemplaza por el prefab. La sección `Resultados` muestra por separado la ruta del `.vox` editable y el prefab que debe colocarse en escena.

## Impostor final y perfiles de calidad

Con Amplify Impostors 1.0.4 instalado, la sección `Impostor final` hornea todos los chunks de LOD0 y añade el billboard resultante como último nivel del `LODGroup`. La herramienta utiliza una sola configuración central en `Assets/VoxelBridgeSettings/VoxelImpostorProfile.asset`; para cada modelo solo se elige un nivel de la lista:

- `Bajo · Rendimiento`: HemiOctahedron, atlas 512, 8×8 vistas, padding 12, 6 vértices y descarte 0.01. Úsalo para props pequeños o muy lejanos que no se observen desde abajo.
- `Medio · Equilibrado`: Octahedron, atlas 1024, 12×12 vistas, padding 32, 8 vértices y descarte 0.005. Es el valor recomendado para la mayoría de vehículos y props.
- `Alto · Gran distancia`: Octahedron, atlas 2048, 16×16 vistas, padding 48, 12 vértices y descarte 0.0015. Está pensado para vehículos voladores, objetos móviles importantes y modelos que pueden verse desde cualquier dirección.
- `Arquitectura · Fondo`: HemiOctahedron, atlas 2048, 16×16 vistas, padding 48, 10 vértices y descarte 0.0005. Concentra las capturas en el hemisferio superior para edificios, estructuras y siluetas del skyline que no se observan desde abajo.

El manifiesto de cada familia guarda el perfil seleccionado y la ventana lo recupera al abrir nuevamente su prefab, `.vox` o `.voxset.json`. `Editar valores` abre la configuración central para ajustar los cuatro perfiles compartidos; su Inspector también permite restaurar todos los valores recomendados con Undo.

`Generar impostores pendientes en la carpeta` procesa los manifiestos sin un impostor válido dentro de la carpeta de exportación seleccionada. Esta operación permite completar lotes voxel existentes sin repetir la voxelización y omite las familias que ya contienen un asset de impostor disponible.

Para vehículos terrestres usa `Medio` en tráfico o elementos secundarios y `Alto` cuando su silueta sea importante o la cámara tenga libertad vertical. Los vehículos voladores deben usar `Alto`, porque necesitan capturas de todo el objeto y pueden verse desde abajo. El movimiento no requiere otro tipo de asset, pero hace más visible el cambio angular: valida el pop-in con la velocidad máxima de cámara y vehículo.

Los grupos decorativos menores de un metro normalmente no justifican una cadena larga. Si LOD1 deja menos de unas 5-6 celdas en el eje principal, usa solamente LOD0 y después impostor o descarte; si todavía conserva una silueta reconocible, usa LOD0 → LOD1 → impostor/descarte. Para objetos únicos que desaparecen pronto suele ser más barato omitir el impostor. Resérvalo para grupos completos —por ejemplo un conjunto de basura o cajas— que se repitan muchas veces o deban seguir visibles a distancia. El impostor siempre se añade después del último LOD voxel definido por el `VoxelStyleProfile`, así que un perfil voxel de uno o dos niveles produce directamente esos dos flujos.

Al reducir la cantidad de LODs, el modo adaptativo puede partir de la misma curva general y compensar automáticamente el tamaño del prop. Para un perfil fijo específico, usa `{ 0.05 }` para LOD0 → impostor y `{ 0.15, 0.04 }` para LOD0 → LOD1 → impostor; el perfil de impostor Bajo terminará de descartarlo en 0.01. Si se omite el impostor, utiliza aproximadamente 0.01 como altura final de descarte y comprueba el resultado con la cámara real.

## LOD manual

Asigna el `.vox` del LOD padre en la sección `LOD manual` y elige uno de estos puntos de partida:

- `Duplicar anterior (libre)`: copia exactamente el `.vox` padre. Conserva el tamaño de vóxel del padre para que el artista pueda simplificar libremente en MagicaVoxel.
- `Reducir a resolución objetivo`: agrupa el volumen editado del padre sobre la rejilla física que corresponde al LOD destino. Ahorra el primer trabajo de reducción y luego permite retocarlo manualmente.

Ambas rutas reemplazan la entrada del nivel elegido en el manifiesto y reconstruyen el mismo prefab. Los archivos anteriores no se borran automáticamente para no destruir trabajo artístico; si se sustituye varias veces un nivel pueden quedar `.vox` huérfanos que se pueden revisar y eliminar manualmente.

## Modelos grandes y chunks

Cuando una rejilla supera `Chunk Cell Size`, Voxel Bridge escribe un único `.vox` VOX 200 con varios modelos y scene graph. Cada modelo ocupa como máximo 256 celdas por eje, conserva su posición global y se puede editar en MagicaVoxel como parte del mismo archivo.

Con Voxel Importer, un `.vox` con varios chunks se importa en modo `Individual`: el prefab conserva un renderer por chunk y el `LODGroup` referencia todos los renderers del nivel. Esta estructura permite culling granular. Los chunks no se combinan en una sola malla.

La fase de voxelización utiliza una rejilla densa global para mantener correctos el relleno interior y las fronteras entre chunks. El límite de seguridad es de 80 millones de celdas. Tanto la conversión individual como el flujo por lotes pueden adaptar la unidad inicial: prueban en orden las medidas definidas por los LOD del perfil y seleccionan la más fina que cumple el límite de rejilla y el presupuesto temporal. La conversión individual expone `Presupuesto temporal`, `Máximo de vóxeles importados` y `Base máxima permitida` bajo `Seguridad para modelos grandes`. Si ninguna base permitida satisface los límites, la operación se cancela antes de reservar la rejilla o crear una familia parcial.

## Conversión puntual por resolución

La conversión por resolución fija tiene una interfaz independiente en `Tools > Voxel Bridge > Conversión por resolución` y sirve para pruebas o assets aislados:

1. Selecciona la fuente.
2. Elige una resolución para el eje más largo.
3. Deja `Rellenar interior` activo para modelos cerrados. Desactívalo para láminas o superficies que deban permanecer huecas.
4. Pulsa `Convertir a un archivo .vox`.

La herramienta genera dos archivos:

- `modelo.vox`: volumen y paleta de hasta 255 colores.
- `modelo.voxelbridge.json`: escala, origen de la rejilla y correspondencia de ejes para el retorno.

No se modifica el FBX/OBJ original. Las mallas animadas se convierten en la pose visible en el momento de la exportación.

## MagicaVoxel a Unity con Voxel Importer (recomendado)

Si `Assets/VoxelImporter` está instalado, conserva el `.vox` como asset de Unity. Al guardar cambios desde MagicaVoxel, Unity lo reimporta y Voxel Bridge vuelve a aplicar automáticamente:

- el tamaño real de cada vóxel;
- el pivote y la orientación del modelo original;
- `Combine Voxel Faces` y `Share Same Face`;
- `Ignore Cavity` cuando `Ocultar cavidades cerradas` está activo.

La interfaz de retorno está separada en `Tools > Voxel Bridge > VOX a Unity`. Permite asignar un `.vox`, comprobar su ruta física, aplicar escala y pivote desde el sidecar, seleccionar el modelo importado o abrirlo en MagicaVoxel. `Tools > Voxel Bridge > Sincronizar todos los .vox` resincroniza todos los archivos generados.

Voxel Bridge necesita corregir las normales que Voxel Importer genera cuando `Import Scale` contiene ejes negativos. Después de instalar o actualizar Voxel Importer usa `Tools > Voxel Bridge > Compatibilidad > Aplicar parche de normales de Voxel Importer`. La acción es idempotente y reimporta automáticamente los `.vox` generados. Si una versión nueva cambia el código esperado, Voxel Bridge no modifica el asset y muestra un warning para que el conflicto se revise manualmente.

Para editar cualquier `.vox`, selecciónalo en Project y usa `Assets > Voxel Bridge > Abrir en MagicaVoxel`. La primera vez se solicitará la ubicación de `MagicaVoxel.exe`; Voxel Bridge recordará esa ruta para las siguientes aperturas. El mismo botón está disponible en las tres interfaces cuando existe una salida `.vox` válida.

Voxel Importer genera mallas normales durante la importación; no dibuja cubos individuales ni mantiene vóxeles editables en runtime. Conservar los `.vox` mantiene la actualización automática al guardar desde MagicaVoxel. El prefab generado referencia los renderers importados desde esos archivos.

## MagicaVoxel OBJ a Unity (alternativa)

1. En MagicaVoxel guarda los cambios y usa `Export > obj`.
2. Guarda el OBJ y sus texturas dentro de una carpeta bajo `Assets`.
3. Abre `Tools > Voxel Bridge > VOX a Unity` y, en la sección alternativa de OBJ, asigna el modelo importado y el `.voxelbridge.json` correspondiente.
4. Mantén `MagicaVoxel Default` salvo que hayas cambiado los ejes de exportación en `config.txt`.
5. Pulsa `Crear prefab desde OBJ`.

El prefab conserva el pivote local y escala cada unidad del OBJ al tamaño del vóxel original. Si se cambiaron las opciones `io_*` de MagicaVoxel, usa `Already Unity Aligned` o ajusta la rotación del hijo una vez creado.

## Consideraciones

- El formato VOX almacena como máximo 256 celdas por eje y 255 colores de paleta por modelo. Voxel Bridge usa varios modelos dentro del mismo archivo cuando hace falta.
- Los detalles más pequeños que un vóxel desaparecerán; esta simplificación es parte del resultado estético.
- El relleno depende de que la superficie sea razonablemente cerrada. Agujeros en la malla pueden dejar el interior vacío.
- `Rellenar interior` desactivado crea una carcasa de un vóxel de grosor. Sus caras internas son reales. `Ignore Cavity` oculta solo cavidades cerradas; una ventana, puerta o grieta conecta la cavidad con el exterior. Para eliminar siempre las caras internas, usa relleno sólido.
- El muestreo de texturas se limita internamente a 512 px por material para evitar picos innecesarios de memoria.
- Cada LOD se cuantiza de forma independiente. La herramienta no genera una paleta compartida entre LODs o familias.
- `Assets/VoxelImporter` es una dependencia opcional del Asset Store y no forma parte del módulo portátil. Instálala por separado antes de aplicar su parche de compatibilidad.
