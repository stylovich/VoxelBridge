# Voxel Bridge

Herramientas de Editor para convertir mallas de Unity a `.vox`, mantener una escala voxel física coherente, editar en MagicaVoxel y generar prefabs con chunks y LODs.

La interfaz, los identificadores de código, los comentarios y los logs utilizan inglés. La documentación, los tooltips y los mensajes explicativos de la interfaz utilizan español.

## Requisitos y alcance

Copiar `Assets/VoxelBridge` junto con sus archivos `.meta` al proyecto. El módulo utiliza Unity 6000.3 con HDRP 17.3. La integración con Voxel Importer permite importar los `.vox` y construir los prefabs; Amplify Impostors 1.0.4 permite generar impostores opcionales. Los assets comerciales se instalan por separado.

La generación y la edición son procesos de autoría. Los prefabs contienen meshes y renderers convencionales; el módulo no implementa edición voxel en runtime.

El diagnóstico de entidades requiere Entities 1.4 y Entities Graphics 1.4, incluidos en la configuración DOTS del proyecto.

Las paletas, el transporte de `ColorID + SurfaceID` y las familias semánticas de producción se describen en [MATERIALS.md](MATERIALS.md). [ROADMAP.md](ROADMAP.md) distingue los sistemas disponibles de las fases pendientes: autoría visual de superficies, validación de producción, impostores semánticos y evaluación de HLOD y presupuestos.

## Herramientas

| Menú en `Tools > Voxel Bridge` | Función |
|---|---|
| `Physical Models and LODs` | Conversión con unidad física, familias LOD, lotes e impostores |
| `Combine Voxel Models` | Unión exacta de instancias semánticas en una familia editable independiente |
| `DOTS Stress Test` | Spawn manual de entidades, recursos compartidos y diagnóstico de frustum |
| `Resolution-Based Conversion` | Conversión individual con una resolución explícita |
| `VOX to Unity` | Sincronización de `.vox`, exportación semántica LOD0, familias de producción o retorno desde OBJ |
| `Bind Semantic IDs` | Asignación de ColorID y SurfaceID a los slots de un `.vox` |
| `Surface Painter` | Selección visual de voxels, asignación de SurfaceID y guardado recuperable de la fuente |
| `Global Palettes` | Administración de las paletas globales y sus LUT |
| `Sync All Generated VOX Assets` | Resincronización explícita de los `.vox` con sidecar |
| `Compatibility` | Aplicación controlada de los parches de integración |

`Bind Semantic IDs` edita slots completos. `Surface Painter` permite cambiar la superficie de una parte de esos voxels sin modificar su ColorID.

## Edición visual de superficies

1. Seleccionar un prefab de producción y pulsar `Edit Surfaces` en la fila del LOD correspondiente. LOD0 es el punto de partida recomendado. También se puede abrir `Tools > Voxel Bridge > Surface Painter` y asignar un `.vox` con sidecar semántico v4.
2. Comprobar que las LUT estén actualizadas. La previsualización utiliza una malla y un material temporales; no modifica las instancias de la escena. Las piezas `Keep Original`, incluido el vidrio retenido, quedan fuera de esta vista.
3. Elegir `Brush` o `Rectangle` en la barra de herramientas y marcar voxels con clic izquierdo o arrastre. `Replace` sustituye la selección, `Add` acumula celdas y `Subtract` las quita; mantener `Shift` al iniciar el gesto activa sustracción temporal. `Size` controla el diámetro de pantalla entre 1 y 128 píxeles de interfaz. `Alt` + arrastre o botón derecho rota la vista; botón central o `Alt+Shift` + arrastre izquierdo desplaza la cámara. La rueda permite acercarse a escala de celda. `Frame All` encuadra el volumen y `Frame Selection` centra la órbita en la selección.
4. Elegir `Surface` y pulsar `Apply Surface`. La asignación afecta al voxel completo, no sólo a la cara señalada. Conserva ColorID, ocupación, escala y pivote.
5. Utilizar `Undo` y `Redo`, o `Ctrl+Z` y `Ctrl+Y`/`Ctrl+Shift+Z`, para revisar asignaciones pendientes. Los atajos actúan sobre el historial local mientras `Surface Painter` tenga el foco y no se esté editando texto; fuera de la ventana continúa el Undo habitual de Unity. `Clear Selection` no elimina las asignaciones aplicadas.
6. Pulsar `Save & Rebuild` para guardar conjuntamente `.vox` y sidecar y reconstruir el LOD vinculado al prefab. La escritura conserva los `.meta`, reutiliza slots existentes y sólo crea otra entrada local cuando hace falta otro par ColorID + SurfaceID. Las instancias comparten la malla reconstruida.
7. Utilizar `Source Actions > Save Source Only` para guardar sin reconstruir. `Source Actions > Rebuild Edited LOD` o `Save & Rebuild` permiten actualizar después una fuente ya guardada. Los descendientes requieren revisión y regeneración explícita para heredar los cambios; sus retoques no se sobrescriben al guardar o reconstruir otro nivel. Sin un prefab vinculado sólo está disponible el guardado de fuente.

