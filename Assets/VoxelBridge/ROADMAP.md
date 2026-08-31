# Hoja de ruta de Voxel Bridge

## Objetivo

Voxel Bridge debe producir familias voxel físicamente coherentes y editables, integrarlas en prefabs con LODs y permitir optimizaciones de renderizado de forma explícita. La conversión base no depende de impostores. Los impostores se reservan para assets y zonas donde una evaluación de visibilidad demuestre un beneficio suficiente.

## Orden de trabajo recomendado

1. Validar la conversión individual y por lotes, los LODs y la ruta opcional de Amplify Impostors.
2. Implementar la combinación manual de familias voxel para grupos estáticos y espacialmente compactos.
3. Definir la representación compartida de materiales y paletas que utilizarán los modelos voxel.
4. Validar la conversión a entidades, las subescenas, los LODs y el culling con el flujo DOTS previsto para producción.
5. Implementar el análisis de visibilidad y presupuesto por zonas sobre la estructura real del mapa.
6. Crear y hornear el plan final de impostores cuando la distribución del mapa y los materiales sean estables.

La consolidación artística de materiales puede continuar después, pero su representación técnica debe definirse antes de validar DOTS y antes de hornear impostores definitivos. Un cambio de shader, paleta o material compartido puede requerir regenerar los atlas.

## Combinación manual de familias voxel

La herramienta de combinación debe permanecer separada de la conversión individual y por lotes. Su finalidad es construir un único asset lógico a partir de modelos voxel ya editados, por ejemplo un puesto de mercado, un conjunto de cajas o una estructura formada por varias piezas.

### Entrada

- Una raíz de escena o una selección de prefabs, manifiestos o LOD0 pertenecientes a familias Voxel Bridge.
- Un `VoxelStyleProfile` que determine la unidad física, la rejilla, los LODs y los chunks de salida.
- Una carpeta y un nombre para la nueva familia.

La primera versión debe realizar una unión exacta y exigir que los LOD0 utilicen la misma unidad voxel efectiva y estén alineados al mismo lattice físico. Una fuente incompatible debe quedar sin modificar y mostrar una explicación concreta. El remuestreo entre unidades diferentes se incorporará únicamente si los casos reales justifican la pérdida de detalle y el coste adicional.

### Proceso

- Leer los volúmenes LOD0 existentes para conservar los retoques realizados en MagicaVoxel.
- Transformar las celdas al espacio local de la nueva familia y ajustar el pivote a la rejilla física.
- Unir las celdas ocupadas con una regla determinista para los solapamientos y mostrar la cantidad de conflictos de color antes de confirmar.
- Mantener los chunks internos necesarios sin convertirlos en familias independientes.
- Generar los LODs inferiores desde el volumen combinado.
- Crear un manifiesto, los `.vox` editables y un prefab con un solo `LODGroup`.
- Conservar las fuentes originales. La colocación y desactivación opcional en escena debe admitir Undo.

El impostor del conjunto permanece desactivado de forma predeterminada y se genera mediante una acción posterior. Todos los renderers del LOD0 combinado forman una sola captura de Amplify Impostors.

### Límites de agrupación

La combinación debe advertir cuando el conjunto tiene bounds excesivos, fuentes muy separadas o cruza los límites definidos para una subescena o celda de streaming. Un cluster demasiado grande reduce la eficacia del culling porque la visibilidad de una parte mantiene activo todo el conjunto.

No se realizará una combinación automática de escenas completas. La agrupación es una decisión artística y espacial explícita.

### Validación mínima

- Traslación, rotación y escala de las fuentes.
- Alineación del pivote y de la rejilla.
- Solapamientos y prioridad de color.
- Fronteras entre chunks.
- Regeneración y alineación de todos los LODs.
- Edición y guardado desde MagicaVoxel.
- Reconstrucción estable del prefab.
- Generación y eliminación opcional de un único impostor.

## Materiales compartidos

La fase de materiales debe definir un grupo reducido de materiales compatibles con HDRP y con la ruta de renderizado DOTS. El diseño debe cubrir:

- asignación estable de colores voxel a materiales compartidos;
- consistencia de paletas entre familias y LODs;
- comportamiento de los modelos combinados cuando las fuentes utilizan paletas diferentes;
- compatibilidad con Voxel Importer, sombras, mipmaps y horneado de impostores;
- separación entre parámetros compartidos y datos específicos del asset.

Los materiales definitivos deben validarse antes de producir atlas finales de impostores.

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
