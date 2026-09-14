# Voxel Bridge para Unity

Herramientas de Editor para convertir mallas de Unity a familias voxel con escala
física, materiales semánticos y niveles de detalle. El repositorio contiene sólo
el código reutilizable, sus shaders, pruebas y documentación técnica. Las escenas,
modelos importados, paquetes comerciales, exports voxel y datos generados se
mantienen fuera del control de versiones.

## Requisitos

- Unity `6000.3` o compatible.
- HDRP `17.3` y Shader Graph `17.3`.
- Input System `1.20`.
- Entities y Entities Graphics `1.4` para los diagnósticos DOTS opcionales.
- MagicaVoxel y Voxel Importer para el flujo de conversión.
- Amplify Impostors para la generación opcional de impostores.

Los paquetes comerciales y las herramientas externas se instalan por separado y
no se redistribuyen en este repositorio.

## Instalación

Copiar `Assets/VoxelBridge` y `Assets/VoxelBridge.meta` para reutilizar el flujo de
conversión de mallas a MagicaVoxel. Los parches opcionales de integración se
distribuyen como transformaciones de código; los paquetes comerciales se instalan
por separado. Consultar [la guía de Voxel Bridge](Assets/VoxelBridge/README.md) para
uso y mantenimiento.

La configuración de paquetes del proyecto anfitrión debe incluir las versiones
compatibles indicadas arriba. El repositorio no contiene `Packages/manifest.json`
porque Voxel Bridge se instala dentro de un proyecto Unity existente.

## Validación

Con el Editor cerrado, ejecutar las pruebas EditMode desde la raíz del proyecto:

```powershell
unity test . --mode EditMode --output Logs/voxel-bridge-tests.xml --timeout 600
```

Algunas pruebas de integración requieren los paquetes comerciales instalados.