Los iconos de la barra permiten deshacer, rehacer y limpiar la selección; sus tooltips identifican cada acción. `Source Actions` reúne recarga, descarte y operaciones secundarias de guardado. `?` despliega los controles y las precauciones de intercambio externo. Los errores, conflictos y recuperaciones pendientes permanecen visibles sin abrir esa ayuda. La barra de estado muestra los contadores y permite consultar el último mensaje informativo mediante su tooltip.

La ventana mantiene una sola fuente, independientemente de la selección de la escena. Guardar reinicia el historial local. Cerrar o cambiar de fuente con cambios pendientes requiere guardar o descartar; cancelar conserva la sesión. Durante Play Mode, la herramienta conserva el borrador y deshabilita la edición. La serialización de la ventana conserva el borrador durante una recarga de scripts, pero reinicia Undo/Redo; no sustituye el guardado ni garantiza recuperar cambios sin guardar tras un cierre abrupto del Editor.

La selección se calcula por tandas y se confirma al soltar el ratón y terminar el cálculo. Durante el gesto, el resaltado muestra el resultado provisional; `Esc` o `Cancel Selection` conserva la selección anterior. Cambiar de ventana, redimensionarla, recargar scripts o entrar en Play Mode cancela la operación en curso. La cámara y los controles de edición permanecen bloqueados mientras se calcula. Seleccionar no cambia SurfaceID ni ColorID y no crea pasos en el historial de asignaciones.

El pincel interpola el recorrido del ratón; el rectángulo admite ambas direcciones de arrastre. Cada muestra de pantalla toma únicamente el primer voxel alcanzado, sin atravesar superficies. El muestreo tiene resolución de un píxel de interfaz: acercar la vista para incluir detalles subpíxel. El radio del pincel es de pantalla, no un radio tridimensional ni un número fijo de voxels.

Los cambios externos del `.vox`, sidecar o paletas bloquean el guardado para evitar sobrescrituras. `Source Actions > Reload Source` requiere resolver el borrador pendiente; `Discard Changes...` lo descarta explícitamente. No hay fusión automática con cambios de MagicaVoxel. La advertencia sobre slots con RGB idéntico y superficies distintas se consulta en `?` o `Source Actions > External Editing Notice`; ese intercambio externo permanece sin certificar.

El guardado conserva copias de recuperación bajo `Library/VoxelBridgeSurfaceEdits`. Una interrupción bloquea las lecturas semánticas de producción hasta ejecutar `Recover Interrupted Save`. La recuperación restaura ambos archivos originales y rechaza sobrescribir modificaciones externas posteriores. No borrar `Library` ni mover las fuentes mientras exista una recuperación pendiente. Un fallo de importación posterior al guardado informa que la fuente está guardada y requiere reimportación.

Límites: 131.072 celdas por selección, 1.048.576 celdas modificadas pendientes, historial de hasta 64 operaciones y 1.048.576 cambios de celda. El resaltado representa todas las caras expuestas de la selección, con contorno cuadrado y relleno tenue; no dibuja aristas interiores ni marcas a través de superficies opacas. La celda bajo el cursor utiliza cian. Las mallas temporales del resaltado se dividen en bloques y se reutilizan mientras no cambie la selección; no crean materiales por voxel ni modifican los assets. La asignación sigue afectando al voxel completo. Se mantienen los límites de lectura y meshing de producción. Superar 255 pares locales bloquea el guardado sin aproximar colores o superficies.

