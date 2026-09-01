# Paletas y materiales voxel

## Alcance

El sistema de materiales voxel separa el color visible de las propiedades físicas de la superficie. Un modelo podrá reutilizar el mismo color con perfiles PBR diferentes sin crear un Material de Unity por combinación.

La primera fase proporciona las paletas globales, IDs estables, validación y generación de LUT. La escritura de `ColorID` y `SurfaceID` en volúmenes, archivos `.vox` y meshes pertenece a las fases posteriores descritas en [ROADMAP.md](ROADMAP.md).

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

## Contrato previsto para meshes y `.vox`

La integración de producción utilizará el siguiente contrato:

- `UV0.x`: `ColorID` crudo.
- `UV1`: lightmaps horneados.
- `UV2`: iluminación en tiempo real o reserva del pipeline.
- `UV3.x`: `SurfaceID` crudo.
- Vertex Color: máscaras estilísticas futuras.

El formato `.vox` sólo ofrece un índice de paleta por voxel. Voxel Bridge transportará cada combinación local como una correspondencia `slot -> ColorID + SurfaceID` acompañada por metadata versionada. La importación deberá detenerse cuando esa correspondencia sea desconocida o ambigua; no asignará una superficie predeterminada silenciosamente.

Hasta que esta integración esté implementada, las paletas globales no alteran la conversión, los materiales ni los prefabs existentes.

## Validaciones

La herramienta comprueba:

- existencia del ID `0`;
- IDs duplicados o fuera de `0..255`;
- IDs activos que también figuren como retirados;
- nombres vacíos o duplicados;
- componentes de color y propiedades PBR fuera de `0..1`;
- contenido y configuración de las LUT;
- desactualización entre el ScriptableObject y su textura generada.

Las fases posteriores deben añadir validación del transporte por `.vox`, consistencia entre LODs y chunks, vertex streams, HDRP, HTrace, SRP Batcher, Entities Graphics y Amplify Impostors.
