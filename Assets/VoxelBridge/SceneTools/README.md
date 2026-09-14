# Inspección en primera persona

`FirstPersonSceneController` permite recorrer una escena desde una altura humana para evaluar escala, LODs y materiales. Utiliza `CharacterController` y el Input System; es una herramienta local de inspección, no el controlador ECS o multijugador del juego.

## Configuración

1. Crear un GameObject con escala mundial `(1, 1, 1)`, situado ligeramente sobre un collider del suelo. Su origen representa los pies.
2. Añadir `CharacterController`: altura `1,8`, radio `0,25`, centro `(0, 0,9, 0)`, Skin Width `0,02`, Step Offset `0,3` y Min Move Distance `0`.
3. Añadir `FirstPersonSceneController` y asignar una cámara hija en `View Camera`. Desactivar otros controladores de esa cámara, incluido `FreeCamera`, y mantener una sola cámara y un solo AudioListener activos para esta vista.
4. Asignar en `Input Actions` un `InputActionAsset` con el mapa `Player`: `Move` y `Look` de tipo Vector2, `Sprint` y `Jump` de tipo Button. El proyecto incluye `Assets/InputSystem_Actions.inputactions`. El componente utiliza una copia temporal; no modifica ni habilita el asset compartido.

Requiere el paquete `com.unity.inputsystem` y el nuevo Input System habilitado en Player Settings. La altura de ojos predeterminada es `1,65 m`, el FOV vertical `65°`, la velocidad al caminar `3 m/s` y al correr `6 m/s`. Los campos del componente permiten ajustar estos valores. `Mouse Sensitivity` expresa grados por píxel; `Stick Look Speed`, grados por segundo.

## Uso

Entrar en Play y hacer clic izquierdo dentro de la vista Game para capturar el ratón.

| Control | Acción |
|---|---|
| WASD | Caminar |
| Ratón | Mirar |
| Shift izquierdo | Correr |
| Espacio | Saltar |
| Esc | Liberar el ratón y detener el movimiento horizontal |
| R | Volver a la posición y orientación de inicio |

WASD, Shift y Espacio corresponden a los bindings del asset del proyecto. El clic de captura, Esc y R son atajos propios del componente. Perder el foco o desactivar el componente libera la captura; otro clic permite continuar. La gravedad permanece activa sin captura. Una caída de más de 30 metros bajo el punto inicial devuelve automáticamente al inicio.

`Test2` contiene el rig en `_Setup/FirstPerson_Inspection`, con la cámara HDRP existente. Utiliza `BaseCollider` como suelo. Los edificios sin collider son atravesables; el relieve del shader tampoco genera colisiones. No se añaden colliders a los modelos convertidos ni se modifican sus prefabs.

## Pruebas

La suite EditMode `FirstPersonSceneControllerTests` comprueba movimiento, salto, reinicio, mirada y aislamiento del asset de entrada. Crea una escena aditiva temporal con un suelo de prueba a 1000 metros de altura y la cierra al terminar. La respuesta física del teclado, la captura del cursor y la presentación final requieren una comprobación adicional en Play.
