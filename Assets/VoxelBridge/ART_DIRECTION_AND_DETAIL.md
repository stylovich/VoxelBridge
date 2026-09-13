# Geometría limpia y detalle superficial

## Estado y objetivo

Enfoque de diseño para validar el acabado visual sobre una manzana piloto antes de ampliar las optimizaciones. Existe un prototipo aislado de rejilla superficial con anclaje mundial o de familia; la gestión específica de anuncios y las mediciones de rendimiento permanecen pendientes. No requiere sustituir el flujo de autoría actual. La dirección del mundo y sus prioridades se describen en la [hoja de ruta de VoxelCity](../../Docs/WorldDesign/VoxelCity/ROADMAP.md).

Priorizar modelos con zonas amplias de ColorID y SurfaceID uniformes, reservando la geometría voxel para volumen, silueta, huecos y relieves intencionales. El detalle puramente superficial puede proceder del shader o de una imagen separada, en lugar de fragmentar las caras del modelo mediante pintura por voxel.

El mesher actual puede unir caras contiguas y coplanares cuando coinciden sus atributos; no une caras entre chunks. Uniformar ColorID y SurfaceID y reconstruir el nivel puede reducir divisiones por atributos, pero no elimina irregularidades geométricas ni actualiza automáticamente otros LODs. Un shader de detalle no simplifica por sí mismo una malla existente.

## Distribución del detalle

| Elemento | Representación propuesta |
|---|---|
| Pared, marco, soporte, cornisas y juntas profundas | Geometría voxel con regiones de color y acabado uniformes |
| Manchas, suciedad y variación superficial de color o rugosidad | Detalle del shader, con recursos compartidos cuando sea posible |
| Publicidad, ilustraciones, letras, logotipos y gráficos complejos | Quad con textura convencional, separado del marco y del soporte |
| Detalle que modifica la silueta o debe tener relieve real | Geometría explícita |

Esta distribución es una decisión de autoría, no una conversión automática de todos los modelos existentes. Conservar excepciones cuando el detalle voxel pintado o modelado sea una intención artística.

## Rejilla del detalle del shader

- Separar la unidad geométrica de la escala visual. El modo fijo conserva un tamaño físico; el modo multiescala mezcla rejillas binarias alineadas según su tamaño proyectado, sin depender del índice LOD geométrico.
- Definir un origen común para la familia, independiente del origen de cada chunk. Mantener la continuidad entre chunks y LODs.
- Anclar el patrón al modelo en vehículos y otros objetos dinámicos para evitar que se deslice al moverlos o rotarlos. La arquitectura estática alineada puede compartir una rejilla mundial con origen y orientación compatibles. El prototipo compensa la escala de los ejes para conservar el tamaño físico; no admite reflexión ni cizallamiento.
- Cuantizar el detalle a la rejilla cuando corresponda al estilo visual. Atenuar o filtrar las frecuencias finas a distancia para evitar aliasing y parpadeo; el muestreo puntual por sí solo no resuelve este problema.
- Considerar una semilla por instancia sólo si aporta variedad útil y puede transportarse sin romper el uso de materiales compartidos ni la compatibilidad con Entities Graphics.
- Mantener ColorID y SurfaceID como identidad de autoría. Definir los límites cromáticos de la variación visual; el ruido no debe crear nuevos IDs ni reinterpretar las superficies.

Para VoxelCity, comparar la base geométrica candidata de `0.0625 m` con las familias existentes de `0.03125 m`; no imponerla a otras bibliotecas ni reinterpretar sus metadatos. Cambiar el tamaño de la rejilla visual no requiere reconversión ni modifica la geometría.

Comenzar con una rejilla opcional de intensidad ajustable, comparando normal, rugosidad y AO con una referencia sin detalle. Evaluar el microbisel y la variación superficial después de comprobar continuidad y estabilidad temporal. Comparar ruido calculado con una textura pequeña compartida cuando se incorpore desgaste. Evitar un sistema general de capas sin una necesidad demostrada.

## Prototipo estático de rejilla

`Shaders/VoxelGridPrototype.shadergraph` conserva las LUT de color y superficie del shader opaco y añade `Shaders/VoxelGridDetail.hlsl`. El patrón proyecta sobre el plano principal de la cara en el espacio elegido mediante `Grid Anchor`, sin reutilizar UV0 o UV3. Modifica normales y smoothness; no desplaza geometría, profundidad, color, emisión ni colisiones. No incluye POM/SPOM.

