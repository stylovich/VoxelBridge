# Hoja de ruta de Voxel Bridge

## Objetivo

Voxel Bridge debe producir familias voxel físicamente coherentes y editables, integrarlas en prefabs con LODs y permitir optimizaciones de renderizado de forma explícita. La conversión base no depende de impostores. Los impostores se reservan para assets y zonas donde una evaluación de visibilidad demuestre un beneficio suficiente.

## Orden de trabajo recomendado

1. Mantener la conversión individual y por lotes, las paletas globales, el transporte semántico y los prefabs de producción como base del flujo.
2. Validar artísticamente `Surface Painter`, la edición de ColorID/SurfaceID, sus herramientas de selección, cuentagotas, vistas de diagnóstico, aislamiento y comparación temporal de acabados sobre modelos representativos.
3. Validar artísticamente el preview temporal del siguiente LOD con el reductor real; continuar la validación de la preasignación PBR y del intercambio con MagicaVoxel.
4. Completar las validaciones funcionales pendientes de materiales especiales, iluminación y carga/descarga de subescenas sobre assets representativos.
5. Preparar con VoxelCity una manzana piloto de geometría uniforme, detalle superficial separado y señalización en textura. Retomar las mediciones de rendimiento por LOD, sombras, transparencias, culling y streaming sobre esa distribución representativa.
6. Evaluar LODs de malla adicionales y HLOD de malla antes de recurrir a impostores selectivos. Adaptar el horneado de impostores a las paletas semánticas antes de incluirlos en la comparación; no imponer un atlas por edificio.
7. Implementar el análisis de visibilidad y presupuesto por zonas, apoyado en las mediciones anteriores.
8. Crear y hornear únicamente los impostores aprobados por el plan de representación cuando la distribución del mapa y los materiales sean estables.

La consolidación artística de las paletas puede continuar durante la validación funcional de DOTS. Las paletas y los materiales deben estabilizarse antes de producir atlas definitivos: un cambio de shader, paleta o material compartido puede requerir regenerarlos.

### Coordinación con VoxelCity

Voxel Bridge mantiene la responsabilidad sobre conversión, paletas, autoría, familias y representaciones de producción. La [hoja de ruta de VoxelCity](../../Docs/WorldDesign/VoxelCity/ROADMAP.md) organiza escala urbana, composición, generación procedural, sockets, semillas y propiedad de las capas editables.

- Unidad objetivo del perfil urbano: `0.03125 m`, con múltiplos binarios y origen coherente. Preparar el perfil y regenerar explícitamente los modelos del laboratorio antes de trasladarlos al proyecto del juego; no reinterpretar fuentes existentes ni imponer la escala a otras bibliotecas.
- Rejilla visual: comparar el modo fijo con crecimiento multiescala binario para conservar la lectura de bloques a distancia, independientemente del LOD geométrico. El prototipo ofrece anclaje mundial y local de familia con un marco común entre renderers; la validación de colocación estática y la integración de producción permanecen pendientes.
- Primera referencia compartida: una manzana sencilla, seguida del prototipo de detalle del shader y Vek en textura; la generación procedural y la prueba 3×3 parten de esa referencia.
- Importancia de detalles: evaluación con el preview y retoques de LOD existentes, sin añadir un atributo ni una herramienta específica de prioridad. La conservación automática de siluetas subvoxel no forma parte de esa garantía.

### Estado por bloque

| Bloque | Estado y alcance pendiente |
|---|---|
| Conversión y reglas | Disponible: flujo físico individual y por lotes, normalización opcional Z-up → Y-up con colocación compensada, exclusión, `Keep Original` y detección de emisión con superficie de respaldo. |
| Materiales semánticos | Disponible: paletas, catálogo recomendado de 45 superficies, visor HDRP temporal, perfiles cromáticos, bindings por slot, LUT, mesher con separación opaco/vidrio y shader HDRP de vidrio básico. |
| Familias y combinación | Disponible: edición de fuentes, reconstrucción, duplicación independiente de familias guardadas, derivación de LODs y unión exacta de conjuntos compactos. La duplicación de variantes, prefabs anidados e impostores horneados queda fuera del alcance actual. |
| DOTS y subescenas | Integración y diagnóstico disponibles; validación funcional inicial de instancias, recursos compartidos y frustum. No equivale a certificar rendimiento, sombras, oclusión o streaming de producción. |
| Autoría visual de superficies | Disponible: selección espacial y semántica, edición independiente de ColorID/SurfaceID, diagnóstico, aislamiento, comparación temporal de acabados y reducción al siguiente LOD, Undo/Redo y guardado recuperable. Pendiente: validación artística, rejilla local de reducción y certificación del intercambio externo de RGB duplicados. |
| Intercambio de RGB duplicados | Pendiente de certificar en MagicaVoxel; la escritura y lectura controladas por Voxel Bridge conservan los pares. |
| Preasignación PBR | Disponible y opcional para HDRP/Lit Standard: candidatos por perfil, muestreo de constantes o Mask Map UV0, distancia y separación mínimas, fallback e informe por LOD. Pendientes: validación artística, calidad espacial de la clasificación y ampliación de shaders/configuraciones según casos reales. |
| Geometría limpia y detalle separado | Prototipo aislado de rejilla, modos fijo y multiescala binaria, anclaje mundial o de familia y validador de su marco local. Pendientes: validación artística y temporal, colocación estática, integración con Entities Graphics, anuncios sobre quads y mediciones. |
| Meshing híbrido | Fase posterior: evaluar caras coplanares con texturas de ColorID/SurfaceID para modelos cuyo detalle por voxel deba conservarse, después de evaluar la autoría con detalle superficial separado. |
| Impostores semánticos | Horneado bloqueado hasta adaptar la captura a las LUT y los canales de IDs. La ruta RGB existente no demuestra compatibilidad semántica. |
| Rendimiento, HLOD y presupuesto | Fase diferida hasta disponer de una distribución representativa. Evaluar LODs adicionales y HLOD de malla antes de impostores selectivos, midiendo memoria y renderizado. |

