# Voxel Bridge para Unity

Este repositorio contiene las herramientas de Editor Voxel Bridge y el prototipo
Dynamic GI archivado. El entorno local de pruebas Polygon, las escenas, los
paquetes importados, `Library`, los exports voxel y los datos de iluminación
generados quedan excluidos del control de versiones.

## Voxel Bridge

Copiar `Assets/VoxelBridge` y `Assets/VoxelBridge.meta` para reutilizar el flujo de
conversión de mallas a MagicaVoxel. Los parches opcionales de integración se
distribuyen como transformaciones de código; los paquetes comerciales se instalan
por separado. Consultar [la guía de Voxel Bridge](Assets/VoxelBridge/README.md) para
uso y mantenimiento.

## Prototipo Dynamic GI archivado

[Archives/DynamicGI.zip](Archives/DynamicGI.zip) contiene el módulo completo,
incluidos scripts, shaders, documentación y archivos `.meta`. El ZIP conserva las
rutas `Assets/DynamicGI` y `Assets/DynamicGI.meta` y permanece fuera de los assets
importados por Unity.

Para recuperar el prototipo, extraer el ZIP en la raíz de un proyecto Unity.
La implementación utiliza Unity 6000.3.21f1 con HDRP 17.3.0. Consultar el archivo
`Assets/DynamicGI/README.md` incluido para instalación, arquitectura, validación y
limitaciones.
