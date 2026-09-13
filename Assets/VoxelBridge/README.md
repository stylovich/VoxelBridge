# Voxel Bridge

Herramientas de Editor para convertir mallas de Unity a `.vox`, mantener una escala voxel física coherente, editar en MagicaVoxel y generar prefabs con chunks y LODs.

La interfaz, los identificadores de código, los comentarios y los logs utilizan inglés. La documentación, los tooltips y los mensajes explicativos de la interfaz utilizan español.

## Requisitos y alcance

Copiar `Assets/VoxelBridge` junto con sus archivos `.meta` al proyecto. El módulo utiliza Unity 6000.3 con HDRP 17.3. La integración con Voxel Importer permite importar los `.vox` y construir los prefabs; Amplify Impostors 1.0.4 permite generar impostores opcionales. Los assets comerciales se instalan por separado.

La generación y la edición son procesos de autoría. Los prefabs contienen meshes y renderers convencionales; el módulo no implementa edición voxel en runtime.

El diagnóstico de entidades requiere Entities 1.4 y Entities Graphics 1.4, incluidos en la configuración DOTS del proyecto.

Las paletas, el transporte de `ColorID + SurfaceID` y las familias semánticas de producción se describen en [MATERIALS.md](MATERIALS.md). [ROADMAP.md](ROADMAP.md) distingue los sistemas disponibles de las fases pendientes de autoría, validación de producción, impostores semánticos y evaluación de HLOD y presupuestos.