## Autoría visual de superficies en Unity

### Alcance de la primera versión

`Surface Painter` es una extensión de Editor para asignar colores y superficies sobre una fuente semántica existente. No sustituye a MagicaVoxel para modelado ni a `Semantic Bindings` para asignaciones de slots completos. El [procedimiento de uso](README.md#edición-visual-de-superficies) detalla las operaciones disponibles y sus límites. No requiere paquetes nuevos ni cambios en el shader de producción.

- Entrada desde el Inspector del prefab de producción mediante `Edit` en una fila de LOD (`Edit Surfaces` en una salida independiente), o arrastrando el prefab padre desde Project al campo `Source` y eligiendo un nivel en `Edit LOD`. Ambas entradas conservan el vínculo de reconstrucción. LOD0 es la entrada recomendada; seleccionar otro nivel requiere una elección explícita.
- Una sola fuente por sesión, identificada por GUID y fijada aunque cambie la selección de la escena. Los prefabs combinados se editan sobre su propia fuente, no sobre los modelos utilizados para construirlos.
- Vista aislada del volumen con órbita, desplazamiento, zoom hasta escala de celda, encuadre del volumen o selección, pincel de tamaño ajustable y rectángulo. Selección visible con reemplazo, adición y sustracción; cálculo por tandas, vista provisional y cancelación sin alterar la selección anterior.
- Selector de `SurfaceID` con ID, nombre y propiedades PBR de referencia. `Apply Surface` modifica únicamente la superficie de las celdas seleccionadas; no edita la definición global, el ColorID, la ocupación, la escala ni el pivote.
- Modo `Color` con cuadrícula lateral, búsqueda por nombre/ID y muestras de los ColorIDs permitidos por el perfil fuente, conservando SurfaceID. Ambos atributos comparten historial y recuperación del borrador. Miniaturas de superficies y `Try on Selection` permiten comparar candidatos con flechas de teclado, confirmación y cancelación, sin modificar el borrador durante la comparación. La vista SurfaceID incluye una leyenda de las superficies utilizadas en el modelo o grupo aislado.
- Previsualización Lit con el shader semántico y vistas Base Color, SurfaceID y Emission mediante un shader exclusivo del Editor. El perímetro de las regiones coplanares seleccionadas conserva huecos, grupos separados y cambios de plano, sin cuadrícula y con oclusión por profundidad. `Tint` activa un tinte suave opcional sin cambiar las asignaciones.
- `Isolate Selection` fija un grupo visible y limita a él tanto la representación como las herramientas; `Show All` restaura el volumen. El historial que recupera selecciones externas termina el aislamiento. Guardar y reconstruir siempre procesan la fuente completa. El aislamiento y las vistas no modifican el transporte ni los materiales de producción.
- `Undo` y `Redo` para selecciones confirmadas, limpieza de selección y asignaciones pendientes, en un historial cronológico compartido con atajos contextuales `Ctrl+Z`, `Ctrl+Y` y `Ctrl+Shift+Z`. Seleccionar no marca la fuente como pendiente de guardar. `Save Source Only` persiste la fuente; `Save & Rebuild` requiere un prefab vinculado y guarda y reconstruye ese LOD. `Discard Changes` descarta el borrador. El historial no revierte archivos guardados ni se mezcla con el de la escena.

La unidad de asignación es el voxel completo, no una cara: todas sus caras expuestas utilizan la misma superficie. La selección inicial no atraviesa geometría ni incluye automáticamente voxels interiores. Las piezas `Keep Original` no pertenecen al volumen editable y deben identificarse como excluidas. No se admite edición en Play Mode.

### Integración y datos

| Sistema | Integración |
|---|---|
| `VoxelProductionLink` y `VoxelProductionFamily` | Resolver fuente y LOD por GUID y mantener los avisos de reconstrucción o derivación pendiente. |
| `VoxelVolumeReader` y `VoxelSceneGraph` | Leer la rejilla y resolver la correspondencia espacial de chunks; no asumir que el índice interno de MagicaVoxel permanece estable. |
| `VoxelGrid` y `VoxelSemanticEncoding` | Editar el atributo activo del par de 16 bits, conservando los ocho bits del otro atributo. |
| `VoxelSurfaceEdit`, `VoxelSemanticVoxDocument` y `VoxelSemanticTransport` | Editar un buffer local, generar candidatos por celda y reutilizar la validación de IDs y metadatos. |
| `VoxelSurfaceEditStore` | Guardar el par de archivos con un registro de recuperación y protección contra conflictos externos. |
| `VoxelSemanticMesher` y exportadores de producción | Previsualizar y reconstruir sin introducir otro mesher ni modificar assets durante un trazo. |

El formato previsto continúa siendo sidecar v4 con bloque semántico v1. Si un subconjunto de voxels azules pasa de `Default` a `Aluminum`, la escritura utiliza otro slot local para el par azul + aluminio, mientras conserva el ColorID global. Un par existente reutiliza su slot; un par nuevo requiere un slot libre. No se modifica la superficie de todos los voxels que compartían el slot original.

El escritor debe preservar posiciones, ocupación, scene graph y chunks ajenos a la edición. La correspondencia celda → registro `XYZI` debe proceder de la misma resolución espacial utilizada por el lector. Los slots de pares sin cambios deben conservarse cuando sea posible; no se debe reutilizar la ordenación por frecuencia del cuantizador para renumerar toda la paleta en cada guardado. Los chunks `MATL` y `NOTE` no definen la identidad semántica ni requieren nuevas propiedades para este editor.

La previsualización debe utilizar recursos temporales aislados, sin crear GameObjects o colliders por voxel ni modificar materiales compartidos. La selección se calcula sobre la rejilla. Reconstruir la previsualización al confirmar una asignación, no por cada evento del ratón. Mantener límites explícitos de selección, historial y malla; no ampliar los límites actuales de lectura y meshing como parte de esta fase.

### Guardado, Undo y recuperación

1. Cargar una fuente válida, sus paletas y las huellas del `.vox` y sidecar. Bloquear fuentes RGB sin bindings, IDs desconocidos, clases de render no compatibles, `IMAP` y correspondencias espaciales ambiguas.
2. Mantener cambios locales y un historial acotado de diferencias por operación. Evitar una copia completa de millones de celdas por cada trazo. Una operación sin cambios no crea entradas de historial.
3. Antes de guardar, comprobar que fuente, sidecar y paletas no hayan cambiado externamente. Un conflicto exige recargar o resolver las asignaciones; no se intenta fusionar automáticamente con un guardado de MagicaVoxel o de `Semantic Bindings`.
4. Validar el límite de 255 pares utilizados y releer los bytes candidatos para comprobar geometría, ColorID y SurfaceID antes de reemplazar archivos. El exceso de pares debe dejar intactos fuente y edición pendiente, sin aproximar superficies ni colores.
5. Guardar `.vox` y sidecar como una operación recuperable, conservando sus `.meta`. Dos reemplazos de archivo no constituyen una transacción conjunta: se utilizan copias de recuperación y un registro de escritura interrumpida que bloquea la lectura semántica de producción. Un fallo recuperable restaura ambos originales. La previsualización de terceros no constituye una fuente de identidad semántica.
6. Después de guardar, establecer una nueva base de edición y reiniciar el historial local. Undo no debe aparentar revertir archivos ya guardados. `Rebuild` actualiza únicamente el nivel editado; un fallo de meshing deja la fuente guardada y la malla anterior con su aviso de desactualización.

Cerrar la ventana o cambiar de fuente permite guardar, descartar o cancelar. Entrar en Play Mode deshabilita la edición y conserva el borrador, sin interrumpir la prueba de la escena. La serialización de la ventana conserva el borrador durante una recarga de scripts y verifica sus huellas antes de restaurarlo; el historial Undo se reinicia. El borrador es estado local del Editor, no una nueva fuente de verdad ni una copia persistente garantizada ante un crash. Guardar explícitamente antes de cerrar el Editor o realizar operaciones de riesgo.

### Relación con MagicaVoxel y LODs

`Duplicate Editable Family` permite preparar una familia independiente como referencia o variante artística sin modificar el original. Copia los recursos editables guardados y mantiene compartidas las paletas, perfiles y materiales. El [procedimiento de duplicación](README.md#duplicación-de-familias-editables) describe sus límites; no sustituye la validación externa de slots con RGB duplicados.

El flujo recomendado será: preparar geometría y colores de LOD0 en MagicaVoxel, asignar superficies en Unity, guardar, reconstruir y derivar los LODs inferiores. Las ediciones en MagicaVoxel posteriores a la pintura requieren conservar slots y sidecar. Dos slots con RGB idéntico pueden intercambiarse sin que una comparación de RGBA detecte el cambio; las huellas locales no certifican por sí solas ese intercambio.

La validación externa debe guardar y reabrir un fixture con el mismo ColorID y superficies distintas, varios chunks y un cambio controlado de color u ocupación. Comparar los pares por coordenada, no sólo el aspecto visual. Hasta completar esa prueba, el flujo de ida y vuelta con RGB duplicados mantiene una advertencia accesible en la ayuda y en `Source Actions > External Editing Notice`, y no debe presentarse como certificado. Si falla, evaluar una representación por voxel con reconciliación explícita antes de ampliar el formato; no introducirla preventivamente.

Los descendientes existentes nunca se sobrescriben al guardar una superficie. `Rebuild` no transmite cambios entre niveles. Crear niveles mediante reducción o duplicación sí parte de los pares del padre; actualizar un descendiente existente requiere `Regenerate`, con la advertencia de pérdida de sus retoques. La reducción prioriza caras expuestas compatibles y utiliza mayoría por volumen para interiores. Los emisivos no tienen prioridad incondicional: regiones pequeñas o acabados que comparten un voxel grueso pueden perderse. Evaluar la fidelidad artística además de la reducción de geometría.

### Preview de reducción al siguiente LOD

Disponible: comparación del borrador completo, incluidos cambios sin guardar, con el resultado temporal de `Reduce` al siguiente nivel. Utiliza el reductor, resolución, padding y alineación de la familia y conserva cámara y orientación al alternar. El preview no representa el LOD guardado ni modifica fuentes, mallas persistentes, historial o retoques de descendientes. El [procedimiento de uso](README.md#preview-de-reducción-al-siguiente-lod) describe sus controles y límites.

- Primera entrega disponible: alternancia borrador/reducción, vistas Lit, Base Color, SurfaceID y Emission, información de nivel y tamaño voxel, y diagnóstico de contribuciones expuestas cuyo ColorID/SurfaceID pierde frente al par elegido. Pendiente de validación artística sobre más modelos representativos.
- Las contribuciones interiores o caras incompatibles no deben presentarse como pérdidas visibles de acabado. Conservar un par no garantiza conservar silueta, huecos ni relieve; comparar también la geometría.
- Calcular bajo demanda con cancelación, reutilizar resultados mientras el borrador y los parámetros sigan vigentes, e identificar resultados desactualizados. No recalcular al mover el puntero.
- Fase posterior: rejilla gruesa local cerca del pincel o selección e inspección de votos por bloque. El resultado reducido tiene prioridad sobre la rejilla; la herramienta no limita el pincel ni obliga a pintar por bloques.

### Entregas y criterios de aceptación

1. **Edición y transporte por celda:** mismo ColorID con dos superficies, cambios parciales de un slot, varios chunks, reutilización de pares, límite de 255, no-op y preservación de geometría/metadatos.
2. **Interfaz mínima:** selección visible, aplicación, previsualización, Undo/Redo, cancelación, cambio de fuente, recarga de scripts y liberación de recursos temporales.
3. **Integración de autoría:** guardado y recuperación ante fallo entre archivos, rechazo de conflictos externos, reconstrucción del prefab manteniendo referencias y avisos de LODs derivados. Prueba de round-trip real con MagicaVoxel antes de certificarlo.
4. **Asistencia de selección:** selección por ColorID, SurfaceID, color similar y regiones conectadas, con alcance visible u oculto explícito. La clasificación como LED, neón u otro material sigue siendo explícita. La intensidad emisiva original no se conserva como atributo por voxel en el formato actual; agrupar por esa intensidad exigiría ampliar su captura y transporte.

Quedan fuera de esta primera versión la modificación de geometría, pintura por cara, selección a través del volumen, edición de follaje voxel, un editor general de materiales, clasificación automática por intensidad y reemplazar MagicaVoxel, Blender o Vengi.

### Estado de las herramientas de corrección

Implementar por bloques verificables, manteniendo las selecciones separadas de la aplicación de cambios:

1. **Selección espacial — disponible:** tamaño de pincel ajustable y selección rectangular, con reemplazo, adición y sustracción. El muestreo de pantalla selecciona la primera celda por rayo y no atraviesa geometría; los detalles subpíxel requieren acercar la vista. El gesto es cancelable y respeta límites de selección y trabajo. Evaluar la comodidad artística sobre modelos representativos antes de considerar un modo de selección a través del volumen.
2. **Selección semántica — disponible:** `Match` por ColorID, SurfaceID o color similar mediante OKLab. `Connected` recorre vecinos por caras; `All Matching` consulta todo el volumen. `Visible Only` limita la búsqueda a centros de caras visibles, con una elección explícita para incluir ocultos. La búsqueda es cancelable y conserva los límites de selección.
3. **Inspección — disponible:** `Pick` y `Use Surface` consultan ambos IDs y toman la superficie sin pintar. `Base Color` separa el color de la iluminación PBR; `SurfaceID` muestra colores de diagnóstico por ID y `Emission` resalta presencia emisiva en la LUT, sin bloom ni clasificación por luminosidad. El aislamiento utiliza un grupo fijo, con selección coherente con las caras visibles, y no recorta los archivos guardados. La validación artística debe revisar legibilidad y comodidad sobre assets representativos.
4. **Edición visual de ColorID — disponible:** operación independiente que conserva SurfaceID y respeta el perfil cromático vinculado. El historial y el borrador transportan ambos atributos; la escritura valida que los cambios de ColorID procedan de operaciones autorizadas y conserva geometría y pares supervivientes.
5. **Comparación visual de acabados — disponible:** miniaturas HDRP y preview Lit sobre una selección fija. Recorrer candidatos reutiliza la geometría temporal; confirmar crea una operación y cancelar no altera datos. Validar comodidad, fidelidad visual y respuesta con selecciones grandes o fragmentadas.

La representación HDR con bloom y una comparación visual antes/después pueden evaluarse después de estas herramientas. La referencia normalizada actual no constituye una vista de iluminación final. La captura de grupos emisivos de origen permanece pendiente; la preasignación PBR utiliza los materiales durante la conversión. Las herramientas de selección no recuperan atributos que no estén almacenados en el `.vox` y su sidecar.

## Vidrio voxel básico

Disponible: preset Glass, opacidad en la paleta, shader HDRP transparente, separación de submeshes y conservación de caras opacas visibles a través del cristal. El relleno de conversión respeta los espacios conectados al exterior a través de vidrio, sin eliminar las ventanas ni el relleno de piezas opacas cerradas. La asignación utiliza las reglas existentes por GameObject o material, o el Surface Painter. `Keep Original` permanece como alternativa independiente; no requiere el shader voxel. Los usos y límites se describen en [Vidrio voxel básico](README.md#vidrio-voxel-básico).

Las celdas de conversión compartidas por vidrio y geometría opaca válida priorizan el opaco para conservar barreras. Los bordes transparentes subvoxel requieren revisión de resolución o uso de `Keep Original`; no se incorpora una segunda superficie por celda.

Pendientes para la revisión de shaders: refracción, absorción por espesor, sombras transparentes, capas solapadas y estrategias de ordenación. Ampliar las comprobaciones en subescenas y plataformas objetivo antes de certificar esos casos; no extrapolar el render en Editor a un presupuesto de producción. La rejilla local del preview de reducción permanece diferida.

## Preasignación de superficies por semejanza PBR

La preasignación está disponible como opción del Conversion Profile. Propone superficies existentes a partir de la apariencia del material fuente, sin identificar su composición física ni crear definiciones PBR por voxel. [SURFACE_MAPPING.md](SURFACE_MAPPING.md) describe configuración, compatibilidad, prioridades e informes.

- `VoxelSurfaceMappingProfile` selecciona SurfaceIDs candidatos opacos no emisivos de la paleta global, sin duplicar definiciones ni LUT. `SurfaceProfile_Vehicles` ofrece un conjunto inicial; los perfiles pueden adaptarse a otros contextos artísticos.
- Muestrear metallic y smoothness durante la voxelización, con los valores del material cuando no haya mapa y con las UV, transformaciones de textura y remapeos propios del shader cuando sí lo haya. Comenzar con HDRP/Lit; configuraciones no compatibles conservan el fallback y producen un aviso resumido.
- En el Mask Map de HDRP, R representa metallic, G oclusión, B máscara de detalle y A smoothness. No utilizar AO ni la máscara de detalle como evidencia de identidad física. No interpretar texturas de shaders diferentes mediante este esquema sin soporte explícito.
- Comparar las muestras con los candidatos permitidos mediante distancia ponderada de metallic y smoothness. Exigir tanto proximidad suficiente como separación respecto a la segunda mejor alternativa. Los umbrales son criterios artísticos configurables, no probabilidades estadísticas de acierto.
- Aplicar primero las reglas explícitas de componente y material. Separar muestras emisivas de no emisivas; conservar el fallback emisivo cuando no exista una elección explícita. No inferir LED o neón a partir de la intensidad.
- El informe `.surface-report.json` conserva conteos de celdas superficiales finales por SurfaceID y decisión, separa el relleno interior y resume incompatibilidades. Es una instantánea de conversión; no persiste procedencia por voxel ni se actualiza con retoques posteriores. No impone atributos adicionales en runtime.
- La asignación automática debe ser explícitamente habilitable, determinista y respetar el límite de 255 pares por `.vox`. No aproximar colores o fusionar superficies silenciosamente para superar ese límite.

La clasificación debe ocurrir antes de exportar a `.vox`, mientras están disponibles los materiales fuente y sus mapas. Los `.vox` existentes no permiten reconstruir las muestras PBR originales; requerirán una conversión nueva o asignación manual. El volumen final mantiene `ColorID + SurfaceID`, mientras las propiedades físicas proceden de la paleta global.

La validación debe cubrir materiales constantes, mapas con distintas zonas, UV y remapeos, empate o cercanía entre candidatos, perfiles vacíos o inválidos, prioridad de reglas explícitas, separación de emisivos y consistencia de conversión individual/por lotes. Los retoques guardados en una fuente no se deben sobrescribir como efecto lateral de cambiar el perfil.

### Calidad espacial de la preasignación — fase posterior

Las máscaras con desgaste y variaciones PBR continuas pueden producir pequeñas regiones de SurfaceID distintos o alternancias con Default. Antes de ampliar el algoritmo, evaluar perfiles de candidatos acotados y las reglas explícitas por material disponibles. La tolerancia de coincidencia no debe aumentarse únicamente para reducir rechazos, sin revisar la calidad de las asignaciones.

Evaluar las siguientes mejoras como opciones de conversión, no como cambios automáticos de las fuentes existentes:

- Candidatos y parámetros de clasificación por material de origen, manteniendo la prioridad de asignaciones explícitas y emisión.
- Filtrado de metallic y smoothness según el área cubierta por cada voxel antes de clasificar, respetando discontinuidades y bordes de islas UV.
- Coherencia espacial para pequeñas regiones de asignaciones poco fiables, sin atravesar límites de materiales, huecos o bordes geométricos, ni eliminar emisivos, detalles finos o decisiones artísticas explícitas.
- Diagnóstico por material de origen y comparación antes/después de cobertura, ambigüedad, rechazos y conservación de detalles. La procedencia necesaria debe capturarse durante la conversión; el informe agregado y los IDs del VOX no permiten recuperarla por sí solos.

Validar con modelos de varios materiales, paneles desgastados, señalética, piezas delgadas y LODs derivados. El proceso debe ser determinista, cancelable y respetar los presupuestos de memoria y el límite de pares semánticos. Esta fase trata la clasificación del acabado, no el suavizado de la geometría. Incluso con SurfaceID uniforme, los cambios de ColorID pueden impedir unir caras coplanares en el mesher y mantener una malla densa.

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
- Unir las celdas ocupadas dando prioridad a la primera fuente en el orden de la jerarquía. Los solapamientos con `ColorID` o `SurfaceID` diferentes requieren confirmación. Se combinan celdas opacas y de vidrio básico; las piezas `Keep Original` mantienen su representación separada.
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
- `UV3.x` contiene el `SurfaceID` crudo; `UV3.y` identifica opaco (0) o vidrio (1).
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

La reducción manual de un volumen semántico selecciona combinaciones existentes mediante el área de caras expuestas compatibles con los límites del voxel reducido; los interiores mantienen la mayoría por volumen y los empates utilizan el par de IDs menor. Respeta las cavidades ocultas y consulta vecinos entre chunks. No cambia la ocupación ni promete conservar todos los detalles subvoxel. Duplicar un LOD conserva la tabla de slots sin reinterpretarla. El alcance de detección de emisión y la conservación de piezas se describen en [MATERIALS.md](MATERIALS.md#superficies-durante-la-conversión) y [README.md](README.md#reglas-de-conversión).

La pintura visual de SurfaceID utiliza una herramienta acotada dentro de Unity; su contrato y etapas se describen en [Autoría visual de superficies en Unity](#autoría-visual-de-superficies-en-unity). La conversión utiliza una superficie emisiva de respaldo configurable, inicialmente Neon. La emisión con color independiente del albedo permanece pendiente. El vidrio voxel básico utiliza una asignación explícita y un shader transparente separado, sin refracción avanzada.

### Mesh de producción

La exportación independiente LOD0 opaca y de vidrio básico está disponible en `VOX to Unity`, con greedy meshing, IDs en UV0/UV3 y material HDRP compartido. El prefab mantiene un vínculo de autoría por GUID y permite abrir la fuente en MagicaVoxel y reconstruir explícitamente la misma malla. El lector localiza chunks por tamaño y posición después de un guardado externo, sin depender de sus índices internos ni descartar modelos adicionales ocupados. Su alcance y límites se describen en [MATERIALS.md](MATERIALS.md).

Las familias semánticas de producción preservan chunks para culling, consultan vecinos a través de sus fronteras y mantienen la alineación física entre niveles. El prefab permite reconstruir cada nivel, derivar los siguientes por duplicación o reducción y revisar avisos de fuentes modificadas sin sobrescribir descendientes. El alcance, los límites de volumen y las operaciones con confirmación se describen en [MATERIALS.md](MATERIALS.md#familias-semánticas-de-producción). La reimportación automática y la ampliación de los límites de meshing requieren validación adicional de memoria y recuperación ante fallos.

La conversión física con `Conversion Profile` genera directamente una familia de producción mediante el mesher semántico, sin otro prefab de previsualización. La colocación individual y por lotes utiliza ese resultado vinculado; los controles de edición, bindings y reconstrucción están disponibles por nivel. El greedy mesher combina caras únicamente cuando coinciden color, superficie, orientación y clase de render.

El resultado utiliza un submesh por comportamiento real de render, no por `SurfaceID`. Voxel Importer permanece disponible para previsualizar y editar `.vox`, pero no es la fuente definitiva del mesh de producción. Esta separación evita parches profundos al asset de terceros y proporciona un contrato estable para combinación, LODs y DOTS.

### Shader, HTrace e impostores

El shader HDRP compartido muestrea ambas LUT y aplica la emisión como `BaseColor x SurfaceEmission x EmissionIntensity`. La escala HDR permanece en el material y puede cambiar sin regenerar meshes.

La referencia emisiva normalizada de `Surface Painter` sirve únicamente para identificar colores durante la autoría. Las validaciones de iluminación y el horneado de impostores deben utilizar la intensidad HDR de producción, no la copia temporal de esa ventana.

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

## Geometría limpia y detalle superficial — prototipo visual pendiente

Priorizar como decisión de autoría zonas planas con ColorID y SurfaceID uniformes. Reservar la geometría para siluetas, huecos y relieves reales; evaluar manchas y variaciones de acabado en el shader. Alinear ese detalle a la unidad voxel mínima del perfil, con un origen coherente entre chunks y LODs y filtrado a distancia.

Separar anuncios e imágenes complejas de sus marcos, paredes y soportes: utilizar un quad con textura convencional para el gráfico y geometría voxel simplificada para la estructura. Mantener materiales compartidos cuando sea viable, sin imponer un material por cartel. Esta propuesta no convierte automáticamente modelos existentes ni modifica la identidad semántica del VOX.

El [plan de geometría y detalle](ART_DIRECTION_AND_DETAIL.md) define el alcance, los recursos editables y los criterios de comparación. Prototipar esta alternativa después de la validación de autoría y MagicaVoxel, antes de ampliar el mesher híbrido. No considerar demostrado un beneficio de rendimiento hasta medir el coste conjunto de geometría, shaders, texturas y draws.

## Normalización de escala en la conversión

La conversión nueva dispone de `Normalize Scale`: escala mundial incorporada antes de voxelizar, análisis de memoria coherente y colocación a escala unitaria. Las familias existentes no se migran automáticamente. La conversión individual permite excluir variantes inactivas; los personajes se capturan como mallas estáticas, sin exportación de animación. Consultar [escala y variantes de origen](README.md#escala-y-variantes-de-origen).

## Relieve voxel POM/SPOM — evaluación futura

Evaluar POM y recorte de silueta como acabado cercano opcional sobre la geometría voxel existente, no como sustituto del volumen, las colisiones o los LODs. Separar el relieve cercano de la lectura multiescala distante; respetar el contrato de anclaje mundial para arquitectura estática y local de familia para objetos dinámicos. Utilizar juntas estrechas, alturas pequeñas y regiones de descanso visual. El crecimiento del relieve POM no se deriva automáticamente del tamaño de la rejilla visual. La plataforma de referencia del juego utiliza rasterización, sin requerir ray tracing.

El asset candidato `Assets/Frostzone/CSPOM` incluye documentación, subgrafos separados de POM y silueta, un modo curvo opcional y un ejemplo plano. La elección entre reutilizar sus subgrafos, adaptarlos o desarrollar una implementación propia permanece abierta; la compatibilidad de producción y el rendimiento requieren pruebas.

Condiciones de integración:

- Preservar ColorID/SurfaceID, LUT y materiales compartidos. Proporcionar coordenadas de relieve independientes: UV0 contiene ColorID en las mallas semánticas, no UV convencionales.
- Configurar y verificar DOTS Instancing en Entities Graphics; el Shader Graph del candidato lo tiene desactivado.
- Distinguir reducción de pasos de muestreo de desvanecimiento de altura. Incorporar una salida económica a distancia, no sólo una reducción de calidad.
- Evaluar el recorte de silueta por separado: sus límites UV no deben abrir juntas entre caras o chunks.
- Comparar una pared opaca sin relieve, con normales, con POM y con SPOM. Comprobar ángulos rasantes, sombras rasterizadas, profundidad, estabilidad temporal y transiciones de LOD; medir tiempo GPU y memoria. Mantener vidrio y emisivos fuera del primer ensayo.

El ahorro geométrico por almacenar atributos en texturas pertenece al meshing híbrido; POM/SPOM añade coste de sombreado y no demuestra por sí mismo una optimización. Este ensayo no bloquea la preparación de la manzana piloto ni implica regenerar assets existentes.

## Meshing híbrido con texturas de IDs — fase posterior

Evaluar una representación de producción opcional que combine geometría voxel para siluetas, huecos y relieves con caras coplanares amplias cuyos atributos se almacenen en texturas de ColorID y SurfaceID. La selección debe considerar planitud, área y fragmentación por atributos, no únicamente el tamaño del modelo. Señalética, paredes, suelos y fachadas son casos de evaluación; las regiones uniformes pueden conservar el mesher actual.

El VOX y su sidecar permanecen como fuente editable. Las mallas y texturas son resultados derivados de una reconstrucción explícita; no se editan como una segunda fuente de verdad ni amplían el límite de pares del transporte. Las texturas deben consultar las paletas globales sin hornear iluminación. Esta representación conserva geometría 3D real y no constituye un impostor ni un sistema HLOD.

Criterios para un prototipo acotado:

- Preservar ocupación, pivote, escala, bordes, huecos y límites de chunks; mantener la granularidad de culling.
- Definir direccionamiento, bordes de atlas, filtrado y tratamiento de mipmaps sin interpolar IDs categóricos ni mezclar regiones vecinas.
- Comparar geometría, memoria de texturas, coste de muestreo y tiempo de reconstrucción contra el mesher actual. No sustituir automáticamente una representación cuando el coste total empeore.
- Validar paletas, emisión, LODs, uniones entre caras y chunks, materiales compartidos y Entities Graphics. Evitar materiales independientes por cara o instancia.
- Comprobar que la edición y reconstrucción regeneren los datos derivados de forma coherente, sin sobrescribir retoques de LODs descendientes.

Esta fase es independiente de la coherencia espacial de la clasificación PBR. Su evaluación pertenece al trabajo de optimización posterior a la estabilización del flujo de autoría y materiales.

## DOTS, subescenas y culling

`DOTS Stress Test` permite crear entidades progresivamente desde prefabs horneados, revisar recursos compartidos y comparar frustum con contadores agregados de Entities Graphics. El procedimiento se describe en [README.md](README.md#prueba-masiva-en-dots). No sustituye la validación visual, las capturas de Profiler o el análisis de residencia por zonas.

La validación DOTS debe realizarse primero con los impostores desactivados para obtener una referencia clara. Debe comprobar:

- conversión de los prefabs Voxel Bridge a entidades;
- representación de los `LODGroup` y sus transiciones;
- compatibilidad de materiales y sombras con Entities Graphics;
- bounds de chunks y modelos combinados;
- instanciación de prefabs repetidos;
- carga y descarga de subescenas;
- culling por cámara y las opciones de oclusión disponibles para la configuración final del proyecto.

El tamaño máximo recomendado para una familia combinada debe derivarse de estos resultados y de la partición espacial de las subescenas.

### Rendimiento diferido, HLOD y streaming

La comprobación funcional de materiales y descarte por frustum no certifica un presupuesto de producción. Retomar las mediciones con la misma cámara, resolución, iluminación y distribución, registrando tiempos CPU/GPU por frame, LOD activo, draws y recursos residentes. Separar el coste de geometría opaca, piezas transparentes, sombras, HTrace y APV; incluir un Player de la plataforma objetivo además del Editor.

La combinación manual actual produce un único asset y un `LODGroup`; no constituye un sistema HLOD. Un HLOD permitiría sustituir varios objetos por una representación conjunta distante y recuperar las representaciones originales al acercarse. Antes de implementarlo, definir propiedad de los grupos, límites de streaming, transiciones, exclusión mutua entre originales y sustituto, y tratamiento de piezas transparentes. Comparar el beneficio con LODs convencionales y agrupaciones compactas, sin combinar automáticamente escenas completas.

Priorizar la evaluación de LODs adicionales y HLOD basados en mallas simplificadas para reducir la dependencia de atlas. La disponibilidad de más niveles no obliga a utilizarlos en todos los assets: también pueden aumentar la memoria de mallas residente. Un HLOD de malla no depende del horneado de impostores. Medir originales, sustitutos y atlas cargados simultáneamente antes de afirmar un ahorro; los impostores permanecen como opción selectiva cuando su coste y calidad lo justifiquen.

El frustum descarta trabajo de render, pero no descarga entidades, meshes ni atlas. Medir por separado la carga/descarga de subescenas y su residencia. La oclusión requiere una evaluación independiente; un objeto detrás de otro no queda cubierto por la validación de frustum. La adaptación del horneado semántico precede a cualquier comparación de impostores o HLOD que los utilice.

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
