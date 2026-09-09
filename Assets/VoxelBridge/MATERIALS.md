# Paletas y materiales voxel

## Alcance

El sistema de materiales voxel separa el color visible de las propiedades físicas de la superficie. Un modelo puede reutilizar el mismo color con perfiles PBR diferentes sin crear un Material de Unity por combinación.

El sistema proporciona paletas globales, IDs estables, generación de LUT, transporte de `ColorID + SurfaceID` en volúmenes y archivos `.vox`, exportación independiente de LOD0 y familias de producción opacas con chunks y material HDRP compartido. La integración con impostores semánticos y DOTS se describe en [ROADMAP.md](ROADMAP.md).

## Assets canónicos

La ventana `Tools > Voxel Bridge > Global Palettes` crea y administra:

- `Assets/VoxelBridge/Palettes/VoxelColorPalette.asset`: colores globales.
- `Assets/VoxelBridge/Palettes/VoxelSurfacePalette.asset`: perfiles físicos globales.
- `Assets/VoxelBridge/Palettes/Generated/VoxelColorPalette_LUT.asset`: LUT de color.
- `Assets/VoxelBridge/Palettes/Generated/VoxelSurfacePalette_LUT.asset`: LUT de superficie.

Los ScriptableObjects son la fuente de verdad. Las texturas de `Generated` no deben editarse directamente.

## Identidad y capacidad

Cada paleta admite IDs entre `0` y `255`. El ID `0` es obligatorio y representa el valor predeterminado seguro.

La identidad depende del número de ID, no del orden ni del nombre de la entrada. Reordenar o renombrar una entrada no modifica los meshes que la referencien. Al eliminar una entrada, su ID queda retirado y no se reutiliza automáticamente.

La combinación de 256 colores y 256 superficies admite 65.536 apariencias sin crear 65.536 Materials de Unity. Un Material compartido puede representar todas las combinaciones compatibles con el mismo comportamiento de render.

## Paleta de color

Cada `ColorID` contiene un nombre y un color RGBA. Su LUT utiliza:

- tamaño fijo de `256 x 1`;
- formato `RGBA32`;
- espacio sRGB;
- filtrado `Point`;
- wrap `Clamp`;
- mipmaps desactivados;
- compresión desactivada al almacenarse como `Texture2D` nativa.

Los slots no definidos contienen el valor del `ColorID 0`. Esta regla sólo completa la textura; una consulta de un ID desconocido sigue considerándose un error y no se resuelve silenciosamente como el valor predeterminado.

## Paleta de superficies

Cada `SurfaceID` define un perfil PBR reutilizable. Por ejemplo, `Aluminum` se configura una vez y cualquier voxel que utilice su ID recibe esas propiedades. No es necesario introducir valores PBR distintos en cada voxel.

La LUT lineal de superficies codifica:

| Canal | Valor | Rango |
| --- | --- | --- |
| R | Metallic | 0..1 |
| G | Smoothness | 0..1 |
| B | Emisión relativa | 0..1 |
| A | Multiplicador de oclusión | 0..1 |

El multiplicador de oclusión controla cuánto participa la oclusión calculada o proporcionada por el pipeline. No representa una sombra fija horneada en el tipo de material.

`Render Class` clasifica la superficie según su comportamiento de render: opaca, follaje, transparente o especial. La clase no forma parte de la LUT PBR; en una fase posterior determinará el material o submesh compartido correspondiente.

El catálogo recomendado contiene 44 perfiles opacos, con IDs `0–43`. `Append Recommended` incorpora presets ausentes sin sobrescribir entradas ni reutilizar IDs retirados. `Preview Selected Surface` abre un visor temporal de esfera/cubo con los valores actuales, sin requerir una LUT reconstruida. Los valores son puntos de partida estilísticos: calibrarlos con el shader HDRP definitivo antes de producir materiales e impostores finales. Consultar [SURFACE_CATALOG.md](SURFACE_CATALOG.md).

## Flujo de edición