El shader opaco de producción ofrece una [rejilla superficial opcional](ART_DIRECTION_AND_DETAIL.md#rejilla-superficial-de-producción), fija o multiescala y con anclaje mundial o local de familia. El acabado avanzado y los anuncios separados de su estructura permanecen como fases posteriores.

## Herramientas

| Menú en `Tools > Voxel Bridge` | Función |
|---|---|
| `Physical Models and LODs` | Conversión con unidad física, familias LOD, lotes e impostores |
| `Combine Voxel Models` | Unión exacta de instancias semánticas en una familia editable independiente |
| `DOTS Stress Test` | Spawn manual de entidades, recursos compartidos y diagnóstico de frustum |
| `Resolution-Based Conversion` | Conversión individual con una resolución explícita |
| `VOX to Unity` | Sincronización de `.vox`, exportación semántica LOD0, familias de producción o retorno desde OBJ |
| `Bind Semantic IDs` | Asignación de ColorID y SurfaceID a los slots de un `.vox` |
| `Surface Painter` | Selección visual, edición de ColorID/SurfaceID, comparación de acabados y guardado recuperable |
| `Global Palettes` | Administración de las paletas globales y sus LUT |
| `Sync All Generated VOX Assets` | Resincronización explícita de los `.vox` con sidecar |
| `Compatibility` | Aplicación controlada de los parches de integración |

`Bind Semantic IDs` edita slots completos. `Surface Painter` permite cambiar ColorID o SurfaceID de una parte de esos voxels, conservando el otro atributo.

## Edición visual de superficies

1. Seleccionar un prefab de producción y pulsar `Edit` en la fila del LOD correspondiente (`Edit Surfaces` en una salida independiente), o abrir `Tools > Voxel Bridge > Surface Painter`, arrastrar el prefab padre desde Project a `Source` y elegir un nivel en `Edit LOD`. El menú muestra los niveles existentes, no genera otros. LOD0 es el punto de partida recomendado. También se puede asignar un `.vox` con sidecar semántico v4 para editar sólo la fuente.
2. Comprobar que las LUT estén actualizadas. La previsualización utiliza mallas y materiales temporales; no modifica las instancias de la escena. Las piezas `Keep Original`, incluido el vidrio retenido, quedan fuera de esta vista.
3. Elegir `Brush` o `Rectangle` en la barra de herramientas y marcar voxels con clic izquierdo o arrastre. `Replace` sustituye la selección, `Add` acumula celdas y `Subtract` las quita; mantener `Shift` al iniciar el gesto activa sustracción temporal. `Size` controla el diámetro de pantalla entre 1 y 128 píxeles de interfaz. `Alt` + arrastre o botón derecho rota la vista; botón central o `Alt+Shift` + arrastre izquierdo desplaza la cámara. La rueda permite acercarse a escala de celda. `Frame All` encuadra el volumen y `Frame Selection` centra la órbita en la selección.
4. Elegir `Surface` y pulsar `Apply Surface` para conservar ColorID, o elegir `Color` y pulsar `Apply Color` para conservar SurfaceID. La asignación afecta al voxel completo, no sólo a la cara señalada. Ambas operaciones conservan ocupación, escala y pivote.
5. Utilizar `Undo` y `Redo`, o `Ctrl+Z` y `Ctrl+Y`/`Ctrl+Shift+Z`, para revisar selecciones y asignaciones pendientes en orden cronológico. Los atajos actúan sobre el historial local mientras `Surface Painter` tenga el foco y no se esté editando texto; fuera de la ventana continúa el Undo habitual de Unity. `Clear Selection` no elimina las asignaciones aplicadas y permite recuperar la selección mediante Undo.
6. Pulsar `Save & Rebuild` para guardar conjuntamente `.vox` y sidecar y reconstruir el LOD vinculado al prefab. La escritura conserva los `.meta`, reutiliza slots existentes y sólo crea otra entrada local cuando hace falta otro par ColorID + SurfaceID. Las instancias comparten la malla reconstruida.
7. Utilizar `Source Actions > Save Source Only` para guardar sin reconstruir. `Source Actions > Rebuild Edited LOD` o `Save & Rebuild` permiten actualizar después una fuente ya guardada. Los descendientes requieren revisión y regeneración explícita para heredar los cambios; sus retoques no se sobrescriben al guardar o reconstruir otro nivel. Sin un prefab vinculado sólo está disponible el guardado de fuente.

Los iconos de la barra permiten deshacer, rehacer y limpiar la selección; sus tooltips identifican cada acción. `Source Actions` reúne recarga, descarte y operaciones secundarias de guardado. `?` despliega los controles y las precauciones de intercambio externo. Los errores, conflictos y recuperaciones pendientes permanecen visibles sin abrir esa ayuda. La barra de estado muestra los contadores y permite consultar el último mensaje informativo mediante su tooltip.

La ventana mantiene una sola fuente, independientemente de la selección de la escena. Guardar reinicia el historial local. Cerrar o cambiar de fuente con cambios pendientes requiere guardar o descartar; cancelar conserva la sesión. Durante Play Mode, la herramienta conserva el borrador y deshabilita la edición. La serialización de la ventana conserva el borrador durante una recarga de scripts, pero reinicia Undo/Redo; no sustituye el guardado ni garantiza recuperar cambios sin guardar tras un cierre abrupto del Editor.

La selección se calcula por tandas y se confirma al soltar el ratón y terminar el cálculo. Durante el gesto, el resaltado muestra el resultado provisional; `Esc` o `Cancel Selection` conserva la selección anterior. Cambiar de ventana, redimensionarla, recargar scripts o entrar en Play Mode cancela la operación en curso. La cámara y los controles de edición permanecen bloqueados mientras se calcula. Cada selección confirmada que cambia el conjunto de voxels crea un paso de Undo; cancelar o repetir la misma selección no crea pasos. Seleccionar no cambia SurfaceID ni ColorID ni marca la fuente como pendiente de guardar.

El pincel interpola el recorrido del ratón; el rectángulo admite ambas direcciones de arrastre. Cada muestra de pantalla toma únicamente el primer voxel alcanzado, sin atravesar superficies. El muestreo tiene resolución de un píxel de interfaz: acercar la vista para incluir detalles subpíxel. El radio del pincel es de pantalla, no un radio tridimensional ni un número fijo de voxels.

### Cuentagotas y selección asistida

- `Pick`: pulsar un voxel visible para consultar su ColorID, muestra de color y SurfaceID. El tooltip de la muestra incluye coordenadas y propiedades PBR. Carga como destino el atributo activo en `Surface` o `Color`, sin modificar la selección ni aplicar cambios.
- `Match`: elegir `ColorID`, `SurfaceID` o `Similar Color` y pulsar el voxel de referencia. ColorID ignora la superficie; SurfaceID ignora el color. `Similar Color` compara los colores globales mediante distancia OKLab, en la misma escala que los perfiles cromáticos, sin alterar los IDs.
- `Connected` limita la búsqueda a vecinos por caras que cumplan el criterio; no une esquinas ni cruza colores o superficies rechazados. `All Matching` busca coincidencias en todo el volumen. La tolerancia siempre se compara con el color inicial, no con el último vecino, para evitar extenderse gradualmente por un degradado.
- `Visible Only`, activado por defecto, exige un centro de cara expuesta visible desde la cámara. No atraviesa oclusores ni incluye interiores. Una cara parcialmente visible cuyo centro esté oculto puede quedar excluida: cambiar el encuadre o utilizar el pincel para estos detalles. Desactivarlo incluye coincidencias ocultas, interiores y fuera de cámara; la interfaz indica `Includes Hidden`.
- `Replace`, `Add`, `Subtract` y `Shift` al iniciar conservan su significado. La búsqueda muestra celdas comprobadas y admite cancelación; superar el límite de selección conserva la selección anterior, sin aceptar resultados parciales. Las búsquedas grandes pueden requerir varios frames.

El muestreo de `Match` no cambia el atributo elegido como destino. `Use Surface` o `Use Color` permite tomarlo explícitamente desde la muestra. Después de revisar el perímetro y el contador, utilizar `Apply Surface` o `Apply Color` y `Save & Rebuild`. Cambiar el criterio o la tolerancia requiere otro clic; no modifica retroactivamente una selección confirmada. El historial local combina selecciones de pincel, rectángulo y Match, limpieza de selección y asignaciones de colores y superficies en orden cronológico. Los botones Undo/Redo y sus atajos operan sobre el mismo historial. El cuentagotas, la cámara y las preferencias de vista no generan pasos.

### Colores y comparación de superficies

`Color` muestra los colores permitidos por el perfil cromático vinculado a la fuente en una cuadrícula lateral derecha, con desplazamiento vertical y búsqueda por nombre o ColorID. Los tooltips identifican cada muestra y su RGB; pulsar una celda elige el color destino sin aplicarlo. La rueda sobre el panel desplaza la paleta, no la cámara. Sin un perfil vinculado se utiliza la paleta global completa. Un perfil ausente o incompatible bloquea la pintura de color; no se sustituye silenciosamente por otro. Los colores existentes fuera del conjunto permitido se conservan al editar superficies. El guardado comprueba que los ColorIDs modificados sigan permitidos. La selección de un color nuevo para el archivo obtiene su RGBA de la paleta global y conserva los slots supervivientes.

`Browse Surfaces` despliega cinco miniaturas alrededor del candidato activo; las flechas recorren las superficies Opaque y Glass. Las esferas comparten un color neutro y una iluminación HDRP fija. El tooltip muestra nombre, ID y valores PBR; las miniaturas no simulan bloom ni la iluminación final de la escena.

Para comparar directamente sobre el modelo:

1. Seleccionar voxels y elegir el modo `Surface` o `Color`.
2. Pulsar `Try on Selection`. La vista temporal utiliza iluminación Lit y conserva el color o superficie que no se está editando. La selección queda fija; la cámara continúa disponible.
3. Elegir miniaturas de superficies o muestras de la cuadrícula de colores. `←` y `→` recorren los candidatos sin modificadores; en Color, `↑` y `↓` avanzan por filas del conjunto filtrado, sin retorno circular al llegar a sus extremos. Las teclas actúan en la ventana mientras no se esté editando texto ni arrastrando un control.
4. Pulsar `Confirm` o `Enter` para registrar una única asignación en el borrador; después utilizar el guardado habitual. `Cancel` o `Esc` descarta la comparación sin modificar historial, selección ni archivos.

El preview prepara una malla temporal que separa los límites de selección y reutiliza esa geometría entre candidatos de la misma clase de render. Cambiar entre opaco y vidrio recalcula las caras visibles y sus submeshes. Conserva los límites de selección y meshing; una selección muy fragmentada puede superar el presupuesto y requerir reducir su tamaño. `Original` compara con el borrador anterior al preview; `Confirm` aplica el candidato elegido. El tinte se suprime durante la comparación y `Outline` permite mostrar el contorno, oculto por defecto para no tapar el acabado. Cerrar la ventana, recargar scripts o entrar en Play Mode descarta el preview; los cambios previamente confirmados siguen sujetos al guardado y recuperación del borrador. Undo/Redo durante el preview lo cancela primero, sin recorrer el historial. La vista de diagnóstico anterior se conserva al salir. El intercambio externo de RGB duplicados continúa pendiente de certificación.

### Preview de reducción al siguiente LOD

`Preview Next LOD`, en la barra `View`, requiere una familia de producción vinculada y un siguiente nivel definido en su perfil con tamaño voxel mayor. Calcula bajo demanda el resultado de `Reduce` desde el borrador completo, incluidos cambios sin guardar. Utiliza el mismo reductor, resolución, padding, alineación y política de cavidades que la derivación de producción.

1. Alternar `Draft` e `If Regenerated: Reduce` para comparar manteniendo la cámara. Las vistas Lit, Base Color, SurfaceID y Emission están disponibles; SurfaceID conserva su leyenda lateral.
2. En `Draft`, activar `Changed Finish` para resaltar en rojo las caras expuestas cuyo par ColorID/SurfaceID pierde frente al elegido en la celda gruesa compatible. Las caras interiores y las orientaciones incompatibles no se consideran pérdidas de acabado. La ausencia de rojo no garantiza conservar silueta, huecos o relieve.
3. Pulsar `Return to Paint` o `Esc` para continuar editando. Undo/Redo durante el preview primero vuelve al pintor sin recorrer el historial. La selección y el aislamiento anterior se conservan; el cálculo siempre utiliza la fuente completa.

El LOD guardado no se carga como resultado ni se modifica: puede contener retoques distintos del resultado hipotético. El preview no guarda archivos, crea assets ni regenera niveles. Se reutiliza mientras el borrador y los parámetros sigan vigentes; los cambios del proyecto pueden marcarlo como desactualizado. `Refresh` verifica fuentes y recalcula; un conflicto externo exige volver y recargar `Source`. La generación y el diagnóstico son cancelables y respetan los límites de volumen y meshing. Cerrar la ventana, recargar scripts o entrar en Play Mode libera los recursos temporales. La rejilla gruesa local y la inspección de votos por bloque quedan fuera de esta versión.

### Vidrio voxel básico

La superficie recomendada `044 · Glass` utiliza `Render Class = Glass (Transparent)`, `Opacity = 0,25` y `Smoothness = 0,9`. El tinte procede del ColorID. En paletas existentes, utilizar `Append Recommended` y reconstruir la LUT para incorporar el preset sin sustituir otras superficies.

- **Por objeto:** añadir `Conversion Rule` al GameObject de las ventanas, elegir `Voxelize` y SurfaceID `44` y convertir el modelo completo con el `Conversion Profile` vinculado a esa paleta.
- **Por material:** asignar el material del cristal en `Conversion Profile > Material Rules`, con `Voxelize` y SurfaceID `44`. Permite separar cristal y carrocería aunque compartan GameObject.
- **Sobre voxels existentes:** elegir Glass en el Painter, previsualizar o aplicar a la selección y utilizar `Save & Rebuild`. La selección alcanza la primera celda ocupada, no atraviesa vidrio. Pintar una superficie transparente no elimina relleno interior ni crea una cabina hueca.

`Keep Original` conserva su geometría y material originales y no activa el shader voxel. La identificación de vidrio es explícita: no se infiere de la transparencia del material fuente. Una regla de GameObject tiene prioridad sobre las reglas de materiales; asignar Glass a la raíz con aplicación a descendientes puede convertir todo el vehículo en vidrio.

Glass utiliza la geometría fuente sin descartarla mediante `Alpha Cutoff`; la opacidad procede de la paleta. No reproduce recortes o variaciones de alpha de una textura. Para conservar esos efectos, utilizar `Keep Original`. Las superficies no transparentes mantienen el recorte por alpha.

Si una misma celda intersecta geometría opaca y vidrio, la conversión conserva la muestra opaca válida para evitar conexiones artificiales a través de marcos o paredes. Una muestra descartada por `Alpha Cutoff` no bloquea el vidrio. Entre muestras de la misma clase se mantiene la más cercana al centro de la celda. Esta política puede engrosar bordes o eliminar vidrio subvoxel; aumentar la resolución o utilizar `Keep Original` cuando sea necesario conservar ese detalle.

Durante la conversión, `Fill Interior` permite atravesar las superficies de clase Glass al identificar los espacios conectados con el exterior. Las ventanas conservan sus voxels y los huecos visibles a través de ellas permanecen vacíos; las piezas opacas cerradas mantienen su relleno. La decisión depende de la clase de superficie, no del valor de opacidad. No abre cabinas selladas completamente por voxels opacos ni recupera huecos que la resolución haya eliminado.

Para eliminar un relleno incorrecto ya guardado, reconvertir desde el modelo fuente con las reglas de vidrio correspondientes. `Rebuild` reconstruye el volumen existente y no elimina ese relleno; pintar Glass sobre una fuente rellena tampoco lo elimina.

`Opacity` y smoothness se editan en la paleta y se transportan mediante su LUT. Cambiar `Render Class` requiere reconstruir las mallas afectadas; los descendientes pintados siguen requiriendo regeneración explícita para heredar cambios de IDs. El vidrio añade un segundo material compartido por pareja de paletas sólo donde existe geometría transparente. No incluye refracción, sombras transparentes, absorción por espesor ni ordenación por triángulo. Capas transparentes solapadas, interiores y detalles finos reducidos requieren revisión artística.

### Vistas de diagnóstico y aislamiento

El selector `View` cambia únicamente la previsualización. No modifica los IDs, las LUT, los materiales compartidos, los archivos fuente ni el historial de edición.

| Vista | Uso |
|---|---|
| `Lit` | Referencia PBR con iluminación y emisión normalizada de autoría. |
| `Base Color` | Color de la paleta global sin iluminación, reflejos ni emisión. Desactivar `Tint` para comparaciones de color sin el tinte de selección. |
| `SurfaceID` | Color de diagnóstico estable por ID, independiente de ColorID y de las propiedades PBR. SurfaceID 0 utiliza gris. La leyenda lateral relaciona muestras, IDs y nombres de las superficies utilizadas en el modelo o grupo aislado, incluidas celdas ocultas. Los tooltips muestran propiedades PBR. La muestra bajo el puntero utiliza la misma correspondencia. |
| `Emission` | Resalta las superficies cuya emisión es distinta de cero en la LUT y atenúa el resto en gris. Aclara ligeramente los emisivos para distinguir incluso colores oscuros. No representa intensidad física, exposición ni bloom y no clasifica LED o neón por luminosidad. |

`Isolate Selection` fija los voxels seleccionados como grupo visible y los encuadra. Permite limpiar o modificar la selección dentro de ese grupo sin cambiar el aislamiento. `Brush`, `Rectangle`, `Match` y `Pick` consultan el mismo volumen visible; incluso `All Matching` con `Visible Only` desactivado permanece dentro del grupo. Las caras descubiertas por el recorte son inspeccionables, sin eliminar geometría de la fuente.

`Show All` restaura el volumen completo; `Frame All` encuadra el volumen visible. Si Undo/Redo recupera una selección fuera del grupo aislado, el aislamiento termina para mostrarla y evitar ediciones sobre selecciones ocultas. Guardar, cambiar o recargar la fuente, recargar scripts o entrar en Play Mode también termina el aislamiento. El modo `View` se conserva como preferencia de la ventana.

El aislamiento utiliza una rejilla temporal con los mismos índices que la fuente y una malla adicional. Respeta los límites de selección y meshing; un grupo disperso que exceda el presupuesto de caras se rechaza sin sustituir la vista anterior. Guardar o reconstruir desde una vista aislada procesa la fuente completa, no un recorte del modelo.

### Guardado y límites

`Save & Rebuild` requiere vincular un prefab de producción mediante `Edit`/`Edit Surfaces` o arrastrándolo a `Source` y eligiendo su LOD. Cargar un `.vox` directamente deja la sesión sin prefab vinculado y deshabilita ese botón; `Source Actions > Save Source Only` permite guardar la fuente sin reconstruir. La ausencia de cambios pendientes no bloquea la reconstrucción de una fuente vinculada.

Cerrar el menú `Edit LOD` sin elegir conserva la sesión actual. Elegir otra fuente con cambios pendientes solicita guardar, descartar o cancelar. Vincular un prefab a la misma fuente conserva el borrador, la selección y el historial. Los niveles con fuentes ausentes permanecen deshabilitados; los prefabs sin vínculo de producción o con vínculos copiados se rechazan sin sustituir la sesión. Los assets se resuelven por GUID, no por nombres de archivo.

Los cambios externos del `.vox`, sidecar o paletas bloquean el guardado para evitar sobrescrituras. `Source Actions > Reload Source` requiere resolver el borrador pendiente; `Discard Changes...` lo descarta explícitamente. No hay fusión automática con cambios de MagicaVoxel. La advertencia sobre slots con RGB idéntico y superficies distintas se consulta en `?` o `Source Actions > External Editing Notice`; ese intercambio externo permanece sin certificar.

El guardado conserva copias de recuperación bajo `Library/VoxelBridgeSurfaceEdits`. Una interrupción bloquea las lecturas semánticas de producción hasta ejecutar `Recover Interrupted Save`. La recuperación restaura ambos archivos originales y rechaza sobrescribir modificaciones externas posteriores. No borrar `Library` ni mover las fuentes mientras exista una recuperación pendiente. Un fallo de importación posterior al guardado informa que la fuente está guardada y requiere reimportación.

Límites: 131.072 celdas por selección, 1.048.576 celdas modificadas pendientes, historial compartido de hasta 64 operaciones y 1.048.576 cambios de celda entre selecciones y asignaciones. El historial almacena las diferencias de selección, no copias completas del volumen. El resaltado muestra el perímetro de las regiones coplanares seleccionadas, sin cuadrícula interior. `Tint`, activado por defecto, añade un tinte suave del 4 %; desactivarlo deja únicamente el contorno. Esta preferencia de vista no modifica la selección ni sus asignaciones. Conserva los bordes de huecos, grupos separados y cambios de plano; no dibuja marcas a través de superficies opacas. La celda bajo el cursor utiliza cian. Las mallas temporales del resaltado se dividen en bloques y se reutilizan mientras no cambie la selección o el modo de tinte; no crean materiales por voxel ni modifican los assets. La asignación sigue afectando al voxel completo. Se mantienen los límites de lectura y meshing de producción. Superar 255 pares locales bloquea el guardado sin aproximar colores o superficies.

Un gesto admite hasta 1.048.576 muestras de pantalla únicas, 4.194.304 muestras contando repeticiones y 4.096 segmentos de arrastre. Superar un límite cancela el gesto completo, conserva la selección anterior e indica que se requiere un área o trazo menor; no confirma silenciosamente una selección parcial.

La vista `Lit` limita el multiplicador de emisión de su material temporal al rango `0..1`; sin material vinculado utiliza `1`. Conserva la emisión relativa de cada SurfaceID y la emisión apagada si el multiplicador original es cero. Esto permite identificar el color sin saturarlo por intensidades HDR altas; no simula exposición, bloom ni luminosidad física final. El diagnóstico `Emission` muestra presencia emisiva en la LUT independientemente de ese multiplicador. Guardar o reconstruir no transfiere estos ajustes de vista al material de producción. Comprobar el resultado luminoso definitivo en la escena.

La vista `Lit` utiliza HDRP Lit, no colores planos. Una superficie con `Metallic = 0` conserva respuesta especular; `Smoothness` influye en su apariencia bajo las luces de referencia. Comparar el brillo con otro editor, como MagicaVoxel, requiere considerar sus diferencias de sombreado e iluminación. Utilizar `Base Color` para comprobar los colores sin esos efectos y `SurfaceID` para revisar las asignaciones.

El perfil recomendado `Default` (SurfaceID 0) utiliza Metallic 0, Smoothness 0, Emission 0 y AO 1 como referencia mate. Cambiar una definición global requiere regenerar su LUT y recargar las sesiones de pintura; afecta a todos los voxels que usan ese ID, sin reconstruir su geometría. Guardar o resolver los borradores antes de modificar las paletas compartidas.

El guardado y la reconstrucción son etapas separadas: un fallo de reconstrucción conserva la fuente guardada y la malla anterior. Reintentar la reconstrucción después de resolver el error; Undo no revierte los archivos guardados.

La preasignación opcional de SurfaceID durante la conversión se describe en [SURFACE_MAPPING.md](SURFACE_MAPPING.md). La paleta recomendada contiene 45 superficies y permite inspeccionarlas con `Preview Selected Surface`; consultar [SURFACE_CATALOG.md](SURFACE_CATALOG.md) para el catálogo y sus límites. `Surface Painter` permite editar ColorID y comparar acabados sobre la selección sin modificar las definiciones globales.

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

1. Abrir `Physical Models and LODs` y seleccionar la pestaña `Individual` o `Batch`.
2. Seleccionar un FBX, OBJ, prefab, GameObject o Mesh como `Source Model`.
3. Crear o seleccionar un `Voxel Style Profile`.
4. Configurar `Base Voxel Size` en unidades de Unity. El valor predeterminado `0.032` equivale a 3,2 cm cuando una unidad representa un metro.
5. Configurar multiplicadores LOD crecientes en potencias de dos, comenzando en `1`, por ejemplo `1, 2, 4`. Cada LOD requiere una transición explícita y válida.
6. Elegir `Generate Levels`: `LOD0 Only` para preparar la autoría semántica, o `All Profile Levels` para voxelizar cada nivel desde la malla original. La selección se aplica a la conversión individual y por lotes, independientemente de `Maximum Allowed Base`.
7. Asignar `Conversion Profile` para utilizar colores y superficies globales. Regenerar las LUT desactualizadas desde `Global Palettes`.
8. Elegir el origen del color, la carpeta de familias y generar el resultado. Con perfil, la salida es el prefab semántico de producción; `Place Result in Scene` coloca ese mismo prefab. Sin perfil, la salida es una previsualización RGB de Voxel Importer.

Cada LOD automático se voxeliza desde la malla fuente. Con multiplicadores `1, 2, 4`, las celdas utilizan `base`, `base × 2` y `base × 4`. Todas las rejillas comparten la alineación física.

Para volver a calcular la ocupación desde los triángulos originales, realizar una nueva conversión. `Rebuild` utiliza los voxels guardados: no recupera geometría omitida durante la conversión ni vuelve a muestrear materiales. Conservar una copia de los retoques antes de reemplazar una familia.

### Escala y variantes de origen

La ventana `Voxel LODs` separa las funciones en cinco pestañas:

| Pestaña | Alcance |
|---|---|
| Individual | Una fuente; exclusión de inactivos, límites de memoria, impostor opcional y colocación específicos. |
| Batch | Hijos del padre; reutilización, overrides, inactivos, límites, recuperación y colocación específicos. |
| Editar LODs | Derivar un nivel desde un VOX, reconstruir el prefab y consultar los niveles de una familia. |
| Impostores | Configurar el impostor de una familia o procesar pendientes de la carpeta compartida. |
| Resultados | Acceder a los últimos assets y al estado de la operación. |

`Ajustes compartidos · Individual + Batch` presenta el mismo estado en ambas pestañas: Voxel Profile, Conversion Profile, color, Alpha Cutoff, Normalize Scale, niveles y carpeta de salida. Cambiar de pestaña no copia ni restablece esos valores. Los límites y la colocación de cada modo son independientes. El perfil voxel también se utiliza en la derivación manual; el perfil de impostores y su calidad son compartidos por los controles que lo utilizan. Los accesos contextuales abren la pestaña correspondiente.

`Normalize Scale`, activado por defecto en la ventana, incorpora la escala mundial de la fuente a la geometría antes del análisis de memoria y la voxelización. Los prefabs y las instancias colocadas utilizan escala `(1,1,1)`, conservando posición, orientación y dimensiones mundiales dentro de la precisión de voxelización. El tamaño de celda del perfil representa metros efectivos, no una medida multiplicada después por el Transform.

La colocación conserva un padre sin escala cuando existe; si la jerarquía aplica escala, utiliza el primer ancestro compatible o la raíz de la escena. No aplica snapping de posición en este modo. Se admiten escalas uniformes o no uniformes y reflejos sin cizallamiento. La reflexión se incorpora a la geometría y se compensa la rotación de colocación; no se elimina el espejo de la pieza. Las escalas nulas, no finitas y jerarquías con cizallamiento se rechazan. El resultado no mantiene la dependencia de una escala animada del padre.

En lotes, una instancia cuya escala mundial difiera de la del prefab fuente utiliza una familia independiente. `Use Source Prefab` sigue descartando los overrides visuales, pero la escala incorporada corresponde a la instancia. Las piezas `Keep Original` reciben la misma transformación que los voxels. El manifiesto registra `normalizedScale` y `bakedRootScale`; las familias antiguas conservan su contrato anterior y requieren una conversión nueva para normalizarse. `Rebuild` no incorpora la escala de sus instancias.

`Ignore Inactive Objects` conserva valores independientes en `Individual` y `Batch`. Para personajes modulares, seleccionar una jerarquía que contenga sólo la variante deseada y los huesos necesarios. Las mallas animadas se capturan como geometría estática en su pose actual; la conversión no exporta rig ni animaciones.

### Ejes de origen

Configurar `Source Axes` en el `Conversion Profile` antes de convertir:

| Opción | Uso |
|---|---|
| `Preserve Local Axes` | Predeterminada. Conservar las coordenadas locales de la raíz de conversión. |
| `Z Up → Y Up` | Normalizar modelos cuyo eje vertical local es Z mediante una rotación de −90° en X: `(x, y, z) → (x, z, −y)`. |

La normalización se aplica a la geometría fuente antes de calcular bounds y voxelizar, incluidas las piezas `Keep Original`. El `.vox`, el prefab de producción y `Surface Painter` utilizan la misma orientación. El origen del pivote permanece fijo; no se infiere la dirección frontal del vehículo. No utilizar `Z Up → Y Up` si una raíz contenedora ya presenta la geometría en Y vertical.

El sidecar y el manifiesto conservan la configuración utilizada. Duplicar, reducir o reconstruir LODs no vuelve a aplicar la rotación. Cambiar `Source Axes` requiere una conversión nueva desde la malla original; `Rebuild` no normaliza una familia existente. Los archivos anteriores, sin este campo, conservan sus ejes locales. Para lotes con distintas convenciones de ejes, utilizar conversiones separadas con el perfil correspondiente.

### Reglas de conversión

Crear `Assets > Create > Voxel Bridge > Conversion Profile` y asignarlo en `Conversion Profile`, dentro de `Physical Models and LODs`. El perfil requiere un `Color Mapping Profile` y una paleta global de superficies guardados como assets. Las reglas se comparten entre la conversión individual y por lotes.

| Acción | Resultado |
|---|---|
| `Voxelize` | Convertir el submesh y asignar sus ColorID y SurfaceID |
| `Ignore` | Excluir la geometría de la rejilla y del resultado |
| `Keep Original` | Excluir la geometría de la rejilla y conservar una copia estática con sus materiales originales en el prefab |

`Material Rules` identifica los materiales por referencia, no por nombre. Para una excepción por objeto, utilizar `Add Component > Voxel Bridge > Conversion Rule`. La regla habilitada más cercana al renderer tiene prioridad; `Apply To Children` permite heredarlas dentro de la raíz de conversión. `Surface ID = -1` conserva la asignación del material o permite la detección automática.

Las reglas de componentes aplicadas a instancias de prefabs se consideran overrides. Para utilizarlas sin modificar el prefab fuente, seleccionar `Modified Instances > Convert Instance Separately` en lotes. La opción de utilizar el prefab original descarta esos overrides de forma intencional.

Los `.vox` generados con perfil contienen vinculaciones semánticas. La carpeta de la familia contiene sus fuentes, manifiesto, mallas de chunks y un único prefab LOD final, sin otro prefab de previsualización. El Inspector del prefab ofrece `VOX` para abrir MagicaVoxel, `Rebuild` y `⋯ > Semantic Bindings` para cada nivel. Sin perfil, la conversión conserva el flujo RGB y permite reglas de componentes `Ignore` y `Keep Original`, pero no SurfaceID explícitos.

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

`Place Result in Scene` instancia el prefab generado como hermano de la fuente. Conserva layer, tag y flags Static. Con `Preserve Local Axes` copia su transform; con `Z Up → Y Up` compensa la rotación y permuta las escalas Y/Z, conservando sus signos, para mantener la apariencia en escena sin duplicar la corrección. La posición utiliza el ajuste a rejilla del perfil. La colocación individual y por lotes consulta la configuración guardada en la familia, no el valor actual del perfil. `Disable Original Object` desactiva la fuente después de completar la conversión y, si corresponde, el horneado del impostor. La operación admite Undo. Una fuente seleccionada desde Project sólo genera assets.

`Snap Pivots on Placement` ajusta la posición mundial al múltiplo más cercano de la unidad base. El desplazamiento máximo es media celda por eje. No introduce un desplazamiento aleatorio.

### Lotes y reutilización

`Batch Generation` procesa los hijos directos del `Parent Object`; cada hijo puede contener una jerarquía de mallas.

- `Ignore Inactive Objects` excluye objetos desactivados y geometría desactivada dentro de cada fuente.
- `Reuse Source Prefab` comparte una conversión entre instancias equivalentes del mismo prefab. Las variantes se consideran fuentes distintas.
- `Modified Instances` permite utilizar el prefab original, convertir la jerarquía modificada como fuente independiente o ignorarla. Ninguna opción modifica el prefab fuente.
- Con `Normalize Scale`, la escala mundial forma parte de la conversión y puede separar familias reutilizadas. Sin esta opción, los transforms del root se recuperan durante la colocación y no provocan por sí solos otra voxelización.
- El plan visible distingue conversiones, reutilizaciones y omisiones antes de comenzar.

`Analyze Batch Memory` muestra las fuentes únicas y su estimación. Los fallos individuales se registran y el lote continúa con las fuentes restantes.

### Tiempos de conversión

La generación automática individual y por lotes escribe un resumen en Console y un informe `Logs/VoxelBridge/ConversionTiming-<fecha>-<id>.json`. Incluye estado final, tiempo total en milisegundos y tiempos acumulados por etapa: preparación, voxelización y muestreo de materiales, relleno interior, escritura de VOX/metadatos, importación, meshing, lectura/guardado de assets y limpieza. `Other` recoge coordinación y operaciones sin una etapa específica.

El informe batch corresponde al lote completo, incluidas sus conversiones fallidas y la limpieza; no duplica los tiempos por reutilizar una familia. Las etapas son exclusivas: una operación anidada pausa la contabilización de su etapa padre. Se conservan informes de cancelación y fallo cuando la ejecución permite finalizar el diagnóstico; un cierre del proceso puede impedir escribirlos.

Las medidas son de tiempo transcurrido, no de CPU o GPU aislados. Incluyen esperas del Editor y callbacks de progreso dentro de la conversión; excluyen la escritura del propio informe, el análisis previo ya realizado, la colocación posterior en escena y el horneado opcional de impostores. Comparar lotes equivalentes y considerar el estado de las cachés antes de decidir una optimización. La instrumentación no activa paralelismo ni cambia la geometría. Los informes son archivos locales fuera de `Assets`, sin importación ni versionado.

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

El Inspector de una familia semántica muestra una fila por LOD con su tamaño físico y las acciones `Edit` (Surface Painter), `VOX` (MagicaVoxel) y `Rebuild`. El menú `⋯` de cada fila contiene `Select Source VOX`, `Semantic Bindings`, el informe de conversión y, para los descendientes, `Regenerate > Duplicate Previous…` y `Regenerate > Reduce Previous…`. La regeneración conserva su confirmación de pérdida de retoques.

`Family ▾` agrupa `Duplicate Editable Family`, `Rebuild All Meshes` y `Select Manifest`. `Create LOD N ▾` ofrece duplicar, reducir o generar los niveles restantes cuando el perfil lo permite. `?` despliega la ayuda; el encabezado permite plegar la lista. Los avisos de origen cambiado y malla pendiente se agrupan por nivel y permanecen visibles con el panel plegado. El estado de presentación se conserva por familia durante la sesión del Editor, sin modificar los prefabs.

`Edit Family LODs` abre la lista de niveles desde el prefab, un `.vox`, el manifiesto o un GameObject de la familia. Cada nivel permite seleccionar su archivo, abrirlo en MagicaVoxel y utilizarlo como padre del siguiente LOD. El menú contextual `Edit This LOD in MagicaVoxel` abre directamente el nivel seleccionado en la jerarquía.

El modo manual ofrece dos operaciones:

- `Duplicate Previous (Free Editing)`: copiar el archivo para una simplificación artística libre.
- `Reduce to Target Resolution`: remuestrear el volumen editado a la unidad del nuevo nivel.

En un volumen semántico, la reducción prioriza el par `ColorID + SurfaceID` con mayor área de caras expuestas orientadas como los límites del voxel reducido. El relleno interior no compite con esas caras; si no hay contribuciones expuestas compatibles, se utiliza la mayoría por volumen. Los empates se resuelven por el par de IDs menor. La política de cavidades del padre se aplica al volumen completo, sin tratar las fronteras entre chunks como superficies exteriores.

Cada voxel reducido conserva un único par existente del bloque fuente. `Default` sigue siendo una superficie válida y los emisivos no reciben prioridad incondicional: una región pequeña o dos acabados que comparten una celda gruesa pueden requerir retoques. La ocupación, la alineación y el tamaño de la rejilla no dependen de esta selección de acabados; conservar más límites semánticos puede aumentar el número de triángulos frente a la mayoría por volumen. La ruta RGB conserva su promedio de color.

La regla se aplica al crear niveles por reducción o al confirmar `⋯ > Regenerate > Reduce Previous…`. `Rebuild` sólo reconstruye las mallas del VOX guardado, sin transferir nuevamente acabados desde el padre. Los LODs existentes no se regeneran automáticamente al actualizar el reductor; conservar una copia antes de reemplazar retoques manuales.

`Rebuild Prefab from Manifest` reconstruye la familia y resincroniza sus imports. Conserva la ruta registrada y el GUID del prefab, incluso si se ha reubicado. Un perfil o manifiesto requerido que falta produce un error; la herramienta no inventa transiciones ni sustituye la familia silenciosamente.

Las conversiones con `Conversion Profile` generan la familia semántica de producción directamente. Para un `.vox` externo o convertido sin perfil, vincular los IDs de LOD0 y utilizar `Semantic Production LOD Family` en `VOX to Unity`. Su prefab permite editar, reconstruir, duplicar y reducir niveles individualmente. Esta ruta utiliza el mesher propio y conserva chunks para culling. Consultar el [flujo de producción](MATERIALS.md#familias-semánticas-de-producción).

## Transiciones y sombras

`Transition Mode` permite utilizar porcentajes fijos de pantalla o adaptación por tamaño. El modo adaptativo aplica la relación entre el tamaño del modelo y `Reference Model Size`, modulada por `Size Adaptation Strength` y sus factores mínimo y máximo.

Para modelos mayores que `Large Model Threshold`, el refuerzo adicional adelanta los niveles de menor resolución. Un porcentaje mayor cambia al siguiente LOD a menos distancia. Con un impostor final, los rangos intermedios se amplían de forma controlada; en estructuras grandes, una curva voxel `75 / 45 / 25` puede producir fronteras `75 / 55 / 35` antes del impostor.

`Small / Medium Model Detail` permite conservar los niveles detallados durante más distancia sin cambiar la curva de los edificios grandes:

- `Lod Detail Screen Heights`: curva alternativa a `Reference Model Size`, con un valor por LOD, positivo, decreciente y no mayor que el correspondiente valor base. Una lista vacía desactiva este ajuste; `Fixed Screen Height` lo ignora.
- `Detail Full Size`: tamaño hasta el que se utiliza la curva alternativa completa.
- `Detail Blend End Size`: tamaño a partir del que se recupera exactamente la curva base. Entre ambos tamaños se mezclan suavemente las curvas antes de aplicar el factor adaptativo existente. Esta mezcla es por tamaño del asset, no un fade durante el cambio de LOD.

Calibración de referencia para cinco niveles: curva base `0.30 / 0.18 / 0.10 / 0.04 / 0.02`, curva de detalle `0.175 / 0.0875 / 0.04375 / 0.024 / 0.0145`, referencia `4 m`, fuerza `0.5`, límites generales `0.35–2`, refuerzo grande desde `6 m` con fuerza `0.25` y máximo `2.5`, detalle completo hasta `7 m` y retorno a la curva base en `20 m`.

| Object Size | Salida de LOD0 / LOD1 / LOD2 / LOD3 / LOD4, en % de pantalla |
|---|---|
| 2 m | 12.37 / 6.19 / 3.09 / 1.70 / 1.03 |
| 7 m | 24.06 / 12.03 / 6.01 / 3.30 / 1.99 |
| 20 m o más | 75 / 45 / 25 / 10 / 5, con esta configuración |

La última frontera descarta el objeto si no existe un impostor posterior. Los tamaños se obtienen del `LODGroup` generado, no del nombre o categoría del modelo. Con `Normalize Scale`, las dimensiones de conversión incorporan la escala de la fuente. Cambiar la escala de una instancia después no recalibra el perfil.

El perfil se aplica en conversiones nuevas y reconstrucciones que configuran el LODGroup; editarlo no actualiza las familias existentes ni sus overrides de escena. Al cambiar el número de niveles, completar ambas curvas. Los umbrales no modifican geometría, resolución voxel ni materiales y requieren revisión visual con la cámara de juego.

`Family > Apply Profile LOD Transitions…` aplica el perfil vinculado a una familia de producción sin reconvertir ni reconstruir sus mallas. Para varias familias, seleccionar sus prefabs o un padre de escena y ejecutar `Voxel Bridge > Production > Apply Profile LOD Transitions` desde el menú contextual. Cada familia se procesa una sola vez, incluidos descendientes inactivos. No requiere leer sus VOX.

La acción actualiza los porcentajes del prefab compartido y su manifiesto, conservando GUIDs, renderers, materiales, sombras, `Object Size`, punto de referencia y configuración de fade. Las instancias sin overrides de LOD heredan el cambio; los overrides manuales de escena se conservan. Las familias con impostor, perfiles incompatibles o el prefab abierto en Prefab Mode se rechazan sin regeneración automática.

La confirmación advierte que esta operación de archivos no admite Undo. Antes de guardar, conserva prefab, manifiesto y sus `.meta` en `Logs/VoxelBridge/LODTransitionBackups/<id>`; Console indica la ubicación. Un fallo de escritura intenta restaurar ambos archivos. Para recuperar manualmente un estado, cerrar Prefab Mode y restaurar los archivos de esa copia en sus rutas originales, conservando sus `.meta`. En operaciones sobre varias familias, cancelar detiene las pendientes y conserva las actualizaciones completadas; no hay una transacción global del lote.

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

## Duplicación de familias editables

`Duplicate Editable Family` está disponible en el Inspector del prefab de producción y en el menú contextual `Voxel Bridge > Production`, tanto en Project como en la jerarquía. Crea una carpeta hermana con sufijo `_Copy_VoxelLOD` y nombre único, y selecciona el nuevo prefab sin colocarlo ni reemplazar instancias en escena.

La copia incluye los VOX y bindings de todos los LODs guardados, el manifiesto, las mallas, el prefab y la geometría `Keep Original`. Utiliza GUIDs y vínculos propios: pintar o reconstruir la copia no modifica las fuentes ni las mallas del original. Conserva los ajustes guardados del prefab y los avisos de mallas o descendientes pendientes; no regenera geometría durante la duplicación.

Las paletas, LUT, perfiles y materiales permanecen compartidos. Cambiar una asignación de ColorID/SurfaceID en la copia es independiente; modificar una definición global de color, superficie o material afecta a todos sus consumidores. Los borradores del Painter y los overrides de instancias de escena no se incluyen: guardar o aplicar esos cambios antes de duplicar si deben formar parte de la copia.

La operación requiere Edit Mode, una familia semántica válida y un prefab regular sin prefabs anidados ni impostores horneados. Los prefabs de producción independientes requieren primero `Create LOD Family`. No admite Undo de archivos; la cancelación o un error elimina únicamente la carpeta parcial creada por la operación. Una interrupción del Editor puede dejar una copia incompleta, sin modificar la familia original.

Utilizar una copia para las pruebas de intercambio con MagicaVoxel. La duplicación conserva los pares semánticos, pero no certifica que una edición externa mantenga slots con RGB idéntico y SurfaceID diferentes.

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
