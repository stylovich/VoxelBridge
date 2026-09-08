# Preasignación de superficies por semejanza PBR

La conversión puede proponer SurfaceIDs a partir de metallic y smoothness del material fuente. La comparación estima semejanza visual, no identifica sustancias físicas. Dos acabados con valores similares pueden ser indistinguibles aunque tengan nombres diferentes.

## Configuración

1. Crear un `Voxel Bridge > Surface Mapping Profile` o utilizar `Palettes/Profiles/SurfaceProfile_Vehicles.asset`.
2. Asignar la misma Surface Palette que utiliza el Conversion Profile.
3. Elegir los SurfaceIDs candidatos. Sólo se admiten IDs activos, únicos, opacos y no emisivos. Limitar el conjunto al contexto artístico; incluir toda la biblioteca incrementa la ambigüedad.
4. Configurar `Metallic Weight`, `Smoothness Weight`, `Maximum Distance` y `Minimum Separation`. Los pesos deben ser no negativos y sumar un valor positivo; los umbrales utilizan el rango `0..1`.
5. En el Conversion Profile, habilitar `Detect Emission`, asignar `Surface Mapping` y activar `Assign Surfaces From Pbr`. La opción está desactivada por defecto.
6. Realizar una conversión nueva desde la malla original. Cambiar el perfil no modifica los `.vox`, prefabs ni retoques existentes.

La distancia es la raíz de la media ponderada de las diferencias cuadráticas de metallic y smoothness. Una propuesta requiere distancia menor o igual al máximo y separación suficiente respecto a la segunda alternativa. Los empates conservan el fallback incluso con separación mínima cero. Los umbrales son criterios artísticos, no probabilidades de acierto. AO, nombre del material y color no participan en esta distancia.

## Prioridades

| Caso | Resultado |
|---|---|
| `Ignore` / `Keep Original` | Conserva el comportamiento de las reglas de conversión; no se clasifica esa geometría. |
| SurfaceID explícito de componente o material | Tiene prioridad sobre emisión y semejanza PBR. Se conserva la precedencia de las reglas existentes. |
| Muestra emisiva reconocida | Utiliza `Emissive Surface ID`, con Neon como respaldo inicial. No distingue LED de neón mediante intensidad. |
| Muestra PBR compatible y suficientemente diferenciada | Asigna el candidato más cercano. |
| Muestra ambigua, distante o no compatible | Conserva `Default Surface ID` e informa el motivo. |

`Single Color` conserva el color elegido, incluso en muestras emisivas. Cuando PBR está habilitado, los materiales disponibles también se consultan para separar las superficies emisivas; sin material se mantiene el color elegido y se utiliza el fallback de superficie.

## Compatibilidad del muestreo

El muestreo admite `HDRP/Lit` Standard opaco. Sin `_MASKMAP` utiliza las constantes `_Metallic` y `_Smoothness`. Con Mask Map activo, R define metallic y A smoothness; sus remapeos mínimo/máximo sustituyen las constantes. G es AO y B máscara de detalle: no se utilizan para identificar superficies. Esta distribución está descrita en la [documentación de Mask Map de HDRP](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Mask-Map-and-Detail-Map.html).

El Mask Map utiliza UV0 y el tiling/offset de la capa base (`_BaseColorMap_ST`), con wrap por eje y filtrado point o bilineal. Las texturas se leen como datos lineales y se reducen a un máximo de 512 píxeles por eje. Esta referencia de autoría no reproduce la selección de mip por derivadas de pantalla ni el filtrado anisotrópico del render final.

No se infieren otros shaders, variantes especiales de Lit, superficies transparentes, clear coat, mapas de detalle, desplazamiento, geometric specular AA, UV1–3, planar/triplanar ni Mask Maps HDR, flotantes o con signo. Los materiales con configuración emisiva no compatible requieren una regla explícita. Propiedades o UV no finitas producen fallback con aviso, no una asignación automática aproximada.

## Informe y revisión

Cada LOD convertido con PBR genera un archivo `<modelo>_LOD<n>.surface-report.json` junto al `.vox`. `Surface Assignment Report` en el Inspector del prefab permite abrirlo. El informe contiene:

- Candidatos, umbrales y huella de la configuración utilizada.
- Conteos de las celdas superficiales finales, agrupados por SurfaceID y decisión: `Explicit`, `EmissiveFallback`, `Automatic`, `Ambiguous`, `TooDistant` o `Unsupported`.
- Cantidad de celdas añadidas por relleno interior; éstas heredan semántica, no una nueva muestra PBR.
- Avisos resumidos de compatibilidad, limitados a 64 mensajes distintos.

El informe es una instantánea de la conversión, no un registro por voxel ni una descripción actualizada después de pintar o editar en MagicaVoxel. No permite seleccionar directamente grupos por procedencia. Utilizar las vistas `SurfaceID` y `Emission`, `Match` e `Isolate Selection` para revisar y corregir las asignaciones. Los emisivos de origen no se conservan como grupos de intensidad.

La conversión individual y por lotes utilizan el mismo muestreo y criterio. Las modificaciones del perfil invalidan el análisis y los checkpoints correspondientes. El límite de 255 pares ColorID + SurfaceID por `.vox` permanece activo: no se fusionan superficies silenciosamente para superarlo.