1. Abrir `Tools > Voxel Bridge > Global Palettes`.
2. Seleccionar `Create or Load Canonical Palettes` cuando los assets todavía no existan.
3. Seleccionar la paleta que se desea modificar.
4. Añadir, reordenar, renombrar o ajustar entradas desde su Inspector.
5. Corregir cualquier ID duplicado, fuera de rango, nombre duplicado o valor PBR inválido indicado por la validación.
6. Seleccionar `Rebuild Color LUT` o `Rebuild Surface LUT` y comprobar que la paleta y su LUT sean válidas y estén actualizadas.
7. Versionar juntos la paleta y su LUT generada.

El número de ID es de sólo lectura en el Inspector para evitar cambios accidentales. Las migraciones intencionales de IDs requerirán una herramienta dedicada que también remapee los assets dependientes.

## Superficies durante la conversión

`VoxelConversionProfile` combina un perfil de mapeo de colores, la paleta global de superficies y reglas por referencia a materiales. En `Physical Models and LODs`, la rasterización resuelve cada muestra como `ColorID + SurfaceID` antes de construir la paleta local del `.vox`; un mismo RGB con superficies distintas no pierde su identidad durante la cuantización.

La prioridad de SurfaceID es:

1. ID explícito del componente `Conversion Rule` habilitado más cercano.
2. ID explícito de la regla del material.
3. `Emissive Surface ID` cuando se reconoce emisión sobre el umbral.
4. Candidato PBR suficientemente cercano y diferenciado, si `Assign Surfaces From Pbr` está habilitado.
5. `Default Surface ID` para las muestras restantes.

La preasignación opcional compara metallic y smoothness de HDRP/Lit Standard con los candidatos del `Surface Mapping Profile`; los casos ambiguos o no compatibles conservan el fallback. Cada LOD incluye un informe de la conversión. Consultar [SURFACE_MAPPING.md](SURFACE_MAPPING.md) para configuración, límites y revisión.

El perfil rechaza IDs desconocidos y superficies no opacas destinadas a voxelización. `Keep Original` conserva vidrio u otras piezas con sus materiales originales fuera del volumen semántico. Los materiales temporales de autoría pueden asignarse a caras en Blender; en Unity se vinculan por referencia desde `Material Rules`. No existe resolución automática mediante prefijos `SURF_`.

`Detect Emission` admite HDRP/Lit y Standard, con emisión uniforme o mapa en UV0. Respeta la activación del mapa en HDRP y la palabra clave de emisión en Standard. Las texturas se muestrean con su escala y desplazamiento, con una resolución de lectura máxima de 512 × 512. `Single Color` omite esta inferencia salvo cuando PBR está habilitado: en ese caso también separa las muestras emisivas antes de clasificar superficies. Los shaders no compatibles y las configuraciones HDRP con emisión dependiente del albedo o mapeo distinto de UV0 requieren asignación explícita y producen una advertencia.

`Emission Threshold` se aplica a la emisión lineal antes de normalizar su color HDR. La intensidad original sirve para reconocer una muestra emisiva, no para crear superficies diferentes por intensidad. El color resultante pasa por el perfil de colores globales; la intensidad final procede de la superficie y del material de producción. Las muestras emisivas utilizan el mismo ColorID para base y emisión: un albedo azul con emisión roja independiente requiere otra representación, fuera de este flujo.

La conversión no amplía automáticamente la paleta global. Una muestra de color fuera de `Maximum Automatic Distance` detiene la fuente con un error; las coincidencias de color sobre `Warning Distance` producen un aviso resumido por LOD. Estos límites cromáticos son independientes del fallback por semejanza PBR. Cada `.vox` admite hasta 255 pares locales; superar ese límite requiere reducir el conjunto de colores o superficies del modelo.

El sidecar conserva la tabla de slots y las referencias a las paletas y al perfil de color. Duplicar un LOD mantiene estos datos; reducirlo selecciona el par mayoritario mediante el desempate estable del reductor. Los descendientes reconstruidos desde el modelo original aplican otra vez las reglas de conversión, sin heredar ediciones posteriores realizadas en otro `.vox`.

## Vinculación semántica de `.vox`

Abrir `Tools > Voxel Bridge > Bind Semantic IDs` o utilizar `Assets > Voxel Bridge > Bind Semantic IDs` sobre un archivo `.vox`.