Un gesto admite hasta 1.048.576 muestras de pantalla únicas, 4.194.304 muestras contando repeticiones y 4.096 segmentos de arrastre. Superar un límite cancela el gesto completo, conserva la selección anterior e indica que se requiere un área o trazo menor; no confirma silenciosamente una selección parcial.

La ventana limita el multiplicador de emisión de su material temporal al rango `0..1`; sin material vinculado utiliza `1`. Conserva la emisión relativa de cada SurfaceID y la emisión apagada si el multiplicador original es cero. Esto permite identificar el color sin saturarlo por intensidades HDR altas; no simula exposición, bloom ni luminosidad física final. La ayuda `?` y el tooltip de las propiedades PBR describen esta limitación. Guardar o reconstruir no la transfiere al material de producción. Comprobar el resultado luminoso definitivo en la escena.

La vista utiliza HDRP Lit, no colores planos. Una superficie con `Metallic = 0` conserva respuesta especular; `Smoothness` influye en su apariencia bajo las luces de referencia. Comparar el brillo con otro editor, como MagicaVoxel, requiere considerar sus diferencias de sombreado e iluminación. No deducir un SurfaceID metálico únicamente por el aspecto de esa comparación; consultar la superficie asignada y sus valores PBR.

El guardado y la reconstrucción son etapas separadas: un fallo de reconstrucción conserva la fuente guardada y la malla anterior. Reintentar la reconstrucción después de resolver el error; Undo no revierte los archivos guardados.

La selección asistida de grupos y la preasignación por semejanza PBR son fases posteriores descritas en [ROADMAP.md](ROADMAP.md).

## Combinación de modelos voxel

1. Colocar dos o más prefabs de producción semántica en la misma escena. Seleccionar las instancias o un padre que las contenga.
2. Abrir `Tools > Voxel Bridge > Combine Voxel Models` o el menú contextual equivalente de GameObject. `Use Scene Selection` actualiza las entradas.
3. Asignar un `Voxel Profile` cuya unidad base coincida con el tamaño físico efectivo de las celdas. Mantener posiciones alineadas a la rejilla mundial y rotaciones ortogonales. Se admiten reflexiones y escalas uniformes únicamente cuando conservan esa unidad efectiva; no se remuestrean celdas.
   `Snap Sources to Grid` muestra una vista previa de posiciones y rotaciones mundiales. `Apply Snap` confirma el ajuste de las raíces; `Cancel Snap` descarta la propuesta. Seleccionar un hijo LOD también resuelve su raíz de producción.
4. Elegir `Family Name`, `Output Folder` y ejecutar `Analyze Combination`. El informe muestra dimensiones, ocupación, pares semánticos, solapamientos y prioridad de fuentes según el orden de la jerarquía.
5. Ejecutar `Create Combined Family`. Los solapamientos con IDs distintos requieren confirmar `Keep First Source`; los que comparten ambos IDs se unifican directamente.
6. Activar `Generate Derived LODs` para reducir el conjunto según el perfil. Desactivar para editar primero LOD0 y derivar los niveles desde el Inspector del prefab.

La herramienta lee los `.vox` LOD0 guardados, incluidos sus retoques, y crea `<Family>_VoxelLOD` con fuentes editables, sidecars, manifiesto, chunks y un único prefab con `LODGroup`. No conserva un vínculo de actualización con las familias de entrada. `Open in MagicaVoxel`, `Semantic Bindings` y `Rebuild` operan sobre las fuentes del conjunto. No se copian los LODs anteriores ni se generan impostores.

El snapping ajusta cada instancia por separado a la posición de rejilla y a la más cercana de las 24 orientaciones ortogonales. Conserva la escala local, los padres, los LODs internos y los archivos fuente. La operación admite Undo/Redo agrupado. Una escala o un origen de rejilla incompatibles impiden aplicar el ajuste a todo el grupo; no se corrigen deformando el modelo. Si cambian los transforms, la escena, el estado activo o la unidad del perfil después de la vista previa, se requiere una nueva propuesta. Revisar los solapamientos después del ajuste: las distancias y orientaciones relativas pueden cambiar.

`Place Result in Scene` coloca el resultado en la raíz de la escena, con rotación identidad y escala unitaria. Su pivote se ajusta a la rejilla cerca de la primera fuente, sin desplazar la geometría combinada. `Disable Source Instances` desactiva únicamente las instancias incluidas, no sus padres. La colocación y desactivación admiten Undo/Redo; la creación de assets no se deshace mediante Undo.

