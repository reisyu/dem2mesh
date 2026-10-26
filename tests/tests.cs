// dem2mesh.cs のテスト
// 合成データのテストは常に動く。実データ（sample の国土地理院GML）のテストは、sample があるときだけ動き、無ければ SKIP にする
// 終了コード: 0 = すべて成功、1 = 失敗あり
// .NET Framework 4.x の csc（C# 5）でビルドするため、文字列補間や ?. などは使わない
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Generic;
using Rhino.Geometry;

public static class Tests {
  static int pass, fail, skip;

  static void Check(string name, bool ok) { Check(name, ok, null); }
  static void Check(string name, bool ok, string detail) {
    if (ok) pass++; else fail++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail == null ? "" : "  (" + detail + ")"));
  }
  static void Skip(string name, string why) { skip++; Console.WriteLine("SKIP " + name + "  (" + why + ")"); }
  static void Section(string title) { Console.WriteLine(); Console.WriteLine("== " + title + " =="); }

  static List<string> Lines(object[] a) { var l = a[6] as List<string>; return l ?? new List<string> { G.Info(a) }; }
  static string Line(object[] a, string key) { return Lines(a).FirstOrDefault(s => s.Contains(key)) ?? ""; }
  static string K(Point3d p) { return string.Format("{0:F3},{1:F3},{2:F2}", p.X, p.Y, p.Z); }
  // 面を「4頂点の座標」で表した一覧（頂点番号に依存しない比較用）
  static List<string> FaceKeys(Mesh m) {
    var keys = m.Faces.F.Select(f => string.Join("|", f.Select(i => K(m.Vertices.P[i])))).ToList();
    keys.Sort(StringComparer.Ordinal);
    return keys;
  }

  public static int Main(string[] args) {
    Console.OutputEncoding = Encoding.UTF8;
    string sample = args.Length > 0 && args[0] != "" ? args[0] : null;

    CellCenter();
    MergeTiles();
    Clipping();
    Errors();
    Zones();
    TypeSplit();
    SrsName();
    RealData(sample);

    Console.WriteLine();
    Console.WriteLine(string.Format("結果: PASS {0} / FAIL {1} / SKIP {2}", pass, fail, skip));
    return fail == 0 ? 0 : 1;
  }

  // 標高点はセルの中心にある（範囲の端ではない）。半セルずれの再発防止
  static void CellCenter() {
    Section("合成: 標高点の位置（セルの中心）");
    var a = G.Run(G.Tile(0, 0, 5, 4, 4), 1, Point3d.Unset, 0, 1);
    var P = ((Mesh)a[7]).Vertices.P;
    var s = new Script_Instance();
    var cj = typeof(Script_Instance).GetMethod("ComputeJGD", BindingFlags.NonPublic | BindingFlags.Instance);
    Func<double, double, Point3d> proj = (lat, lon) => {
      object[] p = { lat, lon, 36.0, 139.833333333333, 6, 0.0, 0.0 };
      cj.Invoke(s, p);
      return new Point3d((double)p[6], (double)p[5], 0);
    };
    double uLat = G.Lat0 + 4 * G.D, lLon = G.Lon0, lLat = G.Lat0, uLon = G.Lon0 + 5 * G.D;
    Point3d first = proj(uLat - G.D / 2, lLon + G.D / 2), last = proj(lLat + G.D / 2, uLon - G.D / 2);
    double d1 = Math.Max(Math.Abs(P[0].X - first.X), Math.Abs(P[0].Y - first.Y));
    double d2 = Math.Max(Math.Abs(P[19].X - last.X), Math.Abs(P[19].Y - last.Y));
    Check("北西の点が北西のセルの中心にある", d1 < 0.001, "ずれ " + d1.ToString("F4") + " m");
    Check("南東の点が南東のセルの中心にある", d2 < 0.001, "ずれ " + d2.ToString("F4") + " m");
    Check("北西の点の標高が先頭のデータ（100）", Math.Abs(P[0].Z - 100) < 1e-6 && Math.Abs(P[19].Z - (100 + 3 * 1000 + 4)) < 1e-6);
  }

  // 複数区画を1つの格子にまとめると、同じ範囲の1区画と同じ結果になる
  static void MergeTiles() {
    Section("合成: 複数区画の統合");
    string big = G.Tile(0, 0, 10, 8, 8);
    // 4区画に分け、順番もばらばらにする
    var parts = new List<string> { G.Tile(5, 4, 5, 4, 8), G.Tile(0, 0, 5, 4, 8), G.Tile(5, 0, 5, 4, 8), G.Tile(0, 4, 5, 4, 8) };
    foreach (int mode in new[] { 0, 1, 2 })
      foreach (int step in new[] { 1, 2, 3 })
        Check("2×2区画 = 1区画（mode " + mode + ", step " + step + "）", G.Dump(G.Run(parts, step, Point3d.Unset, 0, mode)) == G.Dump(G.Run(big, step, Point3d.Unset, 0, mode)));
    var a = G.Run(parts, 1, Point3d.Unset, 0, 1);
    Check("面の数にすき間がない（9×7 = 63）", ((Mesh)a[7]).Faces.Count == 63, ((Mesh)a[7]).Faces.Count + " 面");
    var b = G.Run(new List<string> { parts[0], parts[1], parts[2] }, 1, Point3d.Unset, 0, 1);
    Check("1区画欠けたL字（欠けた部分だけ面がない）", ((Mesh)b[7]).Faces.Count == 9 * 3 + 4 * 4, ((Mesh)b[7]).Faces.Count + " 面");
    // 先頭・末尾が省略された区画（startPoint）
    var c = G.Run(G.Tile(0, 0, 10, 8, 8, 7, 70), 1, Point3d.Unset, 0, 1);
    var P = ((Mesh)c[7]).Vertices.P;
    Check("先頭7点・末尾3点の省略が正しい位置に入る", !P[6].set && P[7].set && Math.Abs(P[7].Z - 107) < 1e-6 && P[76].set && !P[77].set);
  }

  static void Clipping() {
    Section("合成: クリッピング");
    string big = G.Tile(0, 0, 10, 8, 8);
    var parts = new List<string> { G.Tile(5, 4, 5, 4, 8), G.Tile(0, 0, 5, 4, 8), G.Tile(5, 0, 5, 4, 8), G.Tile(0, 4, 5, 4, 8) };
    var bm = (Mesh)G.Run(big, 1, Point3d.Unset, 0, 1)[7];
    var p = bm.Vertices.P[6 * 10 + 7];
    var c = G.Run(parts, 1, new Point3d(p.X, p.Y, 0), 8, 1);
    Check("範囲が1区画の中だけなら、その区画だけ読む", Line(c, "Tiles:").Contains("1 used / 4 loaded"), Line(c, "Tiles:").Trim());
    var q = bm.Vertices.P[4 * 10 + 5];
    var e = G.Run(parts, 1, new Point3d(q.X, q.Y, 0), 14, 1);
    var eBig = G.Run(big, 1, new Point3d(q.X, q.Y, 0), 14, 1);
    Check("区画の境目をまたぐ切り抜き = 1区画で切り抜いた結果", FaceKeys((Mesh)e[7]).SequenceEqual(FaceKeys((Mesh)eBig[7])) && ((Mesh)e[7]).Faces.Count > 0);
    Check("範囲にかかる区画がないとき", G.Info(G.Run(parts, 1, new Point3d(0, 0, 0), 10, 1)).Contains("クリッピング範囲にかかる区画がありません"));
  }

  static void Errors() {
    Section("合成: 入力の誤り");
    string t = G.Tile(0, 0, 5, 4, 8);
    Check("セルの大きさが違う区画", G.Info(G.Run(new List<string> { t, G.Tile(0, 0, 5, 4, 8, 0, -1, G.D * 2) }, 1, Point3d.Unset, 0, 1)).Contains("区画 2 のセルの大きさが区画 1 と違います"));
    G.Lat0 += G.D * 0.5; string shifted = G.Tile(0, 0, 5, 4, 8); G.Lat0 -= G.D * 0.5;
    Check("格子がずれた区画", G.Info(G.Run(new List<string> { t, shifted }, 1, Point3d.Unset, 0, 1)).Contains("区画 2 の格子が区画 1 の格子と揃っていません"));
    Check("要素が足りないGML", G.Info(G.Run(new List<string> { t, "<r xmlns:gml='http://www.opengis.net/gml/3.2'></r>" }, 1, Point3d.Unset, 0, 1)).Contains("区画 2: gml:lowerCorner が見つかりません"));
    Check("空の入力", G.Info(G.Run(new List<string> { "", null }, 1, Point3d.Unset, 0, 1)).StartsWith("Wait:"));
    Check("離れすぎた区画", G.Info(G.Run(new List<string> { t, G.Tile(80000, -27000, 5, 4, 8) }, 1, Point3d.Unset, 0, 1)).Contains("範囲が広すぎます"));
  }

  // 告示（平成14年国土交通省告示第9号）の原点が、各系で (0, 0) になる。11系・12系の原点緯度の誤りの再発防止
  static void Zones() {
    Section("合成: 平面直角座標系 1〜19系の原点");
    double[] oLat = { 33, 33, 36, 33, 36, 36, 36, 36, 36, 40, 44, 44, 44, 26, 26, 26, 26, 20, 26 };
    double[] oLon = { 129.5, 131, 132 + 10 / 60.0, 133.5, 134 + 20 / 60.0, 136, 137 + 10 / 60.0, 138.5, 139 + 50 / 60.0, 140 + 50 / 60.0,
                      140.25, 142.25, 144.25, 142, 127.5, 124, 131, 136, 154 };
    double lat0 = G.Lat0, lon0 = G.Lon0;
    for (int z = 1; z <= 19; z++) {
      // 原点が区画の (列2, 行2) のセルの中心に来るように区画を置く
      G.Lat0 = oLat[z - 1] - 2.5 * G.D; G.Lon0 = oLon[z - 1] - 2.5 * G.D;
      var a = G.Call(G.Tile(0, 0, 5, 5, 5), z, 1, Point3d.Unset, 0, 1, 0);
      var p = ((Mesh)a[7]).Vertices.P[2 * 5 + 2];
      double d = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
      Check(z + "系の原点が (0, 0)", d < 0.01, "ずれ " + d.ToString("F4") + " m");
    }
    G.Lat0 = lat0; G.Lon0 = lon0;
  }

  // 種別ごとのメッシュ（type_output 1, 2）
  static void TypeSplit() {
    Section("合成: 種別による分け方");
    var s = new Script_Instance();
    var ft = typeof(Script_Instance).GetMethod("FaceType", BindingFlags.NonPublic | BindingFlags.Instance);
    var tp = (string[])typeof(Script_Instance).GetField("TiePriority", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    var kt = (string[])typeof(Script_Instance).GetField("KnownTypes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    int[] rank = kt.Select(n => Array.IndexOf(tp, n)).ToArray();
    Func<int, int, int, int, string> F = (a, b, c, d) => kt[(int)ft.Invoke(s, new object[] { a, b, c, d, rank })];
    // KnownTypes の番号: 0 地表面, 1 表層面, 3 内水面
    Check("地表面3・内水面1 → 地表面（多数決）", F(0, 0, 0, 3) == "地表面");
    Check("地表面2・内水面2 → 内水面（同数なら地表面以外を優先）", F(0, 3, 0, 3) == "内水面" && F(3, 3, 0, 0) == "内水面");
    Check("地表面2・内水面1・表層面1 → 地表面", F(0, 0, 3, 1) == "地表面");

    // 斜めに流れる川（幅2セル）と、1セル幅の水路を持つ区画
    Func<int, int, string> kind = (c, r) => (c == r || c == r + 1 || c == 13) ? "内水面" : "地表面";
    string tile = G.Tile(0, 0, 16, 12, 12, 0, -1, G.D, null, kind);
    foreach (int step in new[] { 1, 3 }) {
      var a0 = G.Call(tile, 9, step, Point3d.Unset, 0, 1, 0);
      var a1 = G.Call(tile, 9, step, Point3d.Unset, 0, 1, 1);
      var a2 = G.Call(tile, 9, step, Point3d.Unset, 0, 1, 2);
      var single = FaceKeys((Mesh)a0[7]);
      var parts = (List<Mesh>)a1[8];
      var union = parts.SelectMany(m => FaceKeys(m)).ToList(); union.Sort(StringComparer.Ordinal);
      string tag = "（step " + step + "）";
      Check("種別ごとのメッシュを合わせると1枚の面と一致（すき間も重なりもない）" + tag, single.SequenceEqual(union));
      Check("結合した1枚が1枚の面と一致" + tag, single.SequenceEqual(FaceKeys((Mesh)a2[7])));
      Check("type_output 1 では output_mesh が空" + tag, a1[7] == null);
      Check("頂点ごとに色が付く" + tag, parts.All(m => m.VertexColors.C.Count == m.Vertices.Count) && ((Mesh)a2[7]).VertexColors.C.Count == ((Mesh)a2[7]).Vertices.Count);
    }
    var names = (List<string>)G.Call(tile, 9, 1, Point3d.Unset, 0, 1, 1)[9];
    Check("1セル幅の水路も内水面として残る", names.Contains("内水面") && names.Contains("地表面"), string.Join(", ", names));
  }

  static void SrsName() {
    Section("合成: srsName の表示と警告");
    string a24 = G.Tile(0, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2024.bl");
    string b24 = G.Tile(5, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2024.bl");
    string b11 = G.Tile(5, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2011.bl");
    var same = G.Run(new List<string> { a24, b24 }, 1, Point3d.Unset, 0, 1);
    Check("表記がそろっていれば警告なし", !Lines(same)[0].StartsWith("Warning") && Line(same, "[2]").Contains("fguuid:jgd2024.bl"));
    var mixed = G.Run(new List<string> { a24, b11 }, 1, Point3d.Unset, 0, 1);
    Check("JGD2011 と JGD2024 が混ざると先頭に警告", Lines(mixed)[0].StartsWith("Warning: srsName の違う区画が混ざっています") && Lines(mixed)[0].Contains("fguuid:jgd2011.bl: 区画 2"));
    Check("警告が出てもメッシュは作られる", mixed[7] is Mesh && ((Mesh)mixed[7]).Faces.Count == 9 * 3);
    var p = ((Mesh)G.Run(a24, 1, Point3d.Unset, 0, 1)[7]).Vertices.P[1 * 5 + 2];
    var clipped = G.Run(new List<string> { a24, b11 }, 1, new Point3d(p.X, p.Y, 0), 6, 1);
    Check("クリッピングで外れた区画は警告の対象外", !Lines(clipped)[0].StartsWith("Warning") && Line(clipped, "[2]").Contains("(skipped)"));
    Check("srsName のないGMLは「(srsName なし)」と表示", Line(G.Run(G.Tile(0, 0, 5, 4, 4), 1, Point3d.Unset, 0, 1), "[1]").Contains("(srsName なし)"));
  }

  // sample フォルダの国土地理院GML（2次メッシュ 513253, DEM5A）を使うテスト
  static void RealData(string dir) {
    Section("実データ（sample）");
    if (dir == null || !Directory.Exists(dir)) {
      Skip("実データのテストすべて", "sample が見つかりません。README「サンプルデータの入手」を参照");
      return;
    }
    Console.WriteLine("sample: " + dir);
    Func<string, string> F = code => {
      var f = Directory.GetFiles(dir, "FG-GML-5132-53-" + code + "-DEM5A-*.xml").OrderBy(x => x).LastOrDefault();
      return f == null ? null : File.ReadAllText(f);
    };
    string t06 = F("06"), t07 = F("07"), t16 = F("16"), t17 = F("17"), t52 = F("52"), t53 = F("53"), t62 = F("62"), t63 = F("63");

    if (t62 == null || t63 == null) Skip("東西の境目（62・63）", "区画のファイルがありません");
    else {
      var a = G.Call(new List<string> { t62, t63 }, 0, 1, Point3d.Unset, 0, 1, 0);
      var P = ((Mesh)a[7]).Vertices.P; int nX = 450, nY = 150;
      Check("62+63: 系の自動判定は3系（広島市付近）", Line(a, "JGD Zone").Contains("3 (Auto Detected)"));
      Check("62+63: 450×150点で無効な点がない", P.Count == nX * nY && P.All(v => v.set));
      Check("62+63: 面の数 = 449×149（すき間なし）", ((Mesh)a[7]).Faces.Count == (nX - 1) * (nY - 1));
      var p62 = ((Mesh)G.Call(t62, 0, 1, Point3d.Unset, 0, 1, 0)[7]).Vertices.P;
      var p63 = ((Mesh)G.Call(t63, 0, 1, Point3d.Unset, 0, 1, 0)[7]).Vertices.P;
      double maxd = 0;
      for (int r = 0; r < nY; r++) for (int c = 0; c < 225; c++) {
        Point3d m1 = P[r * nX + c], s1 = p62[r * 225 + c], m2 = P[r * nX + 225 + c], s2 = p63[r * 225 + c];
        maxd = Math.Max(maxd, Math.Max(Math.Abs(m1.X - s1.X) + Math.Abs(m1.Y - s1.Y) + Math.Abs(m1.Z - s1.Z), Math.Abs(m2.X - s2.X) + Math.Abs(m2.Y - s2.Y) + Math.Abs(m2.Z - s2.Z)));
      }
      Check("62+63: まとめても1区画ずつ読んだ点と同じ位置", maxd < 1e-6, "最大差 " + maxd.ToString("E1") + " m");
      double inner = 0, seam = 0;
      for (int r = 0; r < nY; r++) { inner += P[r * nX + 224].X - P[r * nX + 223].X; seam += P[r * nX + 225].X - P[r * nX + 224].X; }
      Check("62+63: 境目の点の間隔 = 区画の中の間隔", Math.Abs(inner - seam) / nY < 0.001, "境目 " + (seam / nY).ToString("F3") + " m / 内側 " + (inner / nY).ToString("F3") + " m");
    }

    if (t52 == null || t53 == null || t62 == null || t63 == null) Skip("南北の境目（52・53・62・63）", "区画のファイルがありません");
    else {
      var b = G.Call(new List<string> { t53, t62, t52, t63 }, 0, 1, Point3d.Unset, 0, 1, 0);
      var Q = ((Mesh)b[7]).Vertices.P; int qX = 450;
      int seamFaces = 0;
      for (int c = 0; c < qX - 1; c++) if (Q[149 * qX + c].set && Q[149 * qX + c + 1].set && Q[150 * qX + c].set && Q[150 * qX + c + 1].set) seamFaces++;
      Check("52+53+62+63（順不同）: 南北の境目に面が張られる", seamFaces == qX - 1, seamFaces + " / " + (qX - 1));
    }

    if (t06 == null || t07 == null || t16 == null || t17 == null) Skip("種別による分け方（06・07・16・17）", "区画のファイルがありません");
    else {
      var files = new List<string> { t06, t07, t16, t17 };
      var a0 = G.Call(files, 3, 1, Point3d.Unset, 0, 1, 0);
      var a1 = G.Call(files, 3, 1, Point3d.Unset, 0, 1, 1);
      var a2 = G.Call(files, 3, 1, Point3d.Unset, 0, 1, 2);
      var single = FaceKeys((Mesh)a0[7]);
      var parts = (List<Mesh>)a1[8]; var names = (List<string>)a1[9];
      var union = parts.SelectMany(m => FaceKeys(m)).ToList(); union.Sort(StringComparer.Ordinal);
      Check("06+07+16+17: 種別ごとのメッシュを合わせると1枚の面と一致", single.SequenceEqual(union));
      Check("06+07+16+17: 結合した1枚が1枚の面と一致", single.SequenceEqual(FaceKeys((Mesh)a2[7])));
      Check("06+07+16+17: 地表面と内水面に分かれる", names.Contains("地表面") && names.Contains("内水面"),
            string.Join(", ", names.Select((n, i) => n + " " + parts[i].Faces.Count + " 面")));
    }

    if (t62 == null || t63 == null) Skip("srsName（62・63）", "区画のファイルがありません");
    else {
      var ok = G.Call(new List<string> { t62, t63 }, 0, 1, Point3d.Unset, 0, 1, 0);
      Check("実データの srsName が表示され、そろっていれば警告なし", !Lines(ok)[0].StartsWith("Warning") && Line(ok, "[1]").Contains("fguuid:"), Line(ok, "[1]").Trim());
      string old63 = t63.Replace("fguuid:jgd2024.bl", "fguuid:jgd2011.bl");
      if (old63 == t63) Skip("JGD2011 の区画が混ざったときの警告（実データ）", "sample の srsName が jgd2024 ではありません");
      else Check("JGD2011 と書き換えた区画が混ざると警告", Lines(G.Call(new List<string> { t62, old63 }, 0, 1, Point3d.Unset, 0, 1, 0))[0].StartsWith("Warning"));
    }

    var all = Directory.GetFiles(dir, "*.xml").OrderBy(x => x).Select(File.ReadAllText).ToList();
    var sw = Stopwatch.StartNew();
    var z = G.Call(all, 0, 4, Point3d.Unset, 0, 1, 2);
    Check("sample の全区画（" + all.Count + " 区画、step 4）を読み込める", z[7] is Mesh && !G.Info(z).StartsWith("Error"), sw.ElapsedMilliseconds + " ms, " + Line(z, "Output Mesh").Trim());
  }
}
