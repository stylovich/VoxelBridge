using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: InternalsVisibleTo("VoxelBridge.Editor.Tests")]

namespace LocalModels.VoxelBridge.SceneTools
{
    /// <summary>Local inspection controller; not an ECS or networked player.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonSceneController : MonoBehaviour
    {
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private Camera viewCamera;
        [SerializeField, Min(.1f)] private float walkSpeed = 3f;
        [SerializeField, Min(.1f)] private float sprintSpeed = 6f;
        [SerializeField, Min(.1f)] private float eyeHeight = 1.65f;
        [SerializeField, Range(35, 95)] private float fieldOfView = 65f;
        [SerializeField, Min(.001f)] private float mouseSensitivity = .08f;
        [SerializeField, Min(1)] private float stickLookSpeed = 120f;
        [SerializeField, Min(.1f)] private float gravity = 20f;
        [SerializeField, Min(0)] private float jumpHeight = .75f;

        private CharacterController motor;
        private InputActionAsset instanceActions;
        private InputAction move, look, sprint, jump;
        private Vector3 spawnPosition;
        private float spawnYaw, spawnPitch, yaw, pitch, verticalSpeed;
        private bool hasSpawn, ownsCursor, skipLook;
        private CursorLockMode previousCursorLock;
        private bool previousCursorVisible;

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (!Initialize(out string error))
            {
                Debug.LogError(error, this);
                enabled = false;
            }
        }

        internal bool Initialize(out string error)
        {
            error = null;
            if (instanceActions) return true;
            motor = GetComponent<CharacterController>();
            if (!viewCamera || viewCamera.transform == transform || !viewCamera.transform.IsChildOf(transform))
                error = "First person inspection requires an assigned child Camera.";
            else if (!inputActions)
                error = "Assign an InputActionAsset containing Player/Move, Look, Sprint and Jump.";
            else if ((transform.lossyScale - Vector3.one).sqrMagnitude > .000001f)
                error = "The first person rig requires unit world scale.";
            else if (eyeHeight >= motor.height || eyeHeight <= 0)
                error = "Eye Height must be positive and lower than the CharacterController height.";
            if (error != null) return false;
            foreach (string name in new[] { "Move", "Look", "Sprint", "Jump" })
                if (inputActions.FindAction("Player/" + name) == null)
                { error = "The input asset is missing Player/" + name + "."; return false; }

            // Own a copy: never enable/disable or modify the project's shared action asset.
            instanceActions = Instantiate(inputActions);
            instanceActions.hideFlags = HideFlags.HideAndDontSave;
            move = instanceActions.FindAction("Player/Move");
            look = instanceActions.FindAction("Player/Look");
            sprint = instanceActions.FindAction("Player/Sprint");
            jump = instanceActions.FindAction("Player/Jump");
            instanceActions.FindActionMap("Player").Enable();
            yaw = transform.eulerAngles.y;
            pitch = Mathf.Clamp(Mathf.DeltaAngle(0, viewCamera.transform.localEulerAngles.x), -85, 85);
            if (!hasSpawn)
            {
                spawnPosition = transform.position; spawnYaw = yaw; spawnPitch = pitch; hasSpawn = true;
            }
            verticalSpeed = 0;
            ApplyCameraSettings();
            return true;
        }

