# Voxel Bridge

Herramienta de Editor para convertir mallas de Unity a `.vox`, editarlas en MagicaVoxel y recuperar el resultado como un prefab con la escala y el pivote originales.

## Unity a MagicaVoxel

1. Selecciona un FBX, OBJ, prefab u objeto de escena.
2. Abre `Tools > Voxel Bridge > Mesh a MagicaVoxel` (también aparece en el menú contextual de Assets).
3. Empieza con resolución 64. Usa 96–128 solo si la silueta necesita más detalle; 256 puede consumir bastante memoria.
4. Deja `Rellenar interior` activo para modelos cerrados. Desactívalo para láminas, ropa abierta o superficies que quieras conservar huecas.
5. Pulsa `Convertir a .vox` y luego `Abrir en MagicaVoxel`.

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

Para corregir un `.vox` creado antes de esta integración, selecciónalo y usa `Assets > Voxel Bridge > Aplicar escala al .vox`, o asígnalo en la sección 2 de la ventana y pulsa `Aplicar metadatos y reimportar`. También puedes resincronizar todos los generados con `Tools > Voxel Bridge > Sincronizar todos los .vox`.

Voxel Bridge necesita corregir las normales que Voxel Importer genera cuando `Import Scale` contiene ejes negativos. Después de instalar o actualizar Voxel Importer usa `Tools > Voxel Bridge > Compatibilidad > Aplicar parche de normales de Voxel Importer`. La acción es idempotente y reimporta automáticamente los `.vox` generados. Si una versión nueva cambia el código esperado, Voxel Bridge no modifica el asset y muestra un warning para que el conflicto se revise manualmente.

Para editar cualquier `.vox`, selecciónalo en Project y usa `Assets > Voxel Bridge > Abrir en MagicaVoxel`. La primera vez se solicitará la ubicación de `MagicaVoxel.exe`; Voxel Bridge recordará esa ruta para las siguientes aperturas. El mismo botón está disponible en la sección 2 de la ventana.

Voxel Importer genera una malla normal durante la importación. El juego usa esa malla; no dibuja millones de cubos ni mantiene vóxeles editables en runtime. Por ello, el `.vox` directo es el flujo más corto y conserva la actualización automática.

## MagicaVoxel OBJ a Unity (alternativa)

1. En MagicaVoxel guarda los cambios y usa `Export > obj`.
2. Guarda el OBJ y sus texturas dentro de una carpeta bajo `Assets`.
3. En la tercera sección de Voxel Bridge asigna el OBJ importado y el `.voxelbridge.json` correspondiente.
4. Mantén `MagicaVoxel Default` salvo que hayas cambiado los ejes de exportación en `config.txt`.
5. Pulsa `Crear prefab alineado`.

El prefab conserva el pivote local y escala cada unidad del OBJ al tamaño del vóxel original. Si se cambiaron las opciones `io_*` de MagicaVoxel, usa `Already Unity Aligned` o ajusta la rotación del hijo una vez creado.

## Consideraciones

- El formato VOX clásico almacena como máximo 256 celdas por eje y 255 colores por modelo.
- Los detalles más pequeños que un vóxel desaparecerán; esta simplificación es parte del resultado estético.
- El relleno depende de que la superficie sea razonablemente cerrada. Agujeros en la malla pueden dejar el interior vacío.
- `Rellenar interior` desactivado crea una carcasa de un vóxel de grosor. Sus caras internas son reales. `Ignore Cavity` oculta solo cavidades cerradas; una ventana, puerta o grieta conecta la cavidad con el exterior. Para eliminar siempre las caras internas, usa relleno sólido.
- El muestreo de texturas se limita internamente a 512 px por material para evitar picos innecesarios de memoria.
- `Assets/VoxelImporter` es una dependencia opcional del Asset Store y no forma parte del módulo portátil. Instálala por separado antes de aplicar su parche de compatibilidad.
