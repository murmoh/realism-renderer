using System.Numerics;

namespace Realism.Rendering;

/// <summary>
/// A Minecraft model's skeleton at rest: every part's rest transform (relative to its parent) and boxes. Gives
/// part-to-model matrices and lets anatomy be designed in a friendly "design space" and put back onto the parts.
///
/// Design space: blocks, origin on the ground under the model, X sideways, Y up, Z forward (toward the face).
/// Model space (what part matrices map into before Minecraft's entity transform): blocks, y down, 1.5 at the ground,
/// face toward -z.
/// </summary>
public sealed class MobRig
{
    public sealed record Part(string Name, int Parent, Matrix4x4 Local, List<ModelCuboid> Boxes)
    {
        public Matrix4x4 ToModel;     // part local -> model space (rest pose)
        public Matrix4x4 FromDesign;  // design space -> part local
        public Vector3 Pivot;         // the joint, in design space
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue); // its boxes, in design space
        public bool HasBoxes => Min.X <= Max.X;
        public Vector3 Centre => (Min + Max) / 2;
        public Vector3 Size => Max - Min;
    }

    public readonly List<Part> Parts = new();

    /// <summary>Design space -> model space.</summary>
    public static readonly Matrix4x4 DesignToModel = new(
        1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 1.5f, 0, 1);

    public static Matrix4x4 RestLocal(float x, float y, float z, float pitch, float yaw, float roll, float sx, float sy, float sz) =>
        // As ModelPart.applyTransform: translate to the pivot, rotate Z*Y*X (X applied first), then scale.
        Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw)
        * Matrix4x4.CreateRotationZ(roll) * Matrix4x4.CreateTranslation(x / 16f, y / 16f, z / 16f);

    public void Add(string name, int parent, Matrix4x4 local, List<ModelCuboid> boxes) => Parts.Add(new Part(name, parent, local, boxes));

    /// <summary>After all parts are added: rest chains, inverses, design-space pivots and bounds.</summary>
    public void Finish()
    {
        Matrix4x4.Invert(DesignToModel, out var modelToDesign);
        foreach (var p in Parts)
        {
            p.ToModel = p.Parent >= 0 ? p.Local * Parts[p.Parent].ToModel : p.Local;
            Matrix4x4.Invert(p.ToModel, out var inv);
            p.FromDesign = DesignToModel * inv;
            p.Pivot = Vector3.Transform(Vector3.Zero, p.ToModel * modelToDesign);
            foreach (var b in p.Boxes)
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) != 0 ? b.Max.X : b.Min.X, (c & 2) != 0 ? b.Max.Y : b.Min.Y, (c & 4) != 0 ? b.Max.Z : b.Min.Z);
                    var d = Vector3.Transform(corner, p.ToModel * modelToDesign);
                    p.Min = Vector3.Min(p.Min, d);
                    p.Max = Vector3.Max(p.Max, d);
                }
        }
    }

    public Part? Find(string name) => Parts.FirstOrDefault(p => p.Name == name);

    public override string ToString() => string.Join("\n", Parts.Select((p, i) =>
        $"  [{i}] {p.Name} parent {p.Parent} pivot ({p.Pivot.X:0.00},{p.Pivot.Y:0.00},{p.Pivot.Z:0.00})"
        + (p.HasBoxes ? $" box ({p.Min.X:0.00},{p.Min.Y:0.00},{p.Min.Z:0.00})..({p.Max.X:0.00},{p.Max.Y:0.00},{p.Max.Z:0.00})" : "")));
}
