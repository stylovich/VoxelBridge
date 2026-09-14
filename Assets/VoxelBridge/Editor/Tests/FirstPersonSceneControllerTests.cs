using LocalModels.VoxelBridge.SceneTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class FirstPersonSceneControllerTests
    {
        private static readonly Vector3 FixtureOrigin = new Vector3(0, 1000, 0);
        private Scene previous, temporary;
        private InputActionAsset actions;
        private FirstPersonSceneController controller;
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            previous = SceneManager.GetActiveScene();
            // Keep the user's scene loaded; put the fixture above the inspection city.
            temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var ground = new GameObject("Test Ground").AddComponent<BoxCollider>();
            ground.transform.position = FixtureOrigin;
            ground.size = new Vector3(30, 1, 30); ground.center = new Vector3(0, -.5f, 0);
            var root = new GameObject("Test First Person"); root.transform.position = FixtureOrigin + new Vector3(0, .04f, 0);
            var motor = root.AddComponent<CharacterController>();
            motor.height = 1.8f; motor.center = new Vector3(0, .9f, 0); motor.radius = .25f;
            motor.skinWidth = .02f; motor.stepOffset = .3f; motor.minMoveDistance = 0;
            var child = new GameObject("Test Camera"); child.transform.SetParent(root.transform, false);
            camera = child.AddComponent<Camera>(); camera.enabled = false;
            actions = ScriptableObject.CreateInstance<InputActionAsset>();
            var map = new InputActionMap("Player");
            map.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
            map.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
            map.AddAction("Sprint", InputActionType.Button); map.AddAction("Jump", InputActionType.Button);
            actions.AddActionMap(map);
            controller = root.AddComponent<FirstPersonSceneController>();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("inputActions").objectReferenceValue = actions;
            serialized.FindProperty("viewCamera").objectReferenceValue = camera;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(controller.Initialize(out var error), Is.True, error);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (temporary.IsValid()) EditorSceneManager.CloseScene(temporary, true);
            if (actions) Object.DestroyImmediate(actions);
        }

        [Test]
        public void DiagonalMovementIsNormalizedAndFollowsYaw()
        {
            Assert.That(FirstPersonSceneController.HorizontalVelocity(Vector2.one, 0, 3).magnitude, Is.EqualTo(3).Within(.0001f));
            Assert.That(Vector3.Distance(FirstPersonSceneController.HorizontalVelocity(Vector2.up, 90, 3), Vector3.right * 3), Is.LessThan(.0001f));
        }

        [Test]
        public void PointerDeltaIsNotMultipliedByFrameTime()
        {
            var a = FirstPersonSceneController.LookDelta(new Vector2(10, 20), true, .08f, 120, 1f / 30);
            var b = FirstPersonSceneController.LookDelta(new Vector2(10, 20), true, .08f, 120, 1f / 120);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(FirstPersonSceneController.LookDelta(Vector2.one, false, .08f, 120, .5f), Is.EqualTo(Vector2.one * 60));
        }

        [Test]
        public void GroundedWalkingAndSprintUseExpectedSpeeds()
        {
            Settle();
            Assert.That(controller.GetComponent<CharacterController>().isGrounded, Is.True);
            Vector3 start = controller.transform.position;
            for (int i = 0; i < 60; i++) controller.Step(Vector2.up, Vector2.zero, true, false, false, 1f / 60);
            Assert.That(controller.transform.position.z - start.z, Is.EqualTo(3).Within(.02f));
            Assert.That(controller.transform.position.y, Is.EqualTo(start.y).Within(.03f));
            start = controller.transform.position;
            for (int i = 0; i < 60; i++) controller.Step(Vector2.up, Vector2.zero, true, true, false, 1f / 60);
            Assert.That(controller.transform.position.z - start.z, Is.EqualTo(6).Within(.02f));
        }

        [Test]
        public void JumpLandsAndResetRestoresSpawn()
        {
            Settle(); float ground = controller.transform.position.y;
            controller.Step(Vector2.zero, Vector2.zero, true, false, true, 1f / 60);
            float maximum = controller.transform.position.y;
            for (int i = 0; i < 100; i++)
            {
                controller.Step(Vector2.zero, Vector2.zero, true, false, false, 1f / 60);
                maximum = Mathf.Max(maximum, controller.transform.position.y);
            }
            Assert.That(maximum - ground, Is.InRange(.6f, .85f));
            Assert.That(controller.transform.position.y, Is.EqualTo(ground).Within(.03f));
            controller.Step(Vector2.up, new Vector2(300, 400), true, false, false, .1f);
            controller.ResetPosition();
            Assert.That(Vector3.Distance(controller.transform.position, FixtureOrigin + new Vector3(0, .04f, 0)), Is.LessThan(.001f));
            Assert.That(camera.transform.localRotation, Is.EqualTo(Quaternion.identity));
        }

        [Test]
        public void PitchIsClampedAndEyeHeightIsHumanScale()
        {
            controller.Step(Vector2.zero, new Vector2(0, 100000), true, false, false, .016f);
            Assert.That(Mathf.DeltaAngle(0, camera.transform.localEulerAngles.x), Is.EqualTo(-85).Within(.001f));
            Assert.That(camera.transform.localPosition.y, Is.EqualTo(1.65f).Within(.001f));
            Assert.That(camera.fieldOfView, Is.EqualTo(65));
        }

        [Test]
        public void DisablingDoesNotChangeSharedInputAsset()
        {
            string before = actions.ToJson();
            Assert.That(actions.FindActionMap("Player").enabled, Is.False);
            controller.enabled = false;
            Assert.That(actions.ToJson(), Is.EqualTo(before));
            controller.enabled = true;
            Assert.That(controller.Initialize(out var error), Is.True, error);
            Assert.That(actions.FindActionMap("Player").enabled, Is.False);
        }

        private void Settle()
        {
            for (int i = 0; i < 60; i++) controller.Step(Vector2.zero, Vector2.zero, true, false, false, 1f / 60);
        }
    }
}
