# Geometría limpia y detalle superficial

## Estado y objetivo

Guía técnica para validar el acabado superficial de las familias voxel antes de ampliar las optimizaciones. El shader opaco de producción dispone de rejilla superficial opcional con anclaje mundial o de familia; el acabado avanzado, la gestión específica de anuncios y las mediciones de rendimiento permanecen pendientes.

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

Comparar la base geométrica candidata de `0.0625 m` con las familias existentes de `0.03125 m`; no reinterpretar metadatos de otras bibliotecas. Cambiar el tamaño de la rejilla visual no requiere reconversión ni modifica la geometría.

Comenzar con una rejilla opcional de intensidad ajustable, comparando normal, rugosidad y AO con una referencia sin detalle. Evaluar el microbisel y la variación superficial después de comprobar continuidad y estabilidad temporal. Comparar ruido calculado con una textura pequeña compartida cuando se incorpore desgaste. Evitar un sistema general de capas sin una necesidad demostrada.

## Rejilla superficial de producción

`Voxel Bridge/VoxelWorldOpaque` incluye la sección `Voxel Grid`. Activar `Grid Enabled` en el material semántico opaco permite aplicar el efecto a familias existentes sin reconvertir ni reconstruir sus mallas. El valor predeterminado es desactivado; los materiales existentes conservan su apariencia. La exportación reutiliza el material compartido y conserva sus ajustes. Modificarlo afecta a todas las familias que lo utilizan; para una comparación aislada, utilizar una copia del material.

Los valores iniciales de producción son `CellSize = 0.0625`, `Anchor = Family Local`, `Mode = Multiscale`, `TargetPixels = 12` y `MaxScaleLevels = 4`. El tamaño visual es una propiedad del material, no se deduce del perfil de conversión. Bibliotecas con otra unidad deben configurar un tamaño compatible. Antes de activar el anclaje local, comprobar el contrato descrito más abajo, especialmente el marco común de los chunks y la ausencia de `Batching Static`.

Para controlar el crecimiento por distancia, seleccionar `Grid Mode = Distance` en el shader de producción. `Grid DistanceStart` conserva la escala base hasta la distancia indicada; `Grid DistanceStep` determina los metros de cada transición al doble de tamaño. La distancia se calcula por punto de superficie a la cámara, no desde el pivote del objeto. Con valores `12` y `20`, la primera mezcla ocupa de 12 a 32 m, la segunda de 32 a 52 m y las siguientes el mismo intervalo, hasta `MaxScaleLevels`. Se mezclan rejillas binarias fijas, sin estirar sus celdas.

Este modo separa la escala artística del filtrado: suelo y pared con iguales parámetros y distancia seleccionan el mismo nivel, independientemente de su orientación, resolución o campo de visión. Las derivadas conservan el filtrado de frecuencias no resolubles; a ángulos rasantes puede atenuarse la rejilla sin aumentar su tamaño. No garantiza visibilidad constante ni elimina todo parpadeo. `TargetPixels` y `FadeStart / FadeEnd` no intervienen en `Distance`. Los materiales existentes conservan su modo; no se migran automáticamente.

La integración conserva LUT, ColorID, SurfaceID, emisión y ajustes de instancing. No afecta al vidrio, a `Keep Original`, a materiales RGB ni a las transiciones de geometría. Incluye normales, rugosidad y un ensayo opcional de POM cercano; no incluye bisel geométrico, desplazamiento de vértices, AO de juntas ni recorte SPOM.

### Perfil de juntas y biseles

`Grid Profile = Beveled` utiliza un campo de altura analítico con junta plana hundida, bisel suave y cara central plana. Las esquinas del contorno son redondeadas. La altura sólo se utiliza para calcular normales y la máscara de rugosidad: la malla, la silueta, el buffer de profundidad y las colisiones no cambian. No produce paralaje, sombras propias de la junta ni oclusión entre cubitos.

