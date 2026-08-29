# Voxel Bridge

Herramienta de Editor para convertir mallas de Unity a `.vox`, mantener una escala voxel física coherente entre assets, editarlas en MagicaVoxel y recuperar el resultado como un prefab con chunks y `LODGroup`.

## Flujo recomendado: perfil físico + LODs

1. Abre `Tools > Voxel Bridge > Modelos físicos y LODs`.
2. Crea un `VoxelStyleProfile` desde la ventana o mediante `Create > Voxel Bridge > Perfil de estilo voxel`.
3. Define `Base Voxel Size` en unidades de Unity. Para vóxeles de 10 cm usa `0.1`.
4. Define multiplicadores LOD estrictamente crecientes y en potencias de dos, por ejemplo `1, 2, 4, 8`.
5. Selecciona un FBX, OBJ, prefab u objeto de escena y pulsa `Generar familia .vox + prefab LOD`.

Cada LOD automático se voxeliza de nuevo desde la malla fuente: LOD0 usa la unidad base, LOD1 usa `base × 2`, LOD2 `base × 4`, etc. Las rejillas se alinean al mismo lattice físico para evitar cambios arbitrarios de tamaño o posición entre assets y niveles.

La familia generada contiene:

- un `.vox` editable por cada LOD;
- un sidecar `.voxelbridge.json` por cada `.vox` con escala, pivote, rejilla y chunks;
- un manifiesto `.voxset.json` que relaciona toda la familia;
- una subcarpeta propia dentro de `Carpeta de prefabs`, con un prefab estable que contiene el `LODGroup`, un hijo por LOD y todos sus renderers de chunk.

Regenerar o sustituir un LOD actualiza el mismo prefab indicado por el manifiesto; no crea copias sucesivas del prefab.

Cada modelo queda agrupado de esta forma: `Assets/VoxelBridgeImports/Nombre_VoxelLOD/Nombre_VoxelLOD.prefab`. Si una familia anterior tiene el prefab suelto directamente en `VoxelBridgeImports`, asigna su `.voxset.json` en `LOD manual` y pulsa `Reconstruir prefab desde manifiesto`; Voxel Bridge lo mueve a una subcarpeta conservando el GUID y las referencias existentes.

Unity muestra los `.vox` con el icono y la representación de un `GameObject` porque Voxel Importer genera sus mallas durante la importación. El archivo del disco sigue siendo `.vox`; no se reemplaza por el prefab. La sección `Resultados` muestra por separado la ruta del `.vox` editable y el prefab que debe colocarse en escena.

## LOD manual

Asigna el `.vox` del LOD padre en la sección `LOD manual` y elige uno de estos puntos de partida:

- `Duplicar anterior (libre)`: copia exactamente el `.vox` padre. Conserva el tamaño de vóxel del padre para que el artista pueda simplificar libremente en MagicaVoxel.
- `Reducir a resolución objetivo`: agrupa el volumen editado del padre sobre la rejilla física que corresponde al LOD destino. Ahorra el primer trabajo de reducción y luego permite retocarlo manualmente.

Ambas rutas reemplazan la entrada del nivel elegido en el manifiesto y reconstruyen el mismo prefab. Los archivos anteriores no se borran automáticamente para no destruir trabajo artístico; si se sustituye varias veces un nivel pueden quedar `.vox` huérfanos que se pueden revisar y eliminar manualmente.

## Modelos grandes y chunks

Cuando una rejilla supera `Chunk Cell Size`, Voxel Bridge escribe un único `.vox` VOX 200 con varios modelos y scene graph. Cada modelo ocupa como máximo 256 celdas por eje, conserva su posición global y se puede editar en MagicaVoxel como parte del mismo archivo.

Con Voxel Importer, un `.vox` con varios chunks se importa en modo `Individual`: el prefab conserva un renderer por chunk. Esto permite culling granular y encaja con el baking de subescenas/DOTS; el `LODGroup` referencia todos los renderers del nivel. No se combinan todos los chunks en una sola malla, porque hacerlo haría que un fragmento visible mantuviera renderizado el edificio completo.

Actualmente la salida está dividida en chunks, pero la fase de voxelización mantiene una rejilla densa global para que el relleno interior y las fronteras entre chunks sean correctos. Existe un límite de seguridad de 80 millones de celdas. Si se supera, aumenta `Base Voxel Size` o divide el asset fuente. Una futura voxelización por bloques podría retirar este límite sin cambiar el formato generado.

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

La interfaz de retorno está separada en `Tools > Voxel Bridge > VOX a Unity`. Allí puedes asignar un `.vox`, comprobar su ruta física, aplicar escala y pivote desde el sidecar, seleccionar el modelo importado o abrirlo en MagicaVoxel. También puedes resincronizar todos los generados con `Tools > Voxel Bridge > Sincronizar todos los .vox`.

Voxel Bridge necesita corregir las normales que Voxel Importer genera cuando `Import Scale` contiene ejes negativos. Después de instalar o actualizar Voxel Importer usa `Tools > Voxel Bridge > Compatibilidad > Aplicar parche de normales de Voxel Importer`. La acción es idempotente y reimporta automáticamente los `.vox` generados. Si una versión nueva cambia el código esperado, Voxel Bridge no modifica el asset y muestra un warning para que el conflicto se revise manualmente.

Para editar cualquier `.vox`, selecciónalo en Project y usa `Assets > Voxel Bridge > Abrir en MagicaVoxel`. La primera vez se solicitará la ubicación de `MagicaVoxel.exe`; Voxel Bridge recordará esa ruta para las siguientes aperturas. El mismo botón está disponible en las tres interfaces cuando existe una salida `.vox` válida.

Voxel Importer genera mallas normales durante la importación. El juego usa esas mallas; no dibuja millones de cubos ni mantiene vóxeles editables en runtime. Por ello, conservar los `.vox` directamente es el flujo más corto y mantiene la actualización automática al guardar desde MagicaVoxel. Para producción se usa el prefab generado, cuyos renderers provienen de esos `.vox`.

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
- La paleta se cuantiza actualmente por cada LOD. La consolidación de materiales/paletas compartidas entre familias queda separada de este flujo de geometría.
- `Assets/VoxelImporter` es una dependencia opcional del Asset Store y no forma parte del módulo portátil. Instálala por separado antes de aplicar su parche de compatibilidad.