### Compatibilidad y límites

- Ambas paletas globales y sus revisiones deben coincidir. Revisar y guardar `Semantic Bindings` si una fuente referencia una revisión anterior; no se reinterpretan IDs entre bibliotecas.
- Las piezas `Keep Original` conservan su colocación y materiales, con copias de sus meshes dentro de la nueva familia. No se voxelizan ni simplifican. No se transfieren scripts, luces ni colliders.
- Las modificaciones de geometría, materiales o transforms internos de una instancia requieren editar la fuente y reconstruirla; mover, rotar o escalar su raíz sí está permitido dentro de las restricciones de la rejilla.
- Se mantienen los límites de producción: 8.000.000 de celdas en el volumen, 4.000.000 de voxels ocupados en la importación inicial, 255 pares por `.vox`, 64 MiB por fuente y 500.000 quads por nivel. La división en chunks no elimina estos límites.
- Mantener agrupaciones compactas. El LODGroup gobierna todo el conjunto; ampliar sus bounds puede perjudicar el culling. La primera versión no admite fuentes de escenas diferentes, selección directa de assets de Project, Prefab Mode ni remuestreo de rejillas incompatibles.

Un error o cancelación elimina solamente la nueva carpeta incompleta. Las fuentes permanecen intactas. La combinación vuelve a analizar los archivos y transforms al crear el resultado; un informe anterior no autoriza datos desactualizados.

## Conversión física y LODs

1. Abrir `Physical Models and LODs`.
2. Seleccionar un FBX, OBJ, prefab, GameObject o Mesh como `Source Model`.
3. Crear o seleccionar un `Voxel Style Profile`.
4. Configurar `Base Voxel Size` en unidades de Unity. El valor predeterminado `0.032` equivale a 3,2 cm cuando una unidad representa un metro.
5. Configurar multiplicadores LOD crecientes en potencias de dos, comenzando en `1`, por ejemplo `1, 2, 4`. Cada LOD requiere una transición explícita y válida.
6. Elegir `Generate Levels`: `LOD0 Only` para preparar la autoría semántica, o `All Profile Levels` para voxelizar cada nivel desde la malla original. La selección se aplica a la conversión individual y por lotes, independientemente de `Maximum Allowed Base`.
7. Asignar `Conversion Profile` para utilizar colores y superficies globales. Regenerar las LUT desactualizadas desde `Global Palettes`.
8. Elegir el origen del color, la carpeta de familias y generar el resultado. Con perfil, la salida es el prefab semántico de producción; `Place Result in Scene` coloca ese mismo prefab. Sin perfil, la salida es una previsualización RGB de Voxel Importer.

Cada LOD automático se voxeliza desde la malla fuente. Con multiplicadores `1, 2, 4`, las celdas utilizan `base`, `base × 2` y `base × 4`. Todas las rejillas comparten la alineación física.

### Reglas de conversión

Crear `Assets > Create > Voxel Bridge > Conversion Profile` y asignarlo en `Conversion Profile`, dentro de `Physical Models and LODs`. El perfil requiere un `Color Mapping Profile` y una paleta global de superficies guardados como assets. Las reglas se comparten entre la conversión individual y por lotes.

| Acción | Resultado |
|---|---|
| `Voxelize` | Convertir el submesh y asignar sus ColorID y SurfaceID |
| `Ignore` | Excluir la geometría de la rejilla y del resultado |
| `Keep Original` | Excluir la geometría de la rejilla y conservar una copia estática con sus materiales originales en el prefab |

`Material Rules` identifica los materiales por referencia, no por nombre. Para una excepción por objeto, utilizar `Add Component > Voxel Bridge > Conversion Rule`. La regla habilitada más cercana al renderer tiene prioridad; `Apply To Children` permite heredarlas dentro de la raíz de conversión. `Surface ID = -1` conserva la asignación del material o permite la detección automática.

Las reglas de componentes aplicadas a instancias de prefabs se consideran overrides. Para utilizarlas sin modificar el prefab fuente, seleccionar `Modified Instances > Convert Instance Separately` en lotes. La opción de utilizar el prefab original descarta esos overrides de forma intencional.