`Grid JointWidth` controla la separación total entre celdas; `Grid BevelWidth`, la anchura del bisel a cada lado; `Grid JointDepth`, la profundidad aparente. Los tres valores son proporciones de la celda visual activa. Como referencia inicial, utilizar `JointWidth = 0.04`, `BevelWidth = 0.06`, `JointDepth = 0.025` y `NormalStrength = 1`. La profundidad equivale a aproximadamente `1.56 mm` para una celda de `0.0625 m`; al crecer la rejilla conserva su proporción, no una profundidad fija en metros.

Profundidad cero anula la contribución del perfil. La anchura del bisel se limita internamente al intervalo `0.005–0.2` y la profundidad a `0–0.25`. `NormalStrength` modula las normales sin modificar la máscara de rugosidad. La parte central de la cara y el fondo de la junta permanecen planos; aumentar la profundidad inclina más el bisel, sin hundir vértices.

El perfil conserva los modos de escala y el filtrado existentes; las franjas sin detalle a ras de suelo y la estabilidad temporal requieren evaluación artística posterior. `Grid Profile = Lines`, predeterminado, conserva el aspecto anterior para comparar. Los parámetros de junta pertenecen al material opaco compartido; la variación de altura descrita a continuación pertenece a cada SurfaceID.

### Variación de altura por SurfaceID

`Cell Height Variation`, en el inspector de la paleta de superficies, controla hundimientos aparentes estables de las caras centrales. Mantener cero en superficies que deban ser uniformes; un punto inicial para hormigón o mampostería es `0.04`. Regenerar la LUT con `Rebuild Surface LUT` y utilizar `Beveled` en el material. El POM permite apreciar el cambio de nivel al mover la cámara; sin POM sólo varía la respuesta de los biseles a la luz.

La altura depende de la celda entera tridimensional en el anclaje elegido, no del tiempo, la cámara, el chunk o el índice de instancia. Las caras que resuelven la misma celda comparten el valor aleatorio; con `Family Local`, las instancias de una familia comparten el patrón. Mantener el contrato de coordenadas de familia. Las caras permanecen planas y hundidas respecto a la altura superior; una junta de fondo común evita discontinuidades entre celdas vecinas con el mismo perfil.

El hundimiento máximo se expresa como fracción de `CellSize`. La suma de `JointDepth` y variación se limita a `0.25` celdas, reduciendo primero la variación disponible. La profundidad física máxima del POM sigue limitando el conjunto. La variación se aplica únicamente a la cuadrícula base y se mezcla gradualmente con un perfil uniforme durante su primera transición; no se generan alturas aleatorias nuevas para las cuadrículas gruesas. Cambiar geometría, escala de celda o anclaje puede cambiar la celda resuelta.

Los valores pertenecen al SurfaceID, no al ColorID, y no alteran los archivos VOX ni las mallas. El vidrio permanece sin variación. Las paletas antiguas conservan variación cero y no necesitan migración automática. Esta etapa no añade silueta, offset de profundidad, colisiones ni sombras de los hundimientos; los límites de POM y las validaciones temporales pendientes siguen aplicándose.

### POM procedural cercano — experimental

Activar `Grid POM` con `Grid Profile = Beveled` permite comparar el perfil de normales con Parallax Occlusion Mapping. El trazado utiliza la altura procedural del bisel, con hasta 16 pasos de búsqueda y cuatro de refinamiento; no requiere textura de alturas ni UV adicionales. Los canales de ColorID/SurfaceID y las LUT permanecen intactos. El shader no depende del asset CSPOM ni incorpora su código.

`Grid POM MaxDepth` limita el desplazamiento en metros: valor inicial `0.003`, intervalo `0–0.01`. Es un límite, no un multiplicador. La profundidad efectiva es el menor valor entre `CellSize × min(JointDepth + Cell Height Variation, 0.25)` y ese límite. Para una comparación cercana más marcada, utilizar temporalmente `JointDepth = 0.08` con `POM MaxDepth = 0.003`, sobre una pared opaca uniforme. Restaurar una intensidad adecuada después de evaluar el movimiento.

El POM requiere cámara perspectiva. Se atenúa entre 2 y 8 m, a ángulos casi rasantes y durante la primera transición de escala visual; queda desactivado desde el primer nivel completo de celdas dobles. Con `DistanceStart = 0` y `DistanceStep = 4`, esto ocurre a 4 m. El recorrido lateral está limitado a `0.2` celdas. Estas restricciones mantienen el ensayo en el detalle cercano y evitan que los bloques distantes generen un relieve creciente.

