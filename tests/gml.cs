// テスト用の合成GMLと、RunScript の呼び出し
using System;
using System.Text;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using Rhino.Geometry;

public static class G {
  public const double D = 0.2 / 3600; // DEM5A のセルの大きさ（0.2秒）
  // 全体格子の南西の角。9系の区域の中（東京駅付近）に置く（テストは9系を指定するので、表で引いた系と違うと Warning が出るため）
  public static double Lat0 = 35.68, Lon0 = 139.76;

  // 全体格子の (gc0, gr0) を北西の角として nx×ny を切り出した区画を作る
  // 標高は全体格子の位置で決まる（100 + 行*1000 + 列）ので、区画を分けても1枚で作っても同じ値になる
  // start / count で先頭・末尾の省略（startPoint）を、srs で srsName を、kind で点ごとの種別を指定できる
  public static string Tile(int gc0, int gr0, int nx, int ny, int totalRows, int start = 0, int count = -1, double d = D, string srs = null, Func<int, int, string> kind = null) {
    double uLat = Lat0 + (totalRows - gr0) * d, lLat = uLat - ny * d, lLon = Lon0 + gc0 * d, uLon = lLon + nx * d;
    var sb = new StringBuilder("\n");
    int n = count < 0 ? nx * ny - start : count;
    for (int k = start; k < start + n; k++) {
      int c = k % nx, r = k / nx;
      string name = kind == null ? "地表面" : kind(gc0 + c, gr0 + r);
      sb.Append(name + "," + (100 + (gr0 + r) * 1000 + (gc0 + c)) + ".\n");
    }
    // 実データと同じく、四隅は小数8桁に丸め、小数点は「.」で書く
    var inv = CultureInfo.InvariantCulture;
    string corners = "<gml:lowerCorner>" + lLat.ToString("F8", inv) + " " + lLon.ToString("F8", inv) + "</gml:lowerCorner>"
                   + "<gml:upperCorner>" + uLat.ToString("F8", inv) + " " + uLon.ToString("F8", inv) + "</gml:upperCorner>";
    if (srs != null) corners = "<gml:Envelope srsName='" + srs + "'>" + corners + "</gml:Envelope>";
    return "<r xmlns:gml='http://www.opengis.net/gml/3.2'>" + corners
         + "<gml:high>" + (nx - 1) + " " + (ny - 1) + "</gml:high>"
         + "<gml:tupleList>" + sb + "</gml:tupleList>"
         + "<gml:startPoint>" + (start % nx) + " " + (start / nx) + "</gml:startPoint></r>";
  }

  // RunScript を呼ぶ。戻り値は {0-5: 入力, 6: info, 7: output_mesh, 8: type_meshes, 9: type_names}
  public static object[] Call(object xml, int zone, int step, Point3d tp, double size, int typeOut) {
    var s = new Script_Instance();
    var m = typeof(Script_Instance).GetMethod("RunScript", BindingFlags.NonPublic | BindingFlags.Instance);
    object arg = xml is string ? new List<string> { (string)xml } : xml;
    object[] b = { arg, zone, step, tp, size, typeOut, null, null, null, null };
    m.Invoke(s, b);
    return new object[] { b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9] };
  }
  public static object[] Run(object xml, int step, Point3d tp, double size) { return Call(xml, 9, step, tp, size, 0); }

  public static string Info(object[] a) {
    if (a[6] is string) return (string)a[6];
    var l = a[6] as List<string>;
    return l == null ? "" : string.Join("\n", l);
  }

  // メッシュを文字列にする（頂点の座標と面の数）。2つの結果が同じかどうかの比較に使う
  public static string Dump(object[] a) {
    var m = a[7] as Mesh;
    if (m == null) return "NO MESH: " + Info(a);
    var sb = new StringBuilder("faces=" + m.Faces.Count + "\n");
    foreach (var p in m.Vertices.P) sb.Append(p.set ? string.Format("{0:F4} {1:F4} {2:F2}\n", p.X, p.Y, p.Z) : "unset\n");
    return sb.ToString();
  }
}
