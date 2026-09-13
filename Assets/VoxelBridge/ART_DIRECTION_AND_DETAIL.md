# Geometría limpia y detalle superficial

## Estado y objetivo

Enfoque de diseño para validar el acabado visual sobre una manzana piloto antes de ampliar las optimizaciones. El shader opaco de producción dispone de rejilla superficial opcional con anclaje mundial o de familia; el acabado avanzado, la gestión específica de anuncios y las mediciones de rendimiento permanecen pendientes. No requiere sustituir el flujo de autoría actual. La dirección del mundo y sus prioridades se describen en la [hoja de ruta de VoxelCity](../../Docs/WorldDesign/VoxelCity/ROADMAP.md).

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

## Rejilla superficial de producción

`Voxel Bridge/VoxelWorldOpaque` incluye la sección `Voxel Grid`. Activar `Grid Enabled` en el material semántico opaco permite aplicar el efecto a familias existentes sin reconvertir ni reconstruir sus mallas. El valor predeterminado es desactivado; los materiales existentes conservan su apariencia. La exportación reutiliza el material compartido y conserva sus ajustes. Modificarlo afecta a todas las familias que lo utilizan; para una comparación aislada, utilizar una copia del material.

Los valores iniciales de producción son `CellSize = 0.0625`, `Anchor = Family Local`, `Mode = Multiscale`, `TargetPixels = 12` y `MaxScaleLevels = 4`. El tamaño visual es una propiedad del material, no se deduce del perfil de conversión. Bibliotecas con otra unidad deben configurar un tamaño compatible. Antes de activar el anclaje local, comprobar el contrato descrito más abajo, especialmente el marco común de los chunks y la ausencia de `Batching Static`.

Para controlar el crecimiento por distancia, seleccionar `Grid Mode = Distance` en el shader de producción. `Grid DistanceStart` conserva la escala base hasta la distancia indicada; `Grid DistanceStep` determina los metros de cada transición al doble de tamaño. La distancia se calcula por punto de superficie a la cámara, no desde el pivote del objeto. Con valores `12` y `20`, la primera mezcla ocupa de 12 a 32 m, la segunda de 32 a 52 m y las siguientes el mismo intervalo, hasta `MaxScaleLevels`. Se mezclan rejillas binarias fijas, sin estirar sus celdas.

Este modo separa la escala artística del filtrado: suelo y pared con iguales parámetros y distancia seleccionan el mismo nivel, independientemente de su orientación, resolución o campo de visión. Las derivadas conservan el filtrado de frecuencias no resolubles; a ángulos rasantes puede atenuarse la rejilla sin aumentar su tamaño. No garantiza visibilidad constante ni elimina todo parpadeo. `TargetPixels` y `FadeStart / FadeEnd` no intervienen en `Distance`. Los materiales existentes conservan su modo; no se migran automáticamente.

La integración conserva LUT, ColorID, SurfaceID, emisión y ajustes de instancing. No afecta al vidrio, a `Keep Original`, a materiales RGB ni a las transiciones de geometría. Normales y rugosidad constituyen el alcance de esta etapa; no incluye bisel geométrico, desplazamiento, AO de juntas ni POM/SPOM.

### Perfil de juntas y biseles

`Grid Profile = Beveled` utiliza un campo de altura analítico con junta plana hundida, bisel suave y cara central plana. Las esquinas del contorno son redondeadas. La altura sólo se utiliza para calcular normales y la máscara de rugosidad: la malla, la silueta, el buffer de profundidad y las colisiones no cambian. No produce paralaje, sombras propias de la junta ni oclusión entre cubitos.

`Grid JointWidth` controla la separación total entre celdas; `Grid BevelWidth`, la anchura del bisel a cada lado; `Grid JointDepth`, la profundidad aparente. Los tres valores son proporciones de la celda visual activa. Como referencia inicial, utilizar `JointWidth = 0.04`, `BevelWidth = 0.06`, `JointDepth = 0.025` y `NormalStrength = 1`. La profundidad equivale a aproximadamente `1.56 mm` para una celda de `0.0625 m`; al crecer la rejilla conserva su proporción, no una profundidad fija en metros.