La salida desplaza el muestreo del perfil de normales y rugosidad, no el buffer de profundidad. No modifica siluetas ni colisiones y no aporta la profundidad del relieve a las sombras o al AO de pantalla; tampoco desplaza el color de una cara hacia la identidad de otra. No certifica juntas entre regiones de atributos diferentes: el recorrido utiliza las propiedades de la cara rasterizada, no consulta los IDs de voxels vecinos. Evaluar primero paredes y suelos uniformes no emisivos; vidrio, offsets de profundidad y recorte SPOM permanecen fuera de este ensayo. El filtrado no garantiza ausencia de parpadeo. La evaluación temporal completa, el coste GPU y Entities Graphics en runtime permanecen pendientes.

### Escritura de profundidad del POM — variante experimental

`Voxel Bridge/Experimental/VoxelWorldOpaquePomDepth` comparte las LUT, propiedades y código POM de `VoxelWorldOpaque`, pero conecta la intersección al bloque `Depth Offset` de HDRP. La exportación continúa utilizando el shader original; seleccionar esta variante es una decisión explícita por material.

1. Duplicar un material semántico opaco y asignarle la variante experimental, conservando las referencias de paleta.
2. Mantener `Grid Enabled = On`, `Grid Profile = Beveled` y `Grid POM = On`.
3. Activar `Surface Options > Depth Offset` y `Conservative`. Alternar únicamente `Depth Offset` para comparar. Una copia del material original puede conservar esa opción apagada aunque el shader experimental la tenga activada por defecto.
4. Aplicar la copia a una superficie de prueba cercana. Reasignar el material original para retirar completamente la variante; no requiere reconvertir ni reconstruir los modelos.

La escritura sólo hunde píxeles: no adelanta profundidad hacia la cámara. Reutiliza el punto de intersección del POM y transforma su hundimiento normal en distancia a lo largo del rayo de vista. Conserva el límite normal en metros de `POM MaxDepth`, la atenuación cercana y el límite lateral de `0.2` celdas. Cuando ese límite lateral interviene, reduce conjuntamente el recorrido y la profundidad para mantenerlos sobre el mismo rayo. El modo fijo respeta además su fade; los niveles visuales gruesos, las cámaras ortográficas y el horneado no reciben offset de vista.

El pase `ShadowCaster` traza el mismo campo de altura desde la luz, incluyendo la proyección ortográfica de las luces direccionales. Una superficie plana de sombra por delante del receptor hundido produciría autosombreado falso. Este pase conserva el relieve físico sin los fades de cámara ni el filtrado por resolución de pantalla. Mantiene el límite de profundidad, pero no aplica el límite lateral de `0.2` celdas de la cámara: hacerlo reduciría la profundidad proyectada bajo luz oblicua, especialmente con variación alta. El recorrido de luz permanece acotado a menos de `2.1` celdas por la profundidad máxima de `0.25` celdas y el coseno de incidencia mínimo de `0.12`. No cambia el bias de las luces ni desactiva las sombras externas. La forma proyectada puede conservar depresiones milimétricas cuando el detalle de vista está atenuado. Evaluar por separado luz rasante, contactos finos y transiciones entre atributos: la aproximación sigue limitada por el muestreo del mapa de sombras y no sustituye una silueta SPOM.

Permite que una superficie situada detrás gane el test de profundidad en las juntas hundidas; las caras planas siguen ocultándola. Los consumidores de la profundidad de cámara pueden percibir ese relieve, pero la visibilidad del AO depende de su radio, sesgo, resolución y orden de ejecución. No incorpora AO propio, silueta SPOM, agujeros en la malla ni colisiones. Las intersecciones entre atributos distintos, la estabilidad con TAA/movimiento, Entities Graphics en runtime y el coste GPU —incluido el trazado adicional en mapas de sombras— requieren evaluación adicional. Mantener profundidades milimétricas y probar primero regiones uniformes no emisivas.