`Grid Mode` permite comparar `Fixed` y `Multiscale`. El segundo mezcla dos rejillas consecutivas de tamaños `CellSize × 2^n`, alineadas al mismo origen del anclaje elegido. La escala se selecciona mediante derivadas de pantalla, por lo que responde a distancia, resolución, campo de visión y oblicuidad. No estira continuamente las celdas ni modifica las transiciones del LODGroup. Durante la mezcla pueden percibirse ambas rejillas: evaluar su lectura artística en movimiento.

Para probarlo, duplicar un material semántico opaco, asignarle el shader `Voxel Bridge/Prototypes/VoxelGridPrototype` y conservar sus referencias a las LUT. Aplicar la copia sólo a las instancias de comparación y a sus LODs, sin modificar el material compartido de producción. Los materiales y escenas de experimentación son recursos locales excluidos del repositorio.

| Control del material | Uso |
|---|---|
| Grid Enabled | `0`: referencia sin detalle; `1`: rejilla activa. |
| Grid Anchor | `World`: origen y ejes mundiales, valor predeterminado. `Family Local`: origen y ejes compartidos por los renderers de la familia; sigue su posición y rotación. |
| Grid CellSize | Tamaño físico fijo o mínimo en metros. Punto inicial de comparación: `0.0625`. |
| Grid Mode | `Fixed`: tamaño constante con atenuación; `Multiscale`: crecimiento visual binario independiente de la geometría. Los materiales existentes conservan `Fixed` por defecto. |
| Grid TargetPixels | Objetivo aproximado de tamaño en pantalla para multiescala, entre 4 y 64 píxeles. Aumentarlo produce bloques visuales mayores; punto inicial: `12`. |
| Grid MaxScaleLevels | Máximo exponente binario, limitado a 0–8. Con base `0.0625` y valor `4`, el máximo es `1 m`. |
| Grid JointWidth | Anchura total de la junta como fracción de celda; valor inicial `0.12`. |
| Grid NormalStrength | Intensidad del cambio de normal; `0` desactiva este componente. |
| Grid RoughnessStrength | Reducción de smoothness en juntas; no cambia SurfaceID. Una superficie con smoothness cero no puede hacerse más rugosa. |
| Grid FadeStart / FadeEnd | Sólo en modo fijo: distancias en metros para atenuar el efecto. Multiescala las ignora y conserva el filtrado de detalle no resoluble, incluso al alcanzar el tamaño máximo. |

Comparar con la misma cámara e iluminación. Para retirar la prueba, reasignar a los renderers afectados el material opaco original de su prefab; no revertir otros overrides de la instancia. Los prefabs, VOX y materiales de producción permanecen independientes del prototipo.

### Contrato de anclaje

- `World`: adecuado para arquitectura estática alineada a los ejes mundiales. Comparte fase entre familias, pero no ajusta sus posiciones ni garantiza coincidencia con bordes fuera de la rejilla.
- `Family Local`: utiliza el espacio local del renderer. Requiere que todos los chunks y LODs conserven el mismo sistema de coordenadas de la raíz de familia, como en las mallas de producción generadas por Voxel Bridge. Un pivote arbitrario o un chunk recentrado no cumple ese contrato. La escala positiva, uniforme o no uniforme, se compensa por eje para expresar `CellSize` en metros; no convierte voxels geométricos estirados en cubos.
- Ejecutar `Validate Family Local Grid` desde el menú contextual del componente `LODGroup`. Comprueba transformaciones relativas y rechaza `Batching Static`, lotes estáticos activos, reflexiones y cizallamiento. No modifica objetos ni certifica la unidad o fase de los vértices de la malla.
- Mantener `Batching Static` desactivado en los renderers con anclaje local: el batching estático clásico puede sustituir su sistema de coordenadas. Esto es independiente de DOTS Instancing.

El origen de la malla y su unidad física deben ser compatibles con la rejilla. El anclaje local evita el deslizamiento al trasladar o rotar la familia, pero no alinea módulos independientes entre sí. Tampoco garantiza que una celda visual multiescala coincida con cada borde geométrico fino: una celda grande agrupa deliberadamente varios voxels.

La validación completa de subescenas, Entities Graphics, movimiento y presupuesto GPU permanece pendiente. El shader conserva DOTS Instancing, pero compilar esa variante no sustituye una prueba de Entities Graphics. No utilizar este prototipo como shader de ray tracing.

### Colocación de arquitectura estática

