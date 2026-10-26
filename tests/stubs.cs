// dem2mesh.cs を Rhino なしでコンパイルするための、最小限の仮の型
// 本物の RhinoCommon とは動きが違う点に注意（下のコメント参照）。Rhino 上での確認の代わりにはならない
using System.Collections.Generic;

public class GH_ScriptInstance {}

// .NET Framework 4.x には Math.Atanh がない。run_tests.ps1 がテスト用のコピーだけ MathX.Atanh に置き換える
public static class MathX { public static double Atanh(double x) { return 0.5 * System.Math.Log((1 + x) / (1 - x)); } }

namespace Rhino {
  public enum UnitSystem { None, Millimeters, Centimeters, Meters, Kilometers }
  // ActiveDoc は既定で null（文書なし → メートルのまま）。単位のテストだけが差し替える
  public class RhinoDoc {
    public static RhinoDoc ActiveDoc { get; set; }
    public UnitSystem ModelUnitSystem { get; set; }
  }
  public static class RhinoMath {
    static double PerMeter(UnitSystem u) {
      switch (u) { case UnitSystem.Millimeters: return 1000; case UnitSystem.Centimeters: return 100; case UnitSystem.Kilometers: return 0.001; default: return 1; }
    }
    // from の 1 単位が to で何単位か
    public static double UnitScale(UnitSystem from, UnitSystem to) { return PerMeter(to) / PerMeter(from); }
  }
}

namespace Rhino.Geometry {
  // set = false が Point3d.Unset。無効な頂点（標高なし）も、この値のまま頂点リストに残る
  public struct Point3d {
    public double X, Y, Z; public bool set;
    public Point3d(double x, double y, double z) { X = x; Y = y; Z = z; set = true; }
    public static Point3d Unset { get { return new Point3d(); } }
    public static bool operator ==(Point3d a, Point3d b) { return a.set == b.set && a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
    public static bool operator !=(Point3d a, Point3d b) { return !(a == b); }
    public override bool Equals(object o) { return o is Point3d && this == (Point3d)o; }
    public override int GetHashCode() { return 0; }
  }
  // CullUnused は何もしない。そのため、頂点の番号が「行 * 列数 + 列」のまま残り、テストで位置を直接引ける
  public class VList {
    public List<Point3d> P = new List<Point3d>();
    public void AddVertices(Point3d[] v) { P.AddRange(v); }
    public int Add(Point3d v) { P.Add(v); return P.Count - 1; }
    public int Count { get { return P.Count; } }
    public void CullUnused() {}
  }
  public class FList {
    public List<int[]> F = new List<int[]>();
    public int Count { get { return F.Count; } }
    public void AddFace(int a, int b, int c, int d) { F.Add(new[] { a, b, c, d }); }
  }
  public class NList { public void ComputeNormals() {} }
  public class CList {
    public List<System.Drawing.Color> C = new List<System.Drawing.Color>(); public Mesh owner;
    public bool CreateMonotoneMesh(System.Drawing.Color c) { C.Clear(); for (int i = 0; i < owner.Vertices.Count; i++) C.Add(c); return true; }
  }
  // 本物の Rhino は、同じ座標の頂点をつながっているものとして扱う（開いた辺の判定など）。この仮の型にはその機能はない
  public class Mesh {
    public VList Vertices = new VList(); public FList Faces = new FList(); public NList Normals = new NList(); public CList VertexColors;
    public Mesh() { VertexColors = new CList { owner = this }; }
    public void Append(Mesh o) {
      int off = Vertices.Count;
      Vertices.P.AddRange(o.Vertices.P);
      VertexColors.C.AddRange(o.VertexColors.C);
      foreach (var f in o.Faces.F) Faces.F.Add(new[] { f[0] + off, f[1] + off, f[2] + off, f[3] + off });
    }
  }
}