Los `.vox` generados con perfil contienen vinculaciones semánticas. La carpeta de la familia contiene sus fuentes, manifiesto, mallas de chunks y un único prefab LOD final, sin otro prefab de previsualización. El Inspector del prefab ofrece `Open in MagicaVoxel`, `Semantic Bindings` y `Rebuild` para cada nivel. Sin perfil, la conversión conserva el flujo RGB y permite reglas de componentes `Ignore` y `Keep Original`, pero no SurfaceID explícitos.

Las ventanas requieren un objeto o submesh identificable. Excluirlas antes del relleno permite conservar el interior si la resolución mantiene una abertura real. La geometría retenida se almacena como `RetainedGeometry.prefab` y meshes en la carpeta de la familia; el sidecar y el manifiesto conservan su GUID. Cada LOD utiliza esa geometría sin simplificar y participa en su transición y descarte. No se copian scripts, colisiones ni animaciones; un renderer skinned se conserva en su pose horneada. Reconvertir la fuente para actualizar la selección de piezas retenidas.

Una fuente debe contener al menos un submesh para voxelizar. `Resolution-Based Conversion` admite exclusión mediante componentes, pero remite al flujo físico para `Keep Original` y SurfaceID. La detección de emisión y sus límites se describen en [MATERIALS.md](MATERIALS.md#superficies-durante-la-conversión).

### Límites y modelos grandes

La división en chunks ocurre después de calcular y voxelizar la rejilla completa. Evita superar las dimensiones de un modelo interno de MagicaVoxel, pero no elimina el coste de la rejilla global.

`Adapt Voxel Size Automatically` en la conversión individual, y `Adapt Resolution Automatically` en lotes, permiten iniciar una familia con una unidad mayor. `Maximum Allowed Base` limita el nivel global que puede utilizarse. Una base de `0.032 m` con máximo LOD2 permite comenzar en `0.032`, `0.064` o `0.128 m`.

Si una familia comienza en `base × 4`, sus siguientes LODs utilizan `×8`, `×16`, etc. El manifiesto conserva la unidad global y el multiplicador inicial para la reducción manual.

Se aplican dos presupuestos independientes:

- `Temporary Memory Budget (MiB)` o `Per-Model Budget (MiB)`: pico estimado por fuente; valor predeterminado de 1024 MiB.
- `Maximum Imported Voxels`: ocupación real máxima del LOD0 antes de escribir archivos; valor predeterminado de 4.000.000.

La herramienta intenta la menor unidad permitida que satisface los límites. Si ninguna sirve, excluye la fuente sin crear una familia parcial. La estimación de memoria debe ajustarse a la memoria disponible y a la carga del Editor; no representa una garantía de consumo total.

### Colocación en escena

`Place Result in Scene` instancia el prefab generado como hermano de la fuente. Conserva su transform, layer, tag y flags Static. `Disable Original Object` desactiva la fuente después de completar la conversión y, si corresponde, el horneado del impostor. La operación admite Undo. Una fuente seleccionada desde Project sólo genera assets.

`Snap Pivots on Placement` ajusta la posición mundial al múltiplo más cercano de la unidad base. El desplazamiento máximo es media celda por eje. No introduce un desplazamiento aleatorio.

### Lotes y reutilización

`Batch Generation` procesa los hijos directos del `Parent Object`; cada hijo puede contener una jerarquía de mallas.

- `Ignore Inactive Objects` excluye objetos desactivados y geometría desactivada dentro de cada fuente.
- `Reuse Source Prefab` comparte una conversión entre instancias equivalentes del mismo prefab. Las variantes se consideran fuentes distintas.
- `Modified Instances` permite utilizar el prefab original, convertir la jerarquía modificada como fuente independiente o ignorarla. Ninguna opción modifica el prefab fuente.
- Los cambios de transform del root se recuperan durante la colocación y no provocan por sí solos otra voxelización.
- El plan visible distingue conversiones, reutilizaciones y omisiones antes de comenzar.

`Analyze Batch Memory` muestra las fuentes únicas y su estimación. Los fallos individuales se registran y el lote continúa con las fuentes restantes.

`Resume Interrupted Batch` reutiliza familias completadas desde `Library/VoxelBridge/BatchCheckpoints` cuando la fuente, el perfil y las opciones coinciden. `Clean Up Every N Families` libera assets sin uso; `1` reduce la acumulación de memoria. El checkpoint se elimina al completar el lote.

`Find and Clean Incomplete Outputs` elimina únicamente carpetas con la marca privada de construcción incompleta. Las familias terminadas y las carpetas manuales quedan fuera de esa limpieza.

La colocación por lotes crea otra raíz con instancias voxel en los transforms de sus fuentes. `Disable Original Root` sólo realiza el reemplazo si todos los hijos relevantes terminan correctamente. Ante errores, cancelaciones o hijos activos sin representación, la nueva raíz permanece desactivada. Este reemplazo está destinado a jerarquías visuales: no copia scripts ni componentes de lógica del padre.

## Familias y edición manual

Cada familia se almacena en `Assets/VoxelBridgeExports/<Model>_VoxelLOD/` e incluye:

- un `.vox` editable por LOD;
- un sidecar `.voxelbridge.json` con escala, pivote, rejilla y chunks;
- un manifiesto `.voxset.json` que relaciona niveles, perfil y prefab;
- un prefab con el nombre del modelo, un `LODGroup` y los renderers de cada nivel.

El archivo `.vox` sigue siendo editable aunque Unity muestre el icono de GameObject generado por Voxel Importer.

`Edit Family LODs` abre la lista de niveles desde el prefab, un `.vox`, el manifiesto o un GameObject de la familia. Cada nivel permite seleccionar su archivo, abrirlo en MagicaVoxel y utilizarlo como padre del siguiente LOD. El menú contextual `Edit This LOD in MagicaVoxel` abre directamente el nivel seleccionado en la jerarquía.

El modo manual ofrece dos operaciones:

- `Duplicate Previous (Free Editing)`: copiar el archivo para una simplificación artística libre.
- `Reduce to Target Resolution`: remuestrear el volumen editado a la unidad del nuevo nivel.

En un volumen semántico, la reducción conserva el par `ColorID + SurfaceID` mayoritario por celda y resuelve empates de forma determinista.

`Rebuild Prefab from Manifest` reconstruye la familia y resincroniza sus imports. Conserva la ruta registrada y el GUID del prefab, incluso si se ha reubicado. Un perfil o manifiesto requerido que falta produce un error; la herramienta no inventa transiciones ni sustituye la familia silenciosamente.

Las conversiones con `Conversion Profile` generan la familia semántica de producción directamente. Para un `.vox` externo o convertido sin perfil, vincular los IDs de LOD0 y utilizar `Semantic Production LOD Family` en `VOX to Unity`. Su prefab permite editar, reconstruir, duplicar y reducir niveles individualmente. Esta ruta utiliza el mesher propio y conserva chunks para culling. Consultar el [flujo de producción](MATERIALS.md#familias-semánticas-de-producción).

## Transiciones y sombras

`Transition Mode` permite utilizar porcentajes fijos de pantalla o adaptación por tamaño. El modo adaptativo aplica la relación entre el tamaño del modelo y `Reference Model Size`, modulada por `Size Adaptation Strength` y sus factores mínimo y máximo.

Para modelos mayores que `Large Model Threshold`, el refuerzo adicional adelanta los niveles de menor resolución. Un porcentaje mayor cambia al siguiente LOD a menos distancia. Con un impostor final, los rangos intermedios se amplían de forma controlada; en estructuras grandes, una curva voxel `75 / 45 / 25` puede producir fronteras `75 / 55 / 35` antes del impostor.

Todos los prefabs generados utilizan `Fade Mode = None`, sin animación ni ancho de cross-fade.

`Reduce Shadows by Size` utiliza el tamaño local del prefab:

- Por debajo de `Last LOD Shadow Threshold` —1 m por defecto— se desactivan las sombras del último LOD voxel y del impostor.
- Por debajo de `Penultimate LOD Shadow Threshold` —0,5 m por defecto— se desactivan desde el penúltimo LOD.

La escala individual de una instancia no recalcula estas decisiones de autoría.

## Impostores opcionales

`Generate Final Impostor` está desactivado por defecto. El horneado está disponible para familias RGB; las familias semánticas requieren un shader de captura compatible con las LUT y bloquean esta operación. Amplify hornea los renderers de LOD0 y añade un último nivel al `LODGroup`. La configuración central reside en `Assets/VoxelBridgeSettings/VoxelImpostorProfile.asset`.

| Perfil | Proyección | Atlas | Vistas | Uso orientativo |
|---|---|---|---|---|
| `Low · Performance` | HemiOctahedron | 512 | 8×8 | Props pequeños o muy lejanos |
| `Medium · Balanced` | Octahedron | 1024 | 12×12 | Vehículos terrestres y props |
| `High · Long Distance` | Octahedron | 2048 | 16×16 | Vehículos voladores y siluetas importantes |
| `Architecture · Background` | HemiOctahedron | 2048 | 16×16 | Edificios que no se observan desde abajo |

`Edit Settings` permite ajustar los perfiles compartidos. En lotes, `Select Profile by Size` utiliza los umbrales centrales; el tamaño mínimo predeterminado es 0,5 m. Los modelos menores pueden terminar con LOD voxel y descarte, sin impostor.

`Total Atlas Budget (MiB)` limita las estimaciones del lote actual. La política puede reducir calidades u omitir familias; no mide la residencia GPU real ni presupuestos acumulados entre lotes. Los horneados son secuenciales y comparten resultados entre instancias de la misma familia.

`Generate Pending Impostors in Folder` procesa familias sin impostor disponible. `Remove Final Impostor` lo elimina del LODGroup y marca la familia como excluida de la generación de pendientes. Conserva los archivos de atlas para una regeneración posterior. La acción explícita de generación permite incorporarlo de nuevo.

## Conversión por resolución y retorno OBJ

`Resolution-Based Conversion` genera un `.vox` individual según el número de celdas indicado para el eje principal, con máximo de 256 por eje. Resulta útil para una conversión aislada sin una familia física.

`VOX to Unity` permite aplicar escala y pivote al import directo. El retorno alternativo desde OBJ requiere el sidecar RGB v3 de la conversión original para recuperar la escala y los ejes. El OBJ no conserva los IDs semánticos del volumen; el retorno v4 no está habilitado por esa ruta.

Para un LOD0 con IDs asignados, `Create or Rebuild Production LOD0` genera un prefab con malla semántica propia y material HDRP compartido. Su Inspector y el menú contextual `Voxel Bridge > Production` permiten `Open in MagicaVoxel`, `Select Source VOX` y `Rebuild`. Guardar el `.vox` requiere una reconstrucción manual para actualizar la malla del prefab y todas sus instancias, conservando referencias y transforms. El flujo completo y sus límites se describen en [MATERIALS.md](MATERIALS.md#edición-desde-el-prefab).

`Fill Interior` añade ocupación a volúmenes cerrados. `Hide Enclosed Cavities` controla la eliminación de caras que miran hacia cavidades cerradas mediante Voxel Importer. Son decisiones distintas.

## Integraciones y mantenimiento

El parche de normales de Voxel Importer corrige la transformación de normales al aplicar una escala física. El parche de Amplify comprueba la integración con el pipeline de render. Ambos verifican la firma y el cuerpo esperado: un conflicto produce un aviso y deja el código sin modificar. Las acciones pueden repetirse después de reinstalar o actualizar el asset.

Los ajustes de escala, pivote y optimización se aplican a `.vox` con sidecar válido. Un archivo externo sin sidecar conserva la configuración del importador y no recibe automáticamente la escala ni la semántica del flujo. Los atlas de impostores generados por la integración se configuran con mipmaps y streaming; esta configuración no modifica las texturas de otros modelos.

Los formatos admitidos son sidecar v3 para RGB, sidecar v4 para datos semánticos y manifiesto v4. Los formatos antiguos o desconocidos requieren regenerar la conversión. La lectura comprueba tamaños de chunks, paleta explícita y correspondencia de modelos y límites: una edición fuera de la rejilla guardada se rechaza antes de reducir un LOD.

Los logs se reservan para fallos, conflictos, recuperación y resúmenes de acciones explícitas. Las importaciones correctas no generan un mensaje por archivo.

## Validación

### Prueba masiva en DOTS

1. Crear un GameObject vacío dentro de una subescena y añadir `Voxel Bridge > DOTS Stress Spawner`.
2. Asignar un **prefab de producción**, no un `.vox` importado. Configurar inicialmente `Count = 100`, `Columns = 10` e `Instances Per Frame = 16`. Ajustar `Spacing` por encima del tamaño del modelo para evitar solapamientos.
3. Guardar la subescena y entrar en Play Mode. Abrir `Tools > Voxel Bridge > DOTS Stress Test` y seleccionar el spawner del mundo predeterminado.
4. Pulsar `Spawn / Resume`. `Pause` detiene la creación sin eliminar instancias; `Clear Spawned` elimina únicamente las raíces creadas por ese spawner y sus grupos vinculados, de forma gradual.
5. Asignar `Frustum Camera`, pausar el spawn y esperar un frame antes de `Capture Resource and Frustum Snapshot`.
6. Comparar el mismo prefab con 100, 500 y 1.000 instancias. Limpiar primero para cambiar `Runtime Target Count`, columnas, separación o ritmo desde la ventana; estos valores no modifican el componente de autoría y se pierden al salir de Play Mode.

La distribución es una cuadrícula XZ centrada en la posición horneada del componente y orientada con su rotación. La escala del componente no se aplica; los clones conservan la escala del prefab. El spawn utiliza `EntityManager.Instantiate` y su `LinkedEntityGroup`, por lo que incluye los chunks, LODs y referencias remapeadas de cada modelo sin crear nuevos Materials o meshes.

El inicio es manual. Se permiten hasta 10.000 raíces y 250.000 entidades vinculadas por spawner; son límites de seguridad, no presupuestos calculados de RAM o VRAM. La eliminación del spawner o la descarga de su subescena activa la limpieza de sus instancias restantes. Utilizar varios spawners para comparar prefabs distintos. No cambiar el prefab ni efectuar rebaking durante una medición.

La ventana de control pertenece al Editor; no proporciona controles de spawn en un Player. Mantener los spawners de diagnóstico fuera de las escenas destinadas a producción.

El baker transfiere la configuración y la referencia al prefab. El sistema inicializa automáticamente la lista de seguimiento en runtime, incluso antes de iniciar el spawn. Esta lista de limpieza no se hornea: Live Baking no transfiere componentes de limpieza entre mundos. La ventana distingue la ausencia de una entidad horneada de una inicialización pendiente.

| Lectura | Interpretación |
|---|---|
| `Unique Materials / Meshes` | Identidades reales de recursos utilizados por las entidades de la prueba, incluidos todos sus LODs. La cantidad única debe permanecer estable al aumentar clones del mismo prefab. |
| `Inside / Outside Frustum` | Estimación por `WorldRenderBounds` respecto a la cámara seleccionada; no aplica selección de LOD ni oclusión y no representa draws efectivos. |
| `Entities Graphics — All Views` | Contadores nativos de rendering y culling para todo el mundo y sus callbacks. No aíslan este spawner, Game View ni una cámara concreta. |
| `Instance Data GPU Memory` | Memoria gestionada para datos de instancias, subidas y sincronización. No incluye toda la VRAM de texturas y meshes. |

La captura de recursos y frustum conserva el frame indicado hasta pulsar nuevamente `Capture Resource and Frustum Snapshot`; los contadores inferiores de Entities Graphics se actualizan por separado. Una referencia a un material de vidrio en la captura no implica que su LOD esté dibujándose. `Culled = 0%` en el LODGroup evita el descarte por tamaño relativo, pero no desactiva el frustum. Descartar renderers fuera de cámara no libera automáticamente sus recursos residentes.

Para comprobar el descarte, comparar la cámara mirando hacia el grupo y en dirección contraria, con idénticas condiciones. Evitar que Scene View u otras cámaras sigan mostrando el grupo. Los objetos fuera de Game View pueden seguir participando en sombras. Utilizar Profiler o Frame Debugger para confirmar los draws de la cámara y separar los pases de sombras. La captura de recursos sincroniza trabajos de ECS y puede alterar el tiempo de ese frame; no utilizar ese frame como medición de rendimiento.

### Pruebas automatizadas

Con el Editor cerrado, ejecutar las pruebas EditMode desde la raíz del proyecto:

```powershell
unity test . --mode EditMode --output Logs/voxel-bridge-tests.xml --timeout 600
```

Las pruebas verifican conversión, chunks, LODs, instancias, recuperación, paletas, transporte semántico y los parches de integración. Algunas pruebas de integración requieren los assets comerciales instalados. La evaluación visual de MagicaVoxel, HDRP/HTrace y DOTS debe realizarse sobre modelos representativos antes de producción.
