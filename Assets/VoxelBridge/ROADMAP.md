# Hoja de ruta de Voxel Bridge

## Objetivo

Voxel Bridge debe producir familias voxel físicamente coherentes y editables, integrarlas en prefabs con LODs y permitir optimizaciones de renderizado de forma explícita. La conversión base no depende de impostores. Los impostores se reservan para assets y zonas donde una evaluación de visibilidad demuestre un beneficio suficiente.

## Orden de trabajo recomendado

1. Validar la conversión individual y por lotes, los LODs y la ruta opcional de Amplify Impostors.
2. Implementar las paletas globales de color y superficie, sus perfiles de selección, sus LUT y la validación de IDs estables.
3. Incorporar `ColorID` y `SurfaceID` al volumen voxel y al intercambio con MagicaVoxel mediante metadata versionada.
4. Incorporar los IDs al generador de meshes de producción y adaptar el shader compartido y el horneado de Amplify Impostors al muestreo de ambas paletas.
5. Validar visualmente la combinación manual de familias voxel para grupos estáticos y espacialmente compactos.
6. Validar la conversión a entidades, las subescenas, los LODs y el culling con el flujo DOTS previsto para producción.
7. Implementar el análisis de visibilidad y presupuesto por zonas sobre la estructura real del mapa.
8. Crear y hornear el plan final de impostores cuando la distribución del mapa y los materiales sean estables.

La representación técnica de materiales debe estar disponible antes de combinar familias. De este modo, la combinación opera sobre IDs globales y no requiere reconstruirse al abandonar las paletas locales. La consolidación artística de las entradas puede continuar después, pero debe estabilizarse antes de validar DOTS y hornear impostores definitivos. Un cambio de shader, paleta o material compartido puede requerir regenerar los atlas.

## Combinación manual de familias voxel