Profundidad cero anula la contribución del perfil. La anchura del bisel se limita internamente al intervalo `0.005–0.2` y la profundidad a `0–0.25`. `NormalStrength` modula las normales sin modificar la máscara de rugosidad. La parte central de la cara y el fondo de la junta permanecen planos; aumentar la profundidad inclina más el bisel, sin hundir vértices.

El perfil conserva los modos de escala y el filtrado existentes; las franjas sin detalle a ras de suelo y la estabilidad temporal requieren evaluación artística posterior. `Grid Profile = Lines`, predeterminado, conserva el aspecto anterior para comparar. La selección por SurfaceID y los hundimientos aleatorios de celdas son fases posteriores; esta configuración pertenece al material opaco compartido.

### Prototipo de comparación

`Shaders/VoxelGridPrototype.shadergraph` conserva las LUT de color y superficie del shader opaco y añade `Shaders/VoxelGridDetail.hlsl`. El patrón proyecta sobre el plano principal de la cara en el espacio elegido mediante `Grid Anchor`, sin reutilizar UV0 o UV3. Modifica normales y smoothness; no desplaza geometría, profundidad, color, emisión ni colisiones. No incluye POM/SPOM.

`Grid Mode` permite comparar `Fixed` y `Multiscale`. El segundo mezcla dos rejillas consecutivas de tamaños `CellSize × 2^n`, alineadas al mismo origen del anclaje elegido. La escala se selecciona mediante derivadas de pantalla, por lo que responde a distancia, resolución, campo de visión y oblicuidad. No estira continuamente las celdas ni modifica las transiciones del LODGroup. Durante la mezcla pueden percibirse ambas rejillas: evaluar su lectura artística en movimiento.

Para probarlo, duplicar un material semántico opaco, asignarle el shader `Voxel Bridge/Prototypes/VoxelGridPrototype` y conservar sus referencias a las LUT. Aplicar la copia sólo a las instancias de comparación y a sus LODs, sin modificar el material compartido de producción. Los materiales y escenas de experimentación son recursos locales excluidos del repositorio.

| Control del material | Uso |
|---|---|
| Grid Enabled | `0`: referencia sin detalle; `1`: rejilla activa. |
| Grid Profile | `Lines`: perfil original. `Beveled`: junta plana y bisel suave con esquinas redondeadas, disponible en producción. |
| Grid Anchor | `World`: origen y ejes mundiales. `Family Local`: origen y ejes compartidos por los renderers de la familia; sigue su posición y rotación. Predeterminado en producción: `Family Local`; en el prototipo: `World`. |
| Grid CellSize | Tamaño físico fijo o mínimo en metros. Punto inicial de comparación: `0.0625`. |
| Grid Mode | `Fixed`: tamaño constante con atenuación; `Multiscale`: crecimiento binario por tamaño proyectado; `Distance`: crecimiento binario por distancia, disponible en producción. Ninguno modifica el LOD geométrico. Predeterminado en producción: `Multiscale`; en el prototipo: `Fixed`. |
| Grid DistanceStart / DistanceStep | Sólo `Distance`: inicio del crecimiento y recorrido por transición, en metros. Valores iniciales `12` y `20`; inicio limitado a cero como mínimo e intervalo a `0.01 m`. |
| Grid TargetPixels | Sólo `Multiscale`: objetivo aproximado de tamaño en pantalla, entre 4 y 64 píxeles. Aumentarlo produce bloques visuales mayores; punto inicial: `12`. |
| Grid MaxScaleLevels | Máximo exponente binario, limitado a 0–8. Con base `0.0625` y valor `4`, el máximo es `1 m`. |
| Grid JointWidth | Anchura total de la junta como fracción de celda; valor inicial `0.12`. |
| Grid BevelWidth / JointDepth | Sólo `Beveled`: anchura del bisel y profundidad aparente como fracciones de celda; valores iniciales `0.06` y `0.025`. |
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

La validación completa de subescenas, Entities Graphics, movimiento y presupuesto GPU permanece pendiente. Ambos shaders conservan DOTS Instancing, pero compilar esa variante no sustituye una prueba de Entities Graphics. El efecto de rejilla no se evalúa en ray tracing.

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
