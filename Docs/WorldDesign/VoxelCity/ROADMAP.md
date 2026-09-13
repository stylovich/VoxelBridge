# Hoja de ruta de VoxelCity

## Alcance

VoxelCity define la dirección del mundo y su construcción procedural. Voxel Bridge proporciona las herramientas reutilizables de conversión, autoría y producción de assets. Consultar la [guía visual](lenguaje_visual_ciudad_voxel.md) y la [hoja de ruta de Voxel Bridge](../../../Assets/VoxelBridge/ROADMAP.md) para sus contratos respectivos.

El proyecto de laboratorio permite regenerar los modelos de prueba antes de migrar las herramientas al proyecto del juego. Esa regeneración es explícita: ni un cambio de documentación ni un cambio de perfil actualizan automáticamente las fuentes y familias existentes.

## Decisiones de diseño

| Área | Criterio |
|---|---|
| Escala | Unidad urbana objetivo de `0.03125 m`, con resoluciones geométricas en múltiplos binarios. Un asset grande puede comenzar con una resolución más gruesa. |
| Rejilla visual | Comparación entre tamaño fijo y multiescala binaria alineada, independiente del LOD geométrico. Conservar la lectura de bloques a distancia mediante mezcla gradual y filtrado; no estirar continuamente la cuadrícula. |
| Anclaje | Rejilla propia para objetos dinámicos; origen mundial compatible para arquitectura estática alineada. Continuidad entre chunks de una familia. |
| Colocación estática | Regla propuesta: origen común, escala normalizada a uno, orientación en múltiplos de 90° y snapping compatible con la unidad y fase de la malla. La validación automática permanece pendiente; el snapping fino no garantiza alineación entre rejillas locales más gruesas. |
| Manzanas | Unidades de diseño compuestas por módulos y familias; no equivalen necesariamente a una única malla, rejilla voxel, familia combinada o unidad de streaming. |
| Autoría procedural | Semillas jerárquicas, identidades estables y propiedad de capas. Conservar receta y versión; respetar contenido congelado y retocado. |
| Detalles importantes | Utilizar el preview del siguiente LOD y los retoques explícitos disponibles. No añadir etiquetas de importancia ni prometer conservación automática de detalles subvoxel. |
| Representación distante | Evaluar LODs adicionales y HLOD de malla antes de impostores selectivos. Comparar memoria residente y coste de renderizado. |

## Prioridades y criterios de avance

### 1. Base disponible y preparación de assets

El laboratorio dispone de vidrio voxel básico y un perfil físico de `0.03125 m`. El siguiente entregable es la manzana de referencia, no una ampliación del shader de vidrio.

- Conservar la ruta `Keep Original` para los casos que la necesiten. La refracción y otros comportamientos avanzados no condicionan el piloto.
- Regenerar los modelos elegidos para el piloto que utilicen otra escala. Verificar dimensiones, pivotes, alineación y LODs; no reutilizar metadatos de otra escala como si fueran equivalentes.
- Mantener las validaciones específicas pendientes de intercambio con MagicaVoxel sin convertirlas en un rediseño general del editor.

### 2. Manzana de referencia

- Construir una manzana sencilla con calle, fachada, acceso, puente y cartel. La primera versión puede montarse manualmente con las herramientas existentes.
- Validar escala humana, espacio transitable, alturas, lectura de volúmenes y composición desde el nivel de calle.
- Utilizar iluminación diurna controlada y una escena pequeña sin APV como referencia inicial. No interpretar esta prueba como validación de la iluminación final del juego.

### 3. Acabado superficial y coherencia de LODs

- Utilizar la rejilla opcional del shader opaco de producción sobre los modelos convertidos. Comparar ausencia de rejilla, líneas, juntas biseladas, POM y hundimientos estables configurados por SurfaceID; evaluar después AO de juntas y siluetas. Comprobar estabilidad temporal y continuidad entre chunks, LODs y transformaciones antes de ampliar el acabado.
- Comparar `World` para módulos estáticos con `Family Local` para props y vehículos. Validar el marco compartido de sus renderers y mantener el batching estático clásico desactivado en el modo local. Evaluar la regla de colocación antes de automatizar su validación; no corregir posiciones de forma implícita.
- Evaluar el [ensayo de POM cercano](../../../Assets/VoxelBridge/ROADMAP.md#relieve-voxel-pomspom--ensayo-cercano) sobre el perfil biselado, con atenuación a distancia y sin requerir ray tracing. El ensayo usa altura procedural propia; CSPOM permanece como referencia para evaluar siluetas y profundidad, no como dependencia de producción aprobada.
- Posponer la señalización hasta consolidar la base voxel, el acabado superficial y su relación con los LODs. En esa fase, integrar [Vek](Vek_v1/LEEME.txt) mediante rasterización controlada de SVG y un atlas compartido sobre una pantalla y una pared. Conservar JSON y SVG como fuentes; probar legibilidad, escala, desgaste y emisión.
- Separar las imágenes del volumen voxel. No introducir un material por símbolo ni ampliar el mesher híbrido antes de evaluar esta ruta.

### 4. Generación procedural mínima

- Generar masas y fachadas con unas pocas reglas, semillas por capa y sockets de conexión compatibles.
- Incorporar congelado con propiedad explícita de capas, procedencia y protección de retoques. La duplicación de una familia guardada no sustituye este contrato.
- Comprobar que variar la decoración no altera la estructura ni sobrescribe contenido congelado. Extender después el ensayo a una cuadrícula 3×3 y evaluar variedad, recorridos y referencias espaciales.

### 5. Representación, carga y presupuesto

- Medir tiempos CPU/GPU, draws, memoria de mallas y texturas, y carga/descarga de subescenas con cámaras y plataforma objetivo representativas.
- Recalibrar qué objetos pequeños dejan de proyectar sombras y desde qué LOD, considerando la unidad voxel base y los niveles vigentes. Comparar coste y estabilidad visual antes de adoptar los umbrales; mantener esta revisión en la fase de rendimiento.
- Comparar LODs de malla, HLOD de malla e impostores únicamente donde procedan. Ajustar los grupos a visibilidad y streaming, no sólo a la jerarquía conceptual de la ciudad.
- Definir exclusión visual entre originales y sustitutos, así como su residencia. El culling no descarga recursos y más niveles no garantizan menor memoria.
- Adaptar el horneado semántico antes de evaluar impostores. Hornear atlas finales sólo para las zonas y familias aprobadas, con materiales suficientemente estables.

## Límites de esta etapa

El generador urbano, el congelado procedural, la integración de Vek y el sistema HLOD son trabajo pendiente. La rejilla dispone de un [prototipo estático aislado](../../../Assets/VoxelBridge/ART_DIRECTION_AND_DETAIL.md#prototipo-estático-de-rejilla), sujeto a validación artística, temporal y de rendimiento antes de integrarlo en producción. No se requiere implementar un volumen voxel adaptativo para toda la ciudad ni un sistema universal de capas de materiales. La suavización espacial de la preasignación PBR, el mesher híbrido y los comportamientos avanzados de vidrio permanecen sujetos a casos reales y evaluación posterior.
