# Voxel Bridge

Herramientas de Editor para convertir mallas de Unity a `.vox`, mantener una escala voxel física coherente, editar en MagicaVoxel y generar prefabs con chunks y LODs.

La interfaz, los identificadores de código, los comentarios y los logs utilizan inglés. La documentación, los tooltips y los mensajes explicativos de la interfaz utilizan español.

## Requisitos y alcance

Copiar `Assets/VoxelBridge` junto con sus archivos `.meta` al proyecto. El módulo utiliza Unity 6000.3 con HDRP 17.3. La integración con Voxel Importer permite importar los `.vox` y construir los prefabs; Amplify Impostors 1.0.4 permite generar impostores opcionales. Los assets comerciales se instalan por separado.

La generación y la edición son procesos de autoría. Los prefabs contienen meshes y renderers convencionales; el módulo no implementa edición voxel en runtime.

El diagnóstico de entidades requiere Entities 1.4 y Entities Graphics 1.4, incluidos en la configuración DOTS del proyecto.

Las paletas, el transporte de `ColorID + SurfaceID` y las familias semánticas de producción se describen en [MATERIALS.md](MATERIALS.md). Los impostores semánticos y la validación DOTS figuran en [ROADMAP.md](ROADMAP.md).

## Herramientas

| Menú en `Tools > Voxel Bridge` | Función |
|---|---|
| `Physical Models and LODs` | Conversión con unidad física, familias LOD, lotes e impostores |
| `Combine Voxel Models` | Unión exacta de instancias semánticas en una familia editable independiente |
| `DOTS Stress Test` | Spawn manual de entidades, recursos compartidos y diagnóstico de frustum |
| `Resolution-Based Conversion` | Conversión individual con una resolución explícita |
| `VOX to Unity` | Sincronización de `.vox`, exportación semántica LOD0, familias de producción o retorno desde OBJ |
| `Bind Semantic IDs` | Asignación de ColorID y SurfaceID a los slots de un `.vox` |
| `Global Palettes` | Administración de las paletas globales y sus LUT |
| `Sync All Generated VOX Assets` | Resincronización explícita de los `.vox` con sidecar |
| `Compatibility` | Aplicación controlada de los parches de integración |

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

Para comprobar el descarte, comparar la cámara mirando hacia el grupo y en dirección contraria, con idénticas condiciones. Evitar que Scene View u otras cámaras sigan mostrando el grupo. Los objetos fuera de Game View pueden seguir participando en sombras. Utilizar Profiler o Frame Debugger para confirmar los draws de la cámara y separar los pases de sombras. La captura de recursos sincroniza trabajos de ECS y puede alterar el tiempo de ese frame; no utilizar ese frame como medición de rendimiento.

### Pruebas automatizadas

Con el Editor cerrado, ejecutar las pruebas EditMode desde la raíz del proyecto:

```powershell
unity test . --mode EditMode --output Logs/voxel-bridge-tests.xml --timeout 600
```

Las pruebas verifican conversión, chunks, LODs, instancias, recuperación, paletas, transporte semántico y los parches de integración. Algunas pruebas de integración requieren los assets comerciales instalados. La evaluación visual de MagicaVoxel, HDRP/HTrace y DOTS debe realizarse sobre modelos representativos antes de producción.