`Combine Voxel Models` es una herramienta separada de la conversión individual y por lotes. Construye un único asset lógico a partir de modelos voxel ya editados, por ejemplo un puesto de mercado, un conjunto de cajas o una estructura formada por varias piezas. El procedimiento y los límites se describen en [README.md](README.md#combinación-de-modelos-voxel).

### Entrada

- Una raíz de escena o una selección de instancias de prefabs de producción semántica Voxel Bridge. La primera versión requiere al menos dos instancias en la misma escena; no acepta manifiestos ni assets de Project directamente.
- Un `VoxelStyleProfile` que determine la unidad física, la rejilla, los LODs y los chunks de salida.
- Una carpeta y un nombre para la nueva familia.

La unión es exacta: los LOD0 deben utilizar la misma unidad voxel efectiva y estar alineados a la rejilla mundial. Las fuentes incompatibles quedan sin modificar y producen un error concreto. El remuestreo entre unidades diferentes se incorporará únicamente si los casos reales justifican la pérdida de detalle y el coste adicional.

### Proceso

- Leer los volúmenes LOD0 existentes para conservar los retoques realizados en MagicaVoxel.
- Transformar las celdas al espacio local de la nueva familia y ajustar el pivote a la rejilla física.
- Unir las celdas ocupadas dando prioridad a la primera fuente en el orden de la jerarquía. Los solapamientos con `ColorID` o `SurfaceID` diferentes requieren confirmación. Sólo se combinan celdas opacas; las piezas `Keep Original` mantienen su representación separada.
- Mantener los chunks internos necesarios sin convertirlos en familias independientes.
- Generar los LODs inferiores desde el volumen combinado, reduciendo el par `ColorID + SurfaceID` mediante una regla determinista.
- Crear un manifiesto, los `.vox` editables y un prefab con un solo `LODGroup`.
- Conservar las fuentes originales. La colocación y desactivación opcional en escena debe admitir Undo.

El impostor semántico del conjunto permanece bloqueado hasta adaptar el shader de horneado. La integración posterior debe capturar todos los renderers del LOD0 combinado como un único impostor.

### Límites de agrupación

El informe muestra los bounds y la ocupación; las fuentes de escenas distintas se rechazan. La detección de límites de streaming dentro de una escena y los umbrales espaciales requieren definir primero la partición del mapa. Un cluster demasiado grande reduce la eficacia del culling porque la visibilidad de una parte mantiene activo todo el conjunto.

No se realizará una combinación automática de escenas completas. La agrupación es una decisión artística y espacial explícita.

### Validación mínima

- Traslación, rotación y escala de las fuentes.
- Alineación del pivote y de la rejilla.
- Solapamientos y prioridad de color.
- Compatibilidad de las revisiones de las paletas globales y remapeo explícito cuando sea necesario.
- Preservación de `ColorID`, `SurfaceID` y clase de render en todos los LODs.
- Fronteras entre chunks.
- Regeneración y alineación de todos los LODs.
- Edición y guardado desde MagicaVoxel.
- Reconstrucción estable del prefab.
- Generación y eliminación opcional de un único impostor.

## Materiales compartidos

El sistema debe separar el color visible de las propiedades físicas de la superficie. La combinación de ambos datos se realiza en el shader y no mediante materiales o submeshes adicionales.

### Paletas globales e IDs estables

- `VoxelColorPalette.asset` define hasta 256 colores globales con IDs explícitos `0..255`.
- `VoxelSurfacePalette.asset` define hasta 256 superficies globales con IDs explícitos `0..255`.
- Reordenar las listas no modifica los IDs. Un ID retirado queda reservado y no se reutiliza automáticamente.
- El ID `0` representa una entrada predeterminada segura en ambas paletas.
- Cada paleta genera una LUT fija de `256 x 1`. La anchura no depende de la cantidad de entradas utilizadas.
- La LUT de color utiliza sRGB, filtrado Point, Clamp y no genera mipmaps.
- La LUT de superficies utiliza datos lineales RGBA32, filtrado Point, Clamp, sin mipmaps y sin compresión con pérdida.
- La LUT de superficies almacena inicialmente `Metallic`, `Smoothness`, emisión relativa y multiplicador de oclusión en RGBA.

La fuente de verdad son los ScriptableObjects. Las texturas generadas no se editan manualmente. La herramienta debe detectar IDs duplicados o fuera de rango, referencias inexistentes y LUT desactualizadas.

### Capacidad y materiales de Unity

El espacio global admite `256 colores x 256 superficies = 65.536` combinaciones visuales y físicas. Una combinación no crea un Material de Unity: todas las combinaciones opacas compatibles pueden utilizar un único `M_VoxelWorld`.

Se utilizan materiales adicionales únicamente cuando cambia el comportamiento de render, por ejemplo:

- `M_VoxelWorld` para superficies opacas estándar;
- `M_VoxelFoliage` para alpha clipping, doble cara y viento;
- `M_VoxelGlass` para transparencia o refracción;
- materiales especiales para agua, hologramas u otros efectos que requieran otro shader.

Una primera configuración debería mantenerse en aproximadamente tres a cinco materiales compartidos. El número exacto depende de los comportamientos de render necesarios, no de la cantidad de colores, superficies, modelos o LODs.

### Canales del mesh

- `UV0.x` contiene el `ColorID` crudo.
- `UV1` queda reservado para lightmaps horneados.
- `UV2` queda reservado para datos de iluminación en tiempo real u otra necesidad del pipeline.
- `UV3.x` contiene el `SurfaceID` crudo.
- Vertex Color queda disponible para suciedad, desgaste, variación de emisión y máscaras estilísticas.

Los IDs se almacenan inicialmente en canales `Vector2` para evitar el coste de un `Vector4` cuando sólo se utiliza una componente. Cada triángulo debe tener un único `ColorID` y un único `SurfaceID`; el mesher separa vértices en las fronteras semánticas.

### Transporte mediante `.vox`

El formato `.vox` sólo contiene un índice de paleta por voxel. `MATL` también está ligado a ese índice y no proporciona un segundo atributo independiente. Para conservar ambos IDs durante la edición en MagicaVoxel, cada slot local representa el par:

```text
slot local -> GlobalColorID + SurfaceID
```

El mismo RGB ocupa dos slots locales únicamente cuando necesita dos superficies distintas. La duplicación es una codificación de autoría y no crea materiales ni submeshes adicionales en Unity.

Cada `.vox` admite como máximo 255 pares locales utilizados simultáneamente. Este límite se aplica a un archivo o modelo concreto, no a la biblioteca global de 65.536 combinaciones. La herramienta debe advertir antes de exportar cuando una familia exceda el límite.

La correspondencia se conserva en el sidecar de Voxel Bridge. El sidecar incluye versión de formato, GUID y ruta de las paletas, sus huellas de contenido, una copia RGBA por slot y una huella de la tabla local. La reimportación se detiene ante slots desconocidos, remapeos ambiguos o pérdida de metadatos; no asigna `Default` silenciosamente.

Los chunks `NOTE` y `MATL` existentes se preservan, pero no son fuentes de identidad. `NOTE` representa filas de la paleta en la versión de MagicaVoxel utilizada y no proporciona una nota independiente por slot. Un `IMAP` no compatible debe detener la lectura semántica.

Debe existir una prueba de round-trip para la versión instalada de MagicaVoxel que verifique `XYZI`, `RGBA`, `NOTE`, `MATL` e `IMAP`. Si MagicaVoxel no conserva de forma estable los slots RGB duplicados y sus notas, la alternativa es un sidecar binario por voxel con reconciliación explícita de voxels añadidos, eliminados o desplazados.

### Autoría de superficies

La conversión desde FBX u OBJ resuelve `SurfaceID` con la siguiente precedencia:

1. asignación explícita mediante el componente `Conversion Rule`;
2. asignación explícita del Material de Unity en `Conversion Profile`;
3. superficie emisiva cuando la muestra supera el umbral de detección;
4. superficie predeterminada configurada por el usuario.

Las reglas `Voxelize`, `Ignore` y `Keep Original` se aplican antes del cálculo de la rejilla y del relleno. La selección por material utiliza referencias estables, no nombres ni aliases. Un material temporal puede etiquetar caras en Blender o Unity y se elimina como dependencia del mesh semántico después de transferir su ID al volumen voxel. Las piezas `Keep Original` conservan sus materiales y una copia estática vinculada a la familia.

La reducción manual de un volumen semántico selecciona la combinación mayoritaria con desempate estable. Duplicar un LOD conserva la tabla de slots sin reinterpretarla. El alcance de detección de emisión y la conservación de piezas se describen en [MATERIALS.md](MATERIALS.md#superficies-durante-la-conversión) y [README.md](README.md#reglas-de-conversión).

La pintura visual de SurfaceID por voxel requiere una fase de autoría específica. La selección entre una herramienta de Unity, un editor voxel adaptado o una integración de Blender permanece pendiente. La agrupación asistida podrá proponer conjuntos según color, intensidad y separación espacial; el equipo artístico asignará el tipo de superficie, sin inferir LED o neón a partir de la intensidad. La conversión utiliza una superficie emisiva de respaldo configurable, inicialmente Neon. El almacenamiento de los IDs debe conservar independencia respecto al editor elegido. La emisión con color independiente del albedo y los shaders voxel transparentes quedan fuera de la conversión opaca inicial.

### Mesh de producción

La exportación independiente LOD0 opaca está disponible en `VOX to Unity`, con greedy meshing, IDs en UV0/UV3 y material HDRP compartido. El prefab mantiene un vínculo de autoría por GUID y permite abrir la fuente en MagicaVoxel y reconstruir explícitamente la misma malla. El lector localiza chunks por tamaño y posición después de un guardado externo, sin depender de sus índices internos ni descartar modelos adicionales ocupados. Su alcance y límites se describen en [MATERIALS.md](MATERIALS.md).

Las familias semánticas de producción preservan chunks para culling, consultan vecinos a través de sus fronteras y mantienen la alineación física entre niveles. El prefab permite reconstruir cada nivel, derivar los siguientes por duplicación o reducción y revisar avisos de fuentes modificadas sin sobrescribir descendientes. El alcance, los límites de volumen y las operaciones con confirmación se describen en [MATERIALS.md](MATERIALS.md#familias-semánticas-de-producción). La reimportación automática y la ampliación de los límites de meshing requieren validación adicional de memoria y recuperación ante fallos.

La conversión física con `Conversion Profile` genera directamente una familia de producción mediante el mesher semántico, sin otro prefab de previsualización. La colocación individual y por lotes utiliza ese resultado vinculado; los controles de edición, bindings y reconstrucción están disponibles por nivel. El greedy mesher combina caras únicamente cuando coinciden color, superficie, orientación y clase de render.

El resultado utiliza un submesh por comportamiento real de render, no por `SurfaceID`. Voxel Importer permanece disponible para previsualizar y editar `.vox`, pero no es la fuente definitiva del mesh de producción. Esta separación evita parches profundos al asset de terceros y proporciona un contrato estable para combinación, LODs y DOTS.

### Shader, HTrace e impostores

El shader HDRP compartido muestrea ambas LUT y aplica la emisión como `BaseColor x SurfaceEmission x EmissionIntensity`. La escala HDR permanece en el material y puede cambiar sin regenerar meshes.

La validación debe cubrir HDRP, SRP Batcher, HTrace con Recursive Rendering y DOTS Instancing. No se utilizan `MaterialPropertyBlock` para seleccionar colores o superficies por renderer; los IDs pertenecen al vertex stream.

El shader de horneado de Amplify Impostors debe leer las mismas LUT y los mismos canales del mesh. El atlas debe reproducir Base Color, Metallic, Smoothness, oclusión y emisión. Los materiales definitivos se validan antes de producir atlas finales de impostores.

### Validación mínima

- IDs duplicados, retirados o fuera de rango.
- Superficies desconocidas y aliases sin correspondencia.
- Límite de 255 pares locales por `.vox`.
- Round-trip de slots, sidecar y chunks preservados en MagicaVoxel.
- Igualdad de IDs entre los tres vértices de cada triángulo.
- Consistencia entre LODs, chunks y familias combinadas.
- Ajustes de sRGB, filtrado, wrap, mipmaps y compresión de ambas LUT.
- Compatibilidad visual con HDRP, HTrace e impostores.
- Compatibilidad del material compartido con SRP Batcher y Entities Graphics.

## DOTS, subescenas y culling

La validación DOTS debe realizarse primero con los impostores desactivados para obtener una referencia clara. Debe comprobar:

- conversión de los prefabs Voxel Bridge a entidades;
- representación de los `LODGroup` y sus transiciones;
- compatibilidad de materiales y sombras con Entities Graphics;
- bounds de chunks y modelos combinados;
- instanciación de prefabs repetidos;
- carga y descarga de subescenas;
- culling por cámara y las opciones de oclusión disponibles para la configuración final del proyecto.

El tamaño máximo recomendado para una familia combinada debe derivarse de estos resultados y de la partición espacial de las subescenas.

## Análisis de visibilidad y presupuesto

El analizador debe ser una herramienta de Editor que no voxelice ni hornee atlas. Su función es evaluar una distribución avanzada del mapa antes de decidir los impostores definitivos.

### Muestreo

- Cuadrícula configurable sobre zonas transitables.
- Rutas de cámara definidas por el usuario.
- Perfiles separados para cámaras terrestres y aéreas.
- Altura, campo de visión, distancia de recorte y resolución objetivo.
- Varias orientaciones por punto de muestra.
- Conjunto de subescenas que pueden permanecer cargadas simultáneamente.

La primera versión puede utilizar una estimación conservadora basada en frustum y bounds. Las mediciones de oclusión y residencia GPU real deben añadirse después de disponer de una ruta DOTS representativa.

### Métricas

- Familias e instancias visibles por muestra.
- Atlas únicos potencialmente residentes, sin duplicar texturas compartidas.
- Perfil, resolución y mip estimado de cada impostor.
- Renderers, draw calls y LODs activos estimados.
- Pico, percentil habitual y principales contribuyentes por zona.

### Resultado

El informe debe presentar un mapa de calor y una lista ordenada de los assets que más contribuyen al coste. Las zonas se comparan contra un presupuesto global por plataforma; no reciben memoria independiente para consumir. Una zona poco densa conserva margen de forma natural, mientras que una zona densa debe indicar oportunidades para:

- combinar grupos estáticos compactos;
- reducir la calidad o eliminar impostores poco útiles;
- ajustar distancias de LOD y descarte;
- mejorar la partición de subescenas;
- aprovechar culling u oclusión cuando estén disponibles.

El análisis no debe modificar assets automáticamente.

## Plan final de impostores

Después de revisar el análisis, la herramienta podrá crear un asset de planificación con la decisión por familia: sin impostor, perfil seleccionado o excepción manual. La aplicación del plan horneará únicamente los atlas aprobados y registrará los fallos sin repetir la voxelización.

La generación presupuestada existente permanece como ruta alternativa y debe habilitarse de forma explícita. Su presupuesto se aplica solamente al lote actual y puede reducir perfiles u omitir familias para respetar el límite indicado.

## Fuera de alcance

- Edición voxel en runtime.
- Combinación automática de objetos distribuidos por toda una escena.
- Creación obligatoria de impostores durante la conversión.
- Modificación automática de calidades a partir del análisis sin revisión del usuario.
- Sustitución del sistema de streaming o culling de DOTS.