La ventana muestra únicamente los slots utilizados por `XYZI`. Al guardar, los slots asignados al mismo par `ColorID + SurfaceID` se consolidan en el slot de menor índice. Se remapean los índices de todos los chunks `XYZI` y se actualiza el sidecar sin modificar la ocupación, las posiciones ni la escala. Las superficies diferentes permanecen separadas, aunque compartan color. Los chunks `NOTE` y `MATL` se conservan sin reinterpretarlos; las propiedades de producción proceden de la paleta global de superficies.

El selector de ColorID muestra muestras de color, ID y nombre, y una comparación ampliada con el color original. Las flechas recorren las opciones, Enter confirma y Escape cierra sin modificar la asignación. `Edit > Undo / Redo` deshace o rehace cambios pendientes de color y superficie, incluido el mapeo automático como una sola operación. Guardar o cambiar de archivo, paleta o perfil reinicia este historial; los archivos guardados no se revierten mediante Undo.

Cada slot debe tener:

- un `ColorID` global;
- un `SurfaceID` global;
- el RGBA que representa ese `ColorID` en la paleta local del `.vox`.

Una coincidencia RGBA exacta permite preseleccionar el `ColorID`. Las entradas sin coincidencia permanecen sin asignar cuando no se utiliza un perfil de mapeo. La vinculación modifica la paleta RGBA del `.vox` y su sidecar, por lo que ambos archivos deben versionarse juntos.

### Perfiles de mapeo de color

`VoxelColorMappingProfile` define un subconjunto de la paleta global mediante rangos inclusivos de ColorID e IDs adicionales. Los rangos pueden cubrir bandas organizadas de la paleta, mientras que los IDs adicionales permiten incorporar acentos sin duplicar colores ni crear otra LUT.

La herramienta convierte los colores sRGB a OKLab y selecciona el ColorID permitido con menor distancia perceptual. Los empates se resuelven por el ID estable menor. Cada perfil define:

- el umbral a partir del cual una coincidencia requiere revisión;
- la distancia máxima permitida para una asignación automática;
- los ColorIDs disponibles para el modelo.

Crear los perfiles mediante `Create > Voxel Bridge > Color Mapping Profile`. `Install Recommended Color Library and Profiles`, en `Global Palettes`, instala o restaura la biblioteca recomendada y sus perfiles después de una confirmación explícita. Antes de reemplazar valores se comprueba que las rutas de destino no contengan assets de otro tipo.

Una coincidencia que supera la distancia máxima permanece sin asignar y requiere una elección manual o una ampliación explícita de la paleta. Aplicar un perfil sólo completa slots sin ColorID; no sobrescribe vinculaciones existentes. La paleta global nunca incorpora colores como efecto lateral de una importación.

Los perfiles son recursos de autoría y no generan materiales o LUT adicionales en runtime. Todos los perfiles resuelven IDs pertenecientes a `VoxelColorPalette.asset`. El sidecar conserva el GUID y la ruta del perfil utilizado para que la edición posterior recupere la misma receta, mientras la tabla `slot -> ColorID + SurfaceID` continúa siendo la autoridad del volumen.

### Biblioteca maestra recomendada

La biblioteca inicial utiliza 224 entradas y reserva los últimos 32 IDs para ampliaciones controladas. Sus bandas son:

| ColorID | Contenido |
|---|---|
| `0–31` | Predeterminado, neutros y grises cálidos o fríos |
| `32–63` | Rojos, naranjas y amarillos |
| `64–95` | Verdes, turquesas y cianes |
| `96–127` | Azules, índigos y violetas |
| `128–159` | Arcilla, marrones, ocres y oliva |
| `160–191` | Pasteles y tonos desaturados |
| `192–223` | Acentos intensos para señalización, pantallas y neón |
| `224–255` | Reserva sin entradas activas |

Los perfiles recomendados seleccionan estos rangos sin duplicar los colores:

- `All`: todos los IDs activos;
- `UrbanIndustrial`: neutros, tierras, pasteles controlados y acentos de seguridad;
- `Architecture`: biblioteca cromática amplia y acentos intensos limitados;
- `Vehicles`: colores de pintura y señalización sin la banda pastel;
- `Nature`: neutros, verdes, cianes, azules, tierras y pasteles naturales;
- `MutedNeon`: base neutra u orgánica con la banda completa de acentos intensos.