La regla de diseño propuesta requiere origen de colocación común, escala normalizada a uno y orientaciones compatibles con los ejes de la rejilla —rotaciones en múltiplos de 90°—, incluyendo las transformaciones heredadas. Comprobar la fase de los vértices, no sólo la posición del pivote.

El snapping a `0.0625 m` no garantiza por sí solo coincidencia entre rejillas locales de `0.25 m` o mayores. Utilizar `World` cuando sea necesaria una fase visual común entre módulos, o acordar una fase de colocación compatible con la escala más gruesa. La validación automática de estas reglas pertenece a una etapa posterior; el prototipo no mueve ni corrige objetos de escena.

## Experimento de escala geométrica

El script independiente `Assets/Editor/HierarchyLodPreviewWindow.cs` abre `Tools > LocalModels > Preview LOD de jerarquía`. Asignar un GO padre de escena y utilizar un tamaño físico objetivo, por ejemplo `0.0625 m` o `0.125 m`, o elegir un índice LOD manual. La elección física lee el manifiesto de cada familia y multiplica su tamaño de celda por la escala mundial uniforme del LODGroup; omite grupos sin nivel coincidente, sin metadatos o con escala no uniforme. Los grupos desactivados permanecen intactos.

La previsualización fija un nivel existente a cualquier distancia mediante `ForceLOD`. `Volver a LOD automático`, cerrar la ventana, recompilar o entrar en Play restaura la selección automática. No modifica LODs, renderers, prefabs ni archivos de la familia. No combinar con otro control de `ForceLOD`; la restauración vuelve a automático, no a una selección forzada por otra herramienta.

Este ensayo permite valorar una geometría más gruesa sin reconversión; no valida transiciones ni equivale necesariamente a generar una familia nueva. La rejilla del shader es independiente. Con base `0.03125 m`, LOD1 mide `0.0625 m` en el asset y `0.125 m` en una instancia escalada ×2; a escala ×1 ese tamaño corresponde a LOD2. La base geométrica candidata de `0.0625 m` y el modo visual multiescala requieren comparación artística; el prototipo conserva el modo fijo como referencia.

## Anuncios separados de su estructura

Preparar el gráfico como una pieza independiente de la pared, el marco y el soporte. La imagen convencional es su fuente editable; no forma parte del VOX ni del intercambio de ColorID/SurfaceID con MagicaVoxel. El volumen voxel conserva su VOX y sidecar como fuente de verdad.

La separación permite cambiar publicidad y gestionar su resolución, emisión, LOD y visibilidad sin reconstruir la estructura. No obliga a utilizar un material exclusivo por anuncio: evaluar atlas o arrays de texturas compartidos cuando exista un conjunto representativo. Validar las restricciones de tamaño, filtrado y transporte de índices de la solución elegida.

Evitar superficies coincidentes que produzcan z-fighting. Revisar la separación respecto al soporte desde ángulos oblicuos y a distancia. Preferir una representación opaca cuando el diseño lo permita; las transparencias requieren evaluar su coste y ordenación. Conservar en geometría los relieves que afecten a la silueta.

## Plan de evaluación

1. Completar la validación pendiente de autoría y transporte semántico con MagicaVoxel.
2. Preparar un edificio y un cartel representativos dentro de la manzana piloto: estructura uniforme, gráfico separado y una referencia equivalente con detalle pintado por voxel. Conservar referencias de comparación y regenerar explícitamente los modelos de prueba que cambien de escala.
3. Prototipar una capa de detalle del shader alineada a la unidad mínima y una imagen sobre un quad separado. Conservar una opción sin detalle como referencia.
4. Comparar con la misma cámara, resolución e iluminación: vértices y triángulos, tiempos CPU/GPU, draws, memoria y muestreo de texturas. Más texturas o menos vértices no garantizan por sí solos una mejora.
5. Verificar continuidad entre chunks y LODs, transformaciones, estabilidad temporal, materiales compartidos, subescenas y Entities Graphics. Integrar el aspecto final en el futuro horneado de impostores antes de utilizar esos atlas en producción.

Evaluar este enfoque antes de ampliar el mesher híbrido con texturas de IDs. El meshing híbrido permanece como alternativa para modelos cuyo detalle por voxel deba conservarse; no es equivalente al quad de publicidad ni queda descartado por esta propuesta. Para la representación distante, evaluar LODs adicionales y HLOD de malla antes de recurrir a impostores selectivos. Comparar memoria residente y coste de renderizado sin asumir que una técnica gana en todos los assets.
