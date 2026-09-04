# Herramientas Dynamic GI y Voxel Bridge para Unity

Este repositorio contiene el código portable del prototipo Dynamic GI y las
herramientas de Editor Voxel Bridge. El entorno local de pruebas Polygon, las
escenas, los paquetes importados, `Library`, los exports voxel y los datos de
iluminación generados quedan excluidos del control de versiones.

## Módulos

Copiar `Assets/DynamicGI` y `Assets/DynamicGI.meta` a otro proyecto para trasladar el
prototipo. La implementación utiliza Unity 6000.3.21f1 con HDRP 17.3.0. Consultar
[la guía de Dynamic GI](Assets/DynamicGI/README.md) para instalación, arquitectura,
validación y limitaciones.

Copiar `Assets/VoxelBridge` y `Assets/VoxelBridge.meta` para reutilizar el flujo de
conversión de mallas a MagicaVoxel. Los parches opcionales de integración se
distribuyen como transformaciones de código; los paquetes comerciales se instalan
por separado. Consultar [la guía de Voxel Bridge](Assets/VoxelBridge/README.md) para
uso y mantenimiento.

Los comandos de Editor específicos de Test3 sirven como ejemplos. El Geometry Field,
el Sky Visibility por tiles, el Radiance Field local de seis direcciones, los
contributors, el Radiance Clipmap centrado en cámara, la propagación difusa acotada,
los compute shaders, las APIs de muestreo y proveedores, la integración opcional con
materiales HDRP y los renderers de depuración no dependen de Polygon ni de los datos
de horneado APV.
