using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelScaleNormalization
    {
        // Express source-local geometry in metres, retaining the source's world rotation separately.
        internal static Vector3 SourceScale(Object source)
        {
            if (!(source is GameObject go)) return Vector3.one;
            Decompose(go.transform, out Vector3 scale, out _);
            return scale;
        }

        private static void Decompose(Transform t, out Vector3 scale, out Quaternion rotation)
        {
            Matrix4x4 actual = t.localToWorldMatrix;
            Vector3 x = actual.GetColumn(0), y = actual.GetColumn(1), z = actual.GetColumn(2);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (!float.IsFinite(scale.x + scale.y + scale.z) ||
                scale.x <= .000001f || scale.y <= .000001f || scale.z <= .000001f)
                throw new InvalidOperationException("Normalize Scale requires finite, non-zero world scale on every axis.");
            x /= scale.x; y /= scale.y; z /= scale.z;
            if (Mathf.Abs(Vector3.Dot(x, y)) > .0001f || Mathf.Abs(Vector3.Dot(x, z)) > .0001f ||
                Mathf.Abs(Vector3.Dot(y, z)) > .0001f)
                throw new InvalidOperationException("Normalize Scale cannot preserve a sheared hierarchy. Apply the parent transforms or convert from an unscaled parent.");
            // A reflection is baked on X in the family frame; the proper rotation is used for placement.
            // Decomposing the matrix also handles rotated children of reflected parents, for which
            // Transform.rotation and signed lossyScale need not reconstruct the actual transform.
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) scale.x = -scale.x;
            rotation = Quaternion.LookRotation(z, y);
            Matrix4x4 expected = Matrix4x4.TRS(t.position, rotation, scale);
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
                if (Mathf.Abs(actual[row, column] - expected[row, column]) > .0001f * Mathf.Abs(scale[column]))
                    throw new InvalidOperationException("Normalize Scale cannot preserve a sheared hierarchy. Apply the parent transforms or convert from an unscaled parent.");
        }

        internal static Transform UnscaledParent(Transform candidate)
        {
            for (var t = candidate; t != null; t = t.parent)
            {
                Matrix4x4 a = t.localToWorldMatrix;
                Matrix4x4 b = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                bool unit = true;
                for (int c = 0; c < 3; c++)
                for (int r = 0; r < 3; r++) unit &= Mathf.Abs(a[r, c] - b[r, c]) < .00001f;
                if (unit) return t;
            }
            return null;
        }

        internal static void Place(Transform source, Transform destination, VoxelSourceAxes axes)
        {
            Decompose(source, out _, out Quaternion rotation);
            destination.SetPositionAndRotation(source.position, axes == VoxelSourceAxes.ZUp
                ? rotation * Quaternion.Euler(90, 0, 0) : rotation);
            destination.localScale = Vector3.one;
        }
    }
}
