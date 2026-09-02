# Paletas y materiales voxel

## Alcance

El sistema de materiales voxel separa el color visible de las propiedades físicas de la superficie. Un modelo puede reutilizar el mismo color con perfiles PBR diferentes sin crear un Material de Unity por combinación.

El sistema proporciona paletas globales, IDs estables, generación de LUT y transporte de `ColorID + SurfaceID` en volúmenes y archivos `.vox`. El mesh de producción y el shader compartido permanecen descritos en [ROADMAP.md](ROADMAP.md).

## Assets canónicos

La ventana `Tools > Voxel Bridge > Paletas globales` crea y administra:

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

Los perfiles iniciales son valores de partida estilísticos. Deben calibrarse visualmente con el shader HDRP definitivo antes de producir materiales e impostores finales.

## Flujo de edición

1. Abrir `Tools > Voxel Bridge > Paletas globales`.
2. Seleccionar `Crear o cargar paletas canónicas` cuando los assets todavía no existan.
3. Seleccionar la paleta que se desea modificar.
4. Añadir, reordenar, renombrar o ajustar entradas desde su Inspector.
5. Corregir cualquier ID duplicado, fuera de rango, nombre duplicado o valor PBR inválido indicado por la validación.
6. Seleccionar `Regenerar LUT` y confirmar que el estado sea `Paleta válida y LUT actualizada`.
7. Versionar juntos la paleta y su LUT generada.

El número de ID es de sólo lectura en el Inspector para evitar cambios accidentales. Las migraciones intencionales de IDs requerirán una herramienta dedicada que también remapee los assets dependientes.

## Vinculación semántica de `.vox`

Abrir `Tools > Voxel Bridge > Vincular IDs semánticos` o utilizar `Assets > Voxel Bridge > Vincular IDs semánticos` sobre un archivo `.vox`.

La ventana muestra únicamente los slots utilizados por `XYZI`. Cada slot debe tener:

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
- `Architecture`: biblioteca no emisiva amplia y acentos intensos limitados;
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

El GUID es la referencia principal y permite mover la paleta dentro del proyecto. Un cambio de huella global produce una advertencia porque ajustar un perfil PBR sin cambiar su ID es válido. Un cambio de la tabla local o del RGBA utilizado produce un error.

Los sidecars de versiones 1 a 3 continúan utilizando colores RGB legacy. No se convierten automáticamente a `ColorID 0` ni reciben una superficie predeterminada.

## Conservación durante LODs y MagicaVoxel

Un volumen semántico almacena el par en 16 bits: ocho para `ColorID` y ocho para `SurfaceID`. Esta representación sustituye al array RGB de la rejilla y evita mantener ambas copias en memoria.

La reducción manual selecciona el par mayoritario dentro de cada nueva celda. Los empates se resuelven por el valor estable menor. La duplicación manual copia el `.vox` y su sidecar sin reinterpretación. Las familias generadas automáticamente desde una malla continúan en modo legacy hasta disponer de la asignación de superficies desde materiales fuente.

Voxel Bridge lee los índices directamente del binario `.vox`. El mesh y el atlas generados por Voxel Importer se utilizan como previsualización, no como fuente semántica, porque el importador puede compactar su paleta interna.

Los chunks `NOTE` y `MATL` se conservan, pero no determinan los IDs. Cualquier `IMAP` se rechaza hasta disponer de un fixture que verifique su dirección y comportamiento en la versión de MagicaVoxel utilizada.

El mismo RGB puede ocupar dos slots locales cuando necesita superficies diferentes. Voxel Bridge conserva esos slots mientras controla la escritura. MagicaVoxel puede reordenar slots visualmente idénticos al volver a guardar; este caso no se considera certificado hasta completar una prueba controlada de round-trip.

## Contrato previsto para meshes

La integración de producción utilizará el siguiente contrato:

- `UV0.x`: `ColorID` crudo.
- `UV1`: lightmaps horneados.
- `UV2`: iluminación en tiempo real o reserva del pipeline.
- `UV3.x`: `SurfaceID` crudo.
- Vertex Color: máscaras estilísticas futuras.

El mesher de producción consumirá el volumen semántico directamente. El material actual de Voxel Importer no cambia durante esta fase.

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

Las fases posteriores deben añadir validación de vertex streams, HDRP, HTrace, SRP Batcher, Entities Graphics y Amplify Impostors.