La banda intensa sólo define el color. La emisión continúa dependiendo del `SurfaceID`, por lo que un mismo acento puede utilizarse como pintura opaca, plástico o neón sin cambiar su `ColorID`.

El sidecar semántico utiliza `VoxelBridgeMetadata` versión 4 y un bloque semántico versión 1. Contiene:

- GUID y ruta de las paletas globales;
- huellas de contenido de ambas paletas;
- tabla `slot -> ColorID + SurfaceID + RGBA`;
- huella canónica de la tabla local.
- referencia opcional al perfil de mapeo de color utilizado durante la autoría.

El GUID identifica la paleta y permite moverla dentro del proyecto conservando su archivo `.meta`. Las rutas almacenadas son información de diagnóstico: si un GUID no se resuelve, la lectura falla aunque exista otro asset en la ruta antigua. Un cambio de huella global produce una advertencia porque ajustar un perfil PBR sin cambiar su ID es válido. Un cambio de la tabla local o del RGBA utilizado produce un error.

El flujo RGB utiliza sidecars v3 y la vinculación semántica produce sidecars v4. Ambos son formatos activos. Las versiones v1, v2 y las versiones desconocidas se rechazan y requieren regenerar la conversión. Una entrada RGB no recibe automáticamente `ColorID 0` ni una superficie predeterminada.

## Conservación durante LODs y MagicaVoxel

Un volumen semántico almacena el par en 16 bits: ocho para `ColorID` y ocho para `SurfaceID`. Esta representación sustituye al array RGB de la rejilla y evita mantener ambas copias en memoria.

La reducción manual selecciona el par mayoritario dentro de cada nueva celda. Los empates se resuelven por el valor estable menor. La duplicación manual copia el `.vox` y su sidecar sin reinterpretación. La conversión física con `Conversion Profile` asigna los IDs desde las muestras y reglas de materiales antes de exportar; sin ese perfil, la ruta RGB requiere vincularlos posteriormente. `All Profile Levels` convierte cada nivel desde la malla fuente y no hereda retoques del LOD0.

Voxel Bridge lee los índices directamente del binario `.vox`. El mesh y el atlas generados por Voxel Importer se utilizan como previsualización, no como fuente semántica, porque el importador puede compactar su paleta interna.

Los chunks `NOTE` y `MATL` se conservan, pero no determinan los IDs. Cualquier `IMAP` se rechaza hasta disponer de un fixture que verifique su dirección y comportamiento en la versión de MagicaVoxel utilizada. La lectura del volumen utiliza el scene graph para localizar los chunks por tamaño y posición, aunque MagicaVoxel cambie sus índices internos o inserte modelos vacíos. Los modelos adicionales vacíos se ignoran; un modelo adicional con voxels produce un error.

La edición admite pintar, añadir y eliminar voxels dentro de las cajas de los chunks exportados. Mover, rotar, reflejar, redimensionar o eliminar esas cajas requiere regenerar el volumen y sus metadatos. Las animaciones, las instancias de modelos internos y los chunks ocupados ocultos se rechazan. Estas validaciones también se aplican a la reducción manual de LODs y evitan reconstruir geometría desalineada o descartar contenido silenciosamente.

El mismo RGB puede ocupar dos slots locales cuando necesita superficies diferentes. Voxel Bridge conserva esos slots mientras controla la escritura. MagicaVoxel puede reordenar slots visualmente idénticos al volver a guardar; este caso no se considera certificado hasta completar una prueba controlada de round-trip.

## Autoría visual de superficies