La variante utiliza un pase con escritura de profundidad cuando se activa `_DEPTHOFFSET_ON`; su coste no debe extrapolarse al shader original. `Conservative` permite al backend aprovechar las garantías de desplazamiento positivo. No certificar rendimiento sólo por compilar esa variante. Al modificar las conexiones de paleta o detalle del graph original, mantener sincronizado el graph experimental y ejecutar las pruebas de equivalencia con `Depth Offset` apagado.

En las pruebas interactivas, mantener desactivados los efectos de terceros que provoquen inestabilidad gráfica. No utilizar una ejecución con `-force-d3d12-debug` como referencia de rendimiento.

### SPOM de bloque cerrado — laboratorio aislado

`Voxel Bridge/Experimental/VoxelWorldBlockSpom` permite estudiar siluetas y laterales sobre un **cubo unitario cerrado**, no sobre mallas convertidas arbitrarias. El soporte conserva los triángulos del cubo: el shader recorre celdas e intersecta volúmenes biselados, escribe la profundidad del primer impacto y recorta los rayos que no encuentran superficie. Un núcleo sólido cierra el fondo de las juntas. Cámara y sombras utilizan el mismo volumen; no requiere DXR ni depende de CSPOM.

Ejecutar `Tools > Voxel Bridge > Experiments > Create SPOM Block Lab`. Genera y localiza un prefab nuevo bajo `Prototypes/BlockSpomLab`, con una referencia POM a la izquierda y el volumen SPOM a la derecha. Incluye una paleta `BlockSurfaces` y materiales propios; no modifica la paleta global, los modelos convertidos ni la escena abierta. No entra automáticamente en Prefab Mode: la estabilidad del render interactivo requiere una comprobación independiente. Las ejecuciones posteriores utilizan otra carpeta para conservar las pruebas existentes.

Configurar `Cell Height Variation` en la entrada `Default` de `BlockSurfaces` y regenerar su LUT. Ambos bloques comparten esa paleta. La base es `0.0625 m`; cada bloque mide `0.5 m` y contiene ocho celdas por eje. Mantener `Grid Enabled`, `Grid POM`, `Beveled`, `Family Local`, modo `Fixed`, `Depth Offset` y `Conservative` activos. Los biseles del volumen son planos y cerrados, no el perfil suave del POM; sus normales proceden de las caras intersectadas, por lo que `NormalStrength` no altera su orientación.

Límites del ensayo:

- Utilizar únicamente el cubo unitario de límites locales `[-0.5, 0.5]`. La escala define las dimensiones físicas; cada semieje debe medir un número entero de celdas entre uno y ocho. No usar cizallamiento ni batching estático. El shader no puede identificar si una malla arbitraria cumple ese contrato.
- Color y SurfaceID uniformes. La altura mantiene el hash estable por celda; las juntas y los biseles definen un volumen de comparación, no una reproducción exacta del perfil de altura POM.
- Sin crecimiento multiescala ni fades de distancia en el volumen. Los modos no compatibles mantienen el comportamiento POM de referencia. No utilizarlo como material general de la ciudad.
- Las colisiones, la geometría exportada y el horneado no cambian. Cámara y luces deben permanecer fuera del soporte. El muestreo temporal, el rendimiento y Entities Graphics en ejecución requieren evaluación adicional; compilar DOTS Instancing no certifica esa integración.
- El recorrido tiene un máximo de 64 celdas para un bloque de hasta 16 celdas por eje. Cada impacto utiliza planos analíticos para cerrar laterales y esquinas. Este coste adicional de fragmento y sombras no implica una optimización.

La integración en edificios y chunks requiere información explícita de ocupación, límites y atributos vecinos, además de un soporte que cubra el volumen. El ensayo por familia descrito a continuación aporta esos datos para LOD0; sustituir únicamente el shader no los proporciona.

### SPOM por familia LOD0 — copia de escena

`Voxel Bridge/Experimental/VoxelWorldFamilySpom` utiliza el VOX semántico de una familia para construir un volumen compartido por todos sus chunks. Cada texel RGBA8 contiene ColorID, SurfaceID, seis indicadores de cara expuesta y ocupación opaca. Un límite de chunk no se considera una superficie exterior. El shader conserva los huecos de la geometría y consulta los atributos del voxel intersectado, no sólo los del triángulo que inició el rayo.

