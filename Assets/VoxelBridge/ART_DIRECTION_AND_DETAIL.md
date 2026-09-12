# Geometría limpia y detalle superficial

## Estado y objetivo

Enfoque de diseño para validar el acabado visual sobre una manzana piloto antes de ampliar las optimizaciones. El detalle procedural del shader y la gestión específica de anuncios descritos aquí no están implementados ni cuentan con mediciones de rendimiento. No requieren sustituir el flujo de autoría actual. La dirección del mundo y sus prioridades se describen en la [hoja de ruta de VoxelCity](../../Docs/WorldDesign/VoxelCity/ROADMAP.md).

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

- Utilizar la unidad voxel mínima del perfil como referencia física, no el tamaño efectivo de cada LOD. El patrón no debe cambiar de escala al cambiar de nivel.
- Definir un origen común para la familia, independiente del origen de cada chunk. Mantener la continuidad entre chunks y LODs.
- Anclar el patrón al modelo en vehículos y otros objetos dinámicos para evitar que se deslice al moverlos o rotarlos. La arquitectura estática alineada puede compartir una rejilla mundial con origen y orientación compatibles. Definir y verificar el tratamiento de escalas uniformes y no uniformes antes de afirmar que conserva su tamaño físico.
- Cuantizar el detalle a la rejilla cuando corresponda al estilo visual. Atenuar o filtrar las frecuencias finas a distancia para evitar aliasing y parpadeo; el muestreo puntual por sí solo no resuelve este problema.
- Considerar una semilla por instancia sólo si aporta variedad útil y puede transportarse sin romper el uso de materiales compartidos ni la compatibilidad con Entities Graphics.
- Mantener ColorID y SurfaceID como identidad de autoría. Definir los límites cromáticos de la variación visual; el ruido no debe crear nuevos IDs ni reinterpretar las superficies.

Para VoxelCity, la unidad objetivo del perfil es `0.03125 m`; no imponerla a otras bibliotecas ni reinterpretar los metadatos de modelos existentes. Preparar el perfil y regenerar explícitamente los modelos de la prueba urbana.

Comenzar con una rejilla opcional de intensidad ajustable, comparando normal, rugosidad y AO con una referencia sin detalle. Evaluar el microbisel y la variación superficial después de comprobar continuidad y estabilidad temporal. Comparar ruido calculado con una textura pequeña compartida cuando se incorpore desgaste. Evitar un sistema general de capas sin una necesidad demostrada.

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