La asignación en `Semantic Bindings` afecta a todos los voxels que utilizan un slot. `Surface Painter`, disponible desde `Edit Surfaces` en el prefab o desde `Tools > Voxel Bridge`, modifica ColorID o SurfaceID de las celdas seleccionadas conservando el otro atributo. La pintura de color respeta el perfil cromático vinculado. El [procedimiento de edición](README.md#edición-visual-de-superficies) describe selección, comparación temporal de acabados, guardado, recuperación y reconstrucción.

La herramienta reutiliza el `.vox`, el sidecar v4 y el mesher existentes. La superficie pertenece al voxel completo, no a cada cara. Guardar no regenera LODs descendientes ni modifica definiciones PBR globales. Los pares supervivientes conservan sus slots; los pares nuevos reutilizan slots libres. El intercambio externo de slots con RGB idéntico requiere certificación específica antes de considerarse seguro. La preasignación PBR y las ayudas de selección permanecen en [ROADMAP.md](ROADMAP.md#preasignación-de-superficies-por-semejanza-pbr).

La emisión de la vista utiliza una referencia acotada sólo en su material temporal. La ayuda `?` y el tooltip de las propiedades PBR describen esta limitación. No representa la exposición ni el bloom de la escena; la intensidad de producción permanece intacta. `Save & Rebuild` guarda la fuente y reconstruye el LOD vinculado; los descendientes requieren regeneración explícita.

El perfil recomendado `Default` utiliza Metallic 0, Smoothness 0, Emission 0 y AO 1. Es una superficie mate neutra, no un modo sin iluminación ni un sustituto de la asignación artística. Las otras superficies conservan sus propios valores. La vista de diagnóstico por SurfaceID está planificada para distinguir asignaciones con apariencias similares.

## Mesh semántico LOD0

`Tools > Voxel Bridge > VOX to Unity > Semantic LOD0 Production Mesh` genera una malla mediante greedy meshing. Las caras adyacentes se unen sólo si coinciden orientación, ColorID y SurfaceID. Las caras entre celdas ocupadas se eliminan, incluso en fronteras de modelos internos del `.vox`. El campo `hideInternalCavities` del sidecar controla la eliminación de caras orientadas hacia cavidades cerradas; las cavidades abiertas permanecen visibles.

El contrato del mesh es:

- `UV0.x`: `ColorID` crudo.
- `UV1`: reservado para lightmaps horneados, sin generar en esta exportación.
- `UV2`: reservado, sin datos.
- `UV3.x`: `SurfaceID` crudo.
- Vertex Color: máscaras estilísticas futuras.

Cada cara dispone de vértices propios, normales planas y tangentes. Los canales de IDs son Vector2 y cada triángulo contiene un único par. El origen de la rejilla y la unidad física se incorporan a los vértices; el prefab conserva transform identidad y el pivote local de la fuente.

### Exportación y material compartido

1. Seleccionar un `.vox` LOD0 con sidecar semántico v4.
2. Guardar sus asignaciones en `Bind Semantic IDs`.
3. Comprobar ambas LUT en `Global Palettes`; regenerarlas si están desactualizadas.
4. Abrir `VOX to Unity`, elegir `Production Folder` y ejecutar `Create or Rebuild Production LOD0`.
5. Colocar el prefab generado en una escena HDRP para comprobar color, superficies y escala.

La primera ejecución crea una carpeta `<LOD0>_Production` con `LOD0.asset` y un prefab vinculado al `.vox`. Las ejecuciones posteriores buscan el vínculo dentro de `Production Folder` y reconstruyen la misma malla. Si existen varios prefabs vinculados a esa fuente, seleccionar el prefab concreto y utilizar `Rebuild`. La operación no modifica la fuente, el manifiesto de la familia ni la colocación en escena.

### Edición desde el prefab

El Inspector del GameObject y los menús contextuales `Assets > Voxel Bridge > Production` y `GameObject > Voxel Bridge > Production` ofrecen:

- `Open in MagicaVoxel`: abrir el `.vox` vinculado.
- `Select Source VOX`: localizar la fuente para editar sus asignaciones semánticas.
- `Rebuild`: leer los cambios guardados y actualizar la malla compartida por todas las instancias.

Guardar en MagicaVoxel actualiza su import de previsualización. El prefab de producción requiere ejecutar `Rebuild` explícitamente. Pintar con un slot existente conserva su par semántico; utilizar un slot nuevo exige asignarle IDs en `Bind Semantic IDs` antes de reconstruir.

La reconstrucción conserva los GUIDs del mesh y prefab, los transforms, componentes y overrides de las instancias, y los ajustes compatibles del material. Admite Undo sobre la malla. Una fuente inválida o una cancelación durante el meshing deja intacto el resultado anterior. Los colliders y componentes añadidos manualmente no se regeneran.

El vínculo de autoría reside en `userData` del importador del prefab, dentro de su `.meta`; no añade componentes ni dependencias de runtime. Identifica por GUID la fuente y la malla. Mover esos assets dentro de Unity conserva el vínculo. El `.vox` debe permanecer junto a su sidecar con el mismo nombre base. Conservar todos sus archivos `.meta` al moverlos fuera del Editor.

Un prefab sin vínculo requiere crear una salida desde su `.vox`; no se infieren asociaciones por nombre. Duplicar un prefab vinculado no crea una fuente independiente: para editar otro modelo, duplicar el `.vox` y su sidecar y exportar esa nueva fuente.

El shader `Voxel Bridge/VoxelWorldOpaque` utiliza HDRP Lit y muestrea ambas LUT en el centro del texel, con LOD 0. La emisión es `BaseColor × SurfaceEmission × EmissionIntensity`; su intensidad global se ajusta en el material sin modificar meshes.

`Assets/VoxelBridgeImports/SharedMaterials` almacena un material por pareja de GUID de paletas. Las exportaciones posteriores reutilizan ese material y conservan su intensidad de emisión. Las LUT mantienen sus referencias al regenerarse. Modificar una paleta afecta a todos los modelos que comparten su material. Una referencia de shader o LUT incompatible detiene la exportación en lugar de sobrescribir ajustes silenciosamente.

Los prefabs generados sólo requieren sus mallas, material, shader y texturas en runtime. No necesitan el `.vox`, el sidecar, los ScriptableObjects de autoría ni Voxel Importer. La previsualización de Voxel Importer permanece independiente y puede diferir en propiedades PBR.

### Límites de esta exportación

- Sólo LOD0 semántico y superficies `Opaque`; otras clases producen un error explícito.
- Una sola malla de salida, aunque la entrada contenga varios modelos internos. No sustituye al flujo de chunks y familias para edificios grandes.
- Máximo de 8.000.000 de celdas, archivo `.vox` de 64 MiB y 500.000 quads. Estos límites acotan el trabajo de la exportación independiente; no son una garantía del consumo total de Unity. No modifican los presupuestos de voxelización ni reducen automáticamente la resolución.
- Sin generación de LODGroup, colliders, impostores, UV de lightmap o actualización automática tras editar el `.vox`.
- DOTS Instancing está habilitado en el graph y existe un flujo de prueba con Entities Graphics. La comprobación funcional inicial en subescenas no certifica todas las configuraciones de iluminación, rendimiento o plataforma. El horneado de impostores semánticos requiere una captura compatible.

## Familias semánticas de producción

1. Asignar `Conversion Profile` y seleccionar `Generate Levels > LOD0 Only` en `Physical Models and LODs`. Las LUT de las paletas deben estar actualizadas.
2. Convertir la fuente. La herramienta genera directamente un prefab semántico vinculado; `Place Result in Scene` permite colocarlo y desactivar el original.
3. Revisar `ColorID + SurfaceID` con `Semantic Bindings` y editar LOD0 mediante `Open in MagicaVoxel`, desde el Inspector del prefab. Utilizar `Rebuild` después de guardar.
4. Seleccionar el prefab de familia. `Duplicate Previous` copia el nivel anterior sin reinterpretar sus slots; `Reduce Previous` selecciona el par semántico mayoritario por celda, con desempate estable. `Generate Remaining LODs by Reduction` crea únicamente los niveles que faltan.
5. Utilizar `Open in MagicaVoxel`, `Semantic Bindings`, `Select Source` y `Rebuild` en la fila de cada LOD. Guardar en MagicaVoxel no reconstruye automáticamente el prefab.

La conversión física guarda el manifiesto, el prefab, las mallas de chunks y las fuentes `.vox` en `<Model>_VoxelLOD`. `All Profile Levels` genera cada nivel desde la malla original, con sus propias vinculaciones; no hereda retoques de otros niveles. La reducción posterior utiliza la unidad base y el multiplicador inicial efectivo de la familia, incluidos los ajustes para modelos grandes. Duplicar conserva la resolución del padre.

Para fuentes externas o convertidas sin perfil, asignar los IDs y utilizar `VOX to Unity > Create or Select Production LOD Family`. Esta operación crea `<Model>_ProductionLODs`, referencia LOD0 por GUID sin duplicarlo y utiliza su unidad efectiva como base. Si el `.vox` pertenece a una familia de producción vinculada, selecciona esa familia sin crear otra.

Cada nivel contiene renderers de chunks independientes. La eliminación de caras y cavidades consulta el volumen completo, incluidas las fronteras entre chunks. La geometría conserva sus coordenadas físicas, sin recentrar cada LOD. El prefab utiliza `LODFadeMode.None`, transiciones adaptativas y política de sombras del perfil. La colocación posterior puede utilizar el snapping del grid; reconstruir no mueve las instancias existentes.

`Rebuild` y `Rebuild All Meshes` leen los archivos editados, sin regenerarlos. Los meshes existentes conservan GUIDs y referencias. Un chunk que queda vacío conserva un mesh vacío para poder recuperar la misma referencia en una edición posterior. Los cambios de malla existentes admiten Undo/Redo agrupado por nivel; la creación de archivos y los cambios de estructura del prefab no constituyen una operación Undo completa.

El manifiesto conserva huellas de las fuentes y del padre utilizado para cada derivación. Si cambia un antecesor, sus descendientes muestran un aviso de revisión. Reconstruir el mesh del padre no elimina ese aviso ni modifica los `.vox` descendientes. `Regenerate: Duplicate` y `Regenerate: Reduce` requieren confirmación antes de reemplazar un nivel existente; conservan su GUID, pero descartan sus retoques manuales y no admiten Undo del archivo fuente. Los errores y cancelaciones restauran los archivos reemplazados. La reconstrucción de varios niveles confirma cada nivel por separado y se detiene ante un fallo.

Las familias admiten superficies opacas, una misma pareja de paletas globales y hasta 8 niveles consecutivos. Se mantienen los límites de 8.000.000 de celdas, 64 MiB de `.vox` y 500.000 quads por nivel; el límite de quads se aplica a la suma de sus chunks. La conversión física considera el límite de celdas al adaptar el tamaño inicial, antes de voxelizar. El meshing de una fuente editada no cambia su resolución: si supera un límite, se detiene con un error. Los colliders y UV de lightmap no se generan en esta ruta. El horneado de impostores semánticos está bloqueado hasta disponer de un shader de captura compatible con las LUT y los IDs.

## Materiales en conjuntos combinados

`Combine Voxel Models` une los pares globales de los LOD0 sin reconstruirlos desde RGB ni desde materiales de Unity. Requiere la misma pareja de paletas y revisiones vigentes. El archivo combinado crea su propia tabla de hasta 255 pares locales; el prefab reutiliza el material global compartido. Sus LODs derivados reducen el par completo mediante la regla mayoritaria existente.

Las piezas `Keep Original` conservan materiales separados y copias de sus mallas. Cambiar una fuente de entrada después de combinar no modifica el conjunto: su autoría reside en el nuevo `.vox` y sidecar. Consultar el [flujo de combinación](README.md#combinación-de-modelos-voxel) para alineación, conflictos y colocación.

## Validaciones

La herramienta comprueba:

- existencia del ID `0`;
- IDs duplicados o fuera de `0..255`;
- IDs activos que también figuren como retirados;
- nombres vacíos o duplicados;
- componentes de color y propiedades PBR fuera de `0..1`;
- contenido y configuración de las LUT;
- desactualización entre el ScriptableObject y su textura generada;
- límite de 255 pares locales por `.vox`;
- slots duplicados, desconocidos o fuera de `1..255`;
- pares semánticos duplicados;
- referencias a IDs globales inexistentes;
- huella de tabla o snapshot RGBA incoherentes;
- sidecar semántico incompleto o con versión desconocida;
- `IMAP` no compatible.

Las pruebas del mesher cubren los canales de IDs y las fronteras semánticas; las pruebas de producción y DOTS cubren referencias, baking e instanciación. La comprobación visual y de rendimiento de HDRP, HTrace, SRP Batcher y Entities Graphics debe ampliarse a escenas y plataformas representativas. La captura de Amplify Impostors y el round-trip externo con RGB duplicados siguen pendientes; consultar [ROADMAP.md](ROADMAP.md).