Seleccionar una familia de producción activa, o uno de sus chunks, y ejecutar `Tools > Voxel Bridge > Experiments > Create Family SPOM Copy`. El comando genera assets independientes bajo `Prototypes/`, instancia una copia en la misma posición y desactiva la original. La escena no se guarda. La creación se agrupa en Undo; deshacerla restaura la instancia original, pero conserva los assets experimentales generados. Restaurar la original antes de preparar otra comparación sobre la misma instancia.

El proceso lee LOD0 y reconstruye sus mallas de soporte con el mesher existente, sin voxelizar otra vez el modelo. No escribe en los VOX, prefabs ni paletas originales. El vidrio conserva su shader y la geometría retenida permanece separada. `FamilyColors` y `FamilySurfaces` son copias editables: regenerar sus LUT tras cambiar colores, BRDF o `Cell Height Variation`. Cambiar ocupación, IDs o clases de render requiere generar otra copia completa.

Requisitos y límites:

- Familia semántica vinculada a su VOX LOD0; escala mundial uno, sin reflexión ni cizallamiento. Cada material SPOM pertenece a su volumen: no compartirlo entre familias diferentes ni asignarlo directamente a otros modelos.
- Máximo de 256 celdas por eje y ocho millones de celdas en la caja de datos. La ocupación utiliza cuatro bytes por celda, sin mipmaps; la memoria residente total y el coste GPU requieren medición.
- Relieve fijo a la unidad voxel original, sin multiescala visual ni desvanecimiento. `Grid Enabled` o `Grid POM` permiten desactivar el ensayo; `Depth Offset` y `Conservative` deben permanecer activos para el trazado. La copia contiene sólo LOD0, sin selección automática de LOD.
- Núcleos conectados en caras ocupadas y cubitos biselados en caras expuestas. Cámara y sombras consultan el mismo volumen; el recorrido está acotado por las dimensiones, con un máximo de 772 celdas. Las juntas entre SurfaceIDs distintos pueden tener profundidades diferentes.
- Ensayo rasterizado: los renderers de la copia no participan en ray tracing. No añade colliders, no valida horneado y no certifica estabilidad temporal ni Entities Graphics en ejecución. Mantenerlo en una pieza piloto antes de extenderlo a la ciudad.

### Prototipo de comparación

`Shaders/VoxelGridPrototype.shadergraph` conserva las LUT de color y superficie del shader opaco y añade `Shaders/VoxelGridDetail.hlsl`. El patrón proyecta sobre el plano principal de la cara en el espacio elegido mediante `Grid Anchor`, sin reutilizar UV0 o UV3. Modifica normales y smoothness; no desplaza geometría, profundidad, color, emisión ni colisiones. No incluye POM/SPOM.

`Grid Mode` permite comparar `Fixed` y `Multiscale`. El segundo mezcla dos rejillas consecutivas de tamaños `CellSize × 2^n`, alineadas al mismo origen del anclaje elegido. La escala se selecciona mediante derivadas de pantalla, por lo que responde a distancia, resolución, campo de visión y oblicuidad. No estira continuamente las celdas ni modifica las transiciones del LODGroup. Durante la mezcla pueden percibirse ambas rejillas: evaluar su lectura artística en movimiento.

Para probarlo, duplicar un material semántico opaco, asignarle el shader `Voxel Bridge/Prototypes/VoxelGridPrototype` y conservar sus referencias a las LUT. Aplicar la copia sólo a las instancias de comparación y a sus LODs, sin modificar el material compartido de producción. Los materiales y escenas de experimentación son recursos locales excluidos del repositorio.

| Control del material | Uso |
|---|---|
| Grid Enabled | `0`: referencia sin detalle; `1`: rejilla activa. |
| Grid Profile | `Lines`: perfil original. `Beveled`: junta plana y bisel suave con esquinas redondeadas, disponible en producción. |
| Grid POM / POM MaxDepth | Ensayo de POM cercano, desactivado por defecto; límite de desplazamiento en metros. Requiere `Beveled` y perspectiva. |
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
