# Catálogo de superficies

La paleta recomendada contiene 44 perfiles opacos con IDs estables entre `0` y `43`, y el preset `Glass` con ID `44`. Los IDs `0–15` conservan los perfiles iniciales; `16–43` ofrecen acabados adicionales para vehículos y arquitectura. Los valores son puntos de partida artísticos, no mediciones físicas ni identificación automática de sustancias.

## Uso y compatibilidad

En el Inspector de `VoxelSurfacePalette`, seleccionar `Append Recommended` para incorporar entradas ausentes. La operación conserva las entradas existentes, omite IDs ocupados o retirados y nombres presentes, y admite Undo. No desplaza un preset a otro ID para resolver conflictos. Reconstruir la LUT mediante `Rebuild Surface LUT` cuando la validación indique que está desactualizada.

`Restore Recommended Surfaces` reemplaza el contenido y los IDs retirados; no es una operación de ampliación segura para una paleta personalizada.

Seleccionar una fila y abrir `Preview Selected Surface` para revisar sus valores actuales sin reconstruir la LUT. La ventana permite elegir otra superficie, alternar esfera y cubo, ajustar un color de referencia y rotar la cámara con el ratón. El color y la cámara pertenecen exclusivamente a la vista. No se crean materiales persistentes ni se modifican los modelos.

La vista utiliza iluminación HDRP fija y materiales temporales para Opaque y Glass. No reproduce la exposición, el bloom, las reflexiones ambientales ni las sombras de una escena de producción. La oclusión se muestra como valor; su efecto requiere geometría o mapas de oclusión. El vidrio utiliza transparencia básica; no simula refracción. Foliage y Special no se representan.

## Glass

El ID `44` utiliza `Glass (Transparent)`: Metallic 0, Smoothness 0,9, Emission 0 y Opacity 0,25. El tinte procede de ColorID. Es un cristal funcional de autoría, sin refracción, absorción por espesor ni sombras transparentes. La opacidad 1 cubre el fondo; los reflejos especulares pueden permanecer con opacidad baja. Consultar [Vidrio voxel básico](README.md#vidrio-voxel-básico) para asignación y reconstrucción.

## Perfiles adicionales

Todos los perfiles de esta tabla utilizan `Render Class = Standard Opaque` y `Occlusion = 1`. `M`, `S` y `E` representan Metallic, Smoothness y emisión relativa.

| ID | Perfil | M | S | E |
| --- | --- | --- | --- | --- |
| 16 | Asphalt Dry | 0 | 0,04 | 0 |
| 17 | Brick | 0 | 0,12 | 0 |
| 18 | Plaster | 0 | 0,08 | 0 |
| 19 | Stone Rough | 0 | 0,18 | 0 |
| 20 | Stone Honed | 0 | 0,42 | 0 |
| 21 | Stone Polished | 0 | 0,88 | 0 |
| 22 | Concrete Sealed | 0 | 0,38 | 0 |
| 23 | Terracotta | 0 | 0,20 | 0 |
| 24 | Tile Satin | 0 | 0,55 | 0 |
| 25 | Wood Raw | 0 | 0,16 | 0 |
| 26 | Wood Oiled | 0 | 0,40 | 0 |
| 27 | Wood Varnished | 0 | 0,72 | 0 |
| 28 | Leather Matte | 0 | 0,30 | 0 |
| 29 | Leather Polished | 0 | 0,60 | 0 |
| 30 | Vinyl | 0 | 0,50 | 0 |
| 31 | Rubber Smooth | 0 | 0,32 | 0 |
| 32 | Plastic Satin | 0 | 0,48 | 0 |
| 33 | Paint Matte | 0 | 0,22 | 0 |
| 34 | Paint Satin | 0 | 0,52 | 0 |
| 35 | Paint Gloss | 0 | 0,86 | 0 |
| 36 | Metal Cast | 1 | 0,14 | 0 |
| 37 | Metal Satin | 1 | 0,54 | 0 |
| 38 | Metal Machined | 1 | 0,74 | 0 |
| 39 | Metal Mirror | 1 | 0,98 | 0 |
| 40 | Rust | 0 | 0,13 | 0 |
| 41 | Emissive Indicator | 0 | 0,55 | 0,35 |
| 42 | Emissive Panel | 0 | 0,25 | 0,75 |
| 43 | Emissive Tube | 0 | 0,80 | 1 |

## Interpretación PBR

La metalicidad representa la respuesta conductora de la capa visible. Pintura y óxido se tratan como dieléctricos aunque cubran un soporte metálico. La documentación de [Filament sobre propiedades de materiales](https://google.github.io/filament/notes/material_properties.html) fundamenta esta distinción, la separación entre color y acabado y los límites del modelo isotrópico.

`Wood Varnished` y `Metal Machined` sólo aproximan un acabado mediante smoothness: no añaden una capa física de barniz, anisotropía ni microgeometría. `Metal Mirror` sigue siendo metal opaco; no implementa un espejo planar. Para diferenciar cobre, acero o aluminio, el color procede de `ColorID`; estos nombres no introducen curvas espectrales independientes.

La [Mask Map de HDRP](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Mask-Map-and-Detail-Map.html) almacena Metallic en R, oclusión en G y Smoothness en A. Estos canales describen apariencia, no identidad semántica. Madera, cuero, piedra o plástico pueden compartir valores similares; limitar los candidatos de conversión al contexto del modelo y revisar las propuestas visualmente. No es aconsejable habilitar todo el catálogo como clasificador universal.

Los emisivos son variantes de autoría: sus nombres no se deducen de la intensidad de entrada. La conversión puede conservar el fallback `Neon`; la elección entre LED, panel, indicador o tubo corresponde a la dirección artística. La emisión de la paleta es relativa y no expresa lúmenes, candelas ni potencia eléctrica.