        private void Update()
        {
            if (!instanceActions) return;
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) ReleaseCursor();
            else if (!ownsCursor && Application.isFocused && Mouse.current?.leftButton.wasPressedThisFrame == true)
                CaptureCursor();
            // The Editor can release the lock independently (Escape or Game view focus).
            if (ownsCursor && Cursor.lockState != CursorLockMode.Locked) ReleaseCursor();
            bool controlling = ownsCursor && Application.isFocused && Cursor.lockState == CursorLockMode.Locked;
            if (controlling && Keyboard.current?.rKey.wasPressedThisFrame == true) ResetPosition();
            Vector2 lookInput = controlling && !skipLook ? look.ReadValue<Vector2>() : Vector2.zero;
            skipLook = false;
            Step(controlling ? move.ReadValue<Vector2>() : Vector2.zero, lookInput,
                look.activeControl?.device is Pointer, controlling && sprint.IsPressed(),
                controlling && jump.WasPressedThisFrame(), Time.deltaTime);
            if (transform.position.y < spawnPosition.y - 30f) ResetPosition();
        }

        internal void Step(Vector2 movement, Vector2 lookInput, bool pointerLook, bool running, bool jumping, float dt)
        {
            if (!motor || !motor.enabled || dt <= 0) return;
            dt = Mathf.Min(dt, .1f);
            Vector2 degrees = LookDelta(lookInput, pointerLook, mouseSensitivity, stickLookSpeed, dt);
            yaw = Mathf.Repeat(yaw + degrees.x, 360);
            pitch = Mathf.Clamp(pitch - degrees.y, -85, 85);
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            if (motor.isGrounded && verticalSpeed < 0) verticalSpeed = -2f;
            if (jumping && motor.isGrounded) verticalSpeed = Mathf.Sqrt(2f * gravity * jumpHeight);
            verticalSpeed = Mathf.Max(verticalSpeed - gravity * dt, -45f);
            Vector3 velocity = HorizontalVelocity(movement, yaw, running ? sprintSpeed : walkSpeed);
            velocity.y = verticalSpeed;
            CollisionFlags collisions = motor.Move(velocity * dt);
            if ((collisions & CollisionFlags.Above) != 0 && verticalSpeed > 0) verticalSpeed = 0;
            if ((collisions & CollisionFlags.Below) != 0 && verticalSpeed < 0) verticalSpeed = -2f;
        }

        internal static Vector3 HorizontalVelocity(Vector2 input, float yaw, float speed)
        {
            Vector2 movement = Vector2.ClampMagnitude(input, 1);
            return Quaternion.Euler(0, yaw, 0) * new Vector3(movement.x, 0, movement.y) * speed;
        }

        // Pointer input is already a per-frame displacement; sticks express a rate.
        internal static Vector2 LookDelta(Vector2 input, bool pointer, float sensitivity, float stickRate, float dt) =>
            input * (pointer ? sensitivity : stickRate * dt);

        public void ResetPosition()
        {
            if (!hasSpawn || !motor) return;
            bool wasEnabled = motor.enabled;
            motor.enabled = false;
            transform.SetPositionAndRotation(spawnPosition, Quaternion.Euler(0, spawnYaw, 0));
            motor.enabled = wasEnabled;
            yaw = spawnYaw; pitch = spawnPitch; verticalSpeed = 0;
            viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            skipLook = true;
        }

        private void CaptureCursor()
        {
            previousCursorLock = Cursor.lockState; previousCursorVisible = Cursor.visible;
            ownsCursor = true; skipLook = true;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        private void ReleaseCursor()
        {
            if (!ownsCursor) return;
            if (Cursor.lockState == CursorLockMode.Locked)
            { Cursor.lockState = previousCursorLock; Cursor.visible = previousCursorVisible; }
            ownsCursor = false;
        }

        private void OnApplicationFocus(bool focused) { if (!focused) ReleaseCursor(); }

        private void OnDisable()
        {
            ReleaseCursor();
            if (!instanceActions) return;
            instanceActions.Disable();
            if (Application.isPlaying) Destroy(instanceActions); else DestroyImmediate(instanceActions);
            instanceActions = null;
            move = look = sprint = jump = null;
        }

        private void ApplyCameraSettings()
        {
            viewCamera.transform.localPosition = new Vector3(0, eyeHeight, 0);
            viewCamera.fieldOfView = fieldOfView;
        }

        private void OnValidate()
        {
            walkSpeed = Mathf.Max(.1f, walkSpeed); sprintSpeed = Mathf.Max(walkSpeed, sprintSpeed);
            gravity = Mathf.Max(.1f, gravity); jumpHeight = Mathf.Max(0, jumpHeight);
            fieldOfView = Mathf.Clamp(fieldOfView, 35, 95);
            mouseSensitivity = Mathf.Max(.001f, mouseSensitivity); stickLookSpeed = Mathf.Max(1, stickLookSpeed);
            if (Application.isPlaying && viewCamera) ApplyCameraSettings();
        }
    }
}
