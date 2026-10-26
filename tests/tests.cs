// dem2mesh.cs のテスト
// 合成データのテストは常に動く。実データ（sample の国土地理院GML）のテストは、sample があるときだけ動き、無ければ SKIP にする
// 終了コード: 0 = すべて成功、1 = 失敗あり
// .NET Framework 4.x の csc（C# 5）でビルドするため、文字列補間や ?. などは使わない
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Generic;
using Rhino;
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
  static string K(Point3d p) { return string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F2}", p.X, p.Y, p.Z); }
  // 面を「4頂点の座標」で表した一覧（頂点番号に依存しない比較用）
  static List<string> FaceKeys(Mesh m) {
    var keys = m.Faces.F.Select(f => string.Join("|", f.Select(i => K(m.Vertices.P[i])))).ToList();
    keys.Sort(StringComparer.Ordinal);
    return keys;
  }

  // dem2mesh.cs の中の Projection（座標変換）を作って呼ぶ
  static Func<double, double, Point3d> Projector(double phi0, double lam0) {
    var type = typeof(Script_Instance).GetNestedType("Projection", BindingFlags.NonPublic);
    object proj = Activator.CreateInstance(type, new object[] { phi0, lam0 });
    var compute = type.GetMethod("Compute");
    return (lat, lon) => {
      object[] p = { lat, lon, 0.0, 0.0 };
      compute.Invoke(proj, p);
      return new Point3d((double)p[3], (double)p[2], 0); // Rhino と同じく 東 = X、北 = Y
    };
  }

  public static int Main(string[] args) {
    Console.OutputEncoding = Encoding.UTF8;
    string sample = args.Length > 0 && args[0] != "" ? args[0] : null;

    ProjectionAccuracy();
    CellCenter();
    MergeTiles();
    Clipping();
    FilePaths();
    Errors();
    Zones();
    ZoneDetection();
    Dms();
    Culture();
    Units();
    TypeSplit();
    SrsName();
    RealData(sample);

    Console.WriteLine();
    Console.WriteLine(string.Format("結果: PASS {0} / FAIL {1} / SKIP {2}", pass, fail, skip));
    return fail == 0 ? 0 : 1;
  }

  // 国土地理院 測量計算サイトの「緯度経度 → 平面直角座標」（bl2xy、refFrame=2）の結果と照合する
  // 国土地理院の値は 0.1 mm 単位なので、差が 0.2 mm 以内なら一致とみなす
  static void ProjectionAccuracy() {
    Section("合成: 座標変換と国土地理院の計算値");
    double[] oLat = { 33, 33, 36, 33, 36, 36, 36, 36, 36, 40, 44, 44, 44, 26, 26, 26, 26, 20, 26 };
    double[] oLon = { 129.5, 131, 132 + 10 / 60.0, 133.5, 134 + 20 / 60.0, 136, 137 + 10 / 60.0, 138.5, 139 + 50 / 60.0, 140 + 50 / 60.0,
                      140.25, 142.25, 144.25, 142, 127.5, 124, 131, 136, 154 };
    // { 系, 緯度, 経度, 国土地理院の X（北）, Y（東） }
    double[][] gsi = {
      new[] { 3,  34.4708333333, 132.40625, -169610.2556,  22008.8113 },
      new[] { 9,  35.73,         140.85,     -29478.7323,  91970.2099 },
      new[] { 12, 43.06,         141.35,    -104033.3164, -73308.3018 },
      new[] { 13, 43.33,         145.58,     -73574.2880, 107857.0969 },
      new[] { 16, 24.47,         123.0,     -169104.5470, -101372.0871 } };
    foreach (var g in gsi) {
      int z = (int)g[0];
      var p = Projector(oLat[z - 1], oLon[z - 1])(g[1], g[2]);
      double d = Math.Max(Math.Abs(p.Y - g[3]), Math.Abs(p.X - g[4]));
      Check(z + "系 北緯" + g[1].ToString("F2") + "° 東経" + g[2].ToString("F2") + "° が国土地理院の値と一致", d < 0.0002, "差 " + (d * 1000).ToString("F3") + " mm");
    }
  }

  // 標高点はセルの中心にある（範囲の端ではない）。半セルずれの再発防止
  static void CellCenter() {
    Section("合成: 標高点の位置（セルの中心）");
    var a = G.Run(G.Tile(0, 0, 5, 4, 4), 1, Point3d.Unset, 0);
    var P = ((Mesh)a[7]).Vertices.P;
    var proj = Projector(36.0, 139.833333333333);
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
    foreach (int step in new[] { 1, 2, 3 })
      Check("2×2区画 = 1区画（step " + step + "）", G.Dump(G.Run(parts, step, Point3d.Unset, 0)) == G.Dump(G.Run(big, step, Point3d.Unset, 0)));
    var a = G.Run(parts, 1, Point3d.Unset, 0);
    Check("面の数にすき間がない（9×7 = 63）", ((Mesh)a[7]).Faces.Count == 63, ((Mesh)a[7]).Faces.Count + " 面");
    var b = G.Run(new List<string> { parts[0], parts[1], parts[2] }, 1, Point3d.Unset, 0);
    Check("1区画欠けたL字（欠けた部分だけ面がない）", ((Mesh)b[7]).Faces.Count == 9 * 3 + 4 * 4, ((Mesh)b[7]).Faces.Count + " 面");
    // 10m（セル 0.4秒）と1m（セル 0.04秒）でも、統合とセルの中心の位置が正しい（実データでは未確認）
    var proj = Projector(36.0, 139.833333333333);
    foreach (var res in new[] { new { name = "10m", d = 0.4 / 3600 }, new { name = "1m", d = 0.04 / 3600 } }) {
      string bigD = G.Tile(0, 0, 10, 8, 8, 0, -1, res.d);
      var partsD = new List<string> { G.Tile(5, 4, 5, 4, 8, 0, -1, res.d), G.Tile(0, 0, 5, 4, 8, 0, -1, res.d), G.Tile(5, 0, 5, 4, 8, 0, -1, res.d), G.Tile(0, 4, 5, 4, 8, 0, -1, res.d) };
      var md = G.Run(partsD, 1, Point3d.Unset, 0);
      var first = proj(G.Lat0 + 8 * res.d - res.d / 2, G.Lon0 + res.d / 2);
      var p0 = ((Mesh)md[7]).Vertices.P[0];
      double dd = Math.Max(Math.Abs(p0.X - first.X), Math.Abs(p0.Y - first.Y));
      Check(res.name + "（セル " + (res.d * 3600).ToString("0.##", CultureInfo.InvariantCulture) + "秒）: 2×2区画 = 1区画で、北西の点がセルの中心",
            G.Dump(md) == G.Dump(G.Run(bigD, 1, Point3d.Unset, 0)) && ((Mesh)md[7]).Faces.Count == 63 && dd < 0.001, "ずれ " + dd.ToString("F4") + " m");
    }

    // 先頭・末尾が省略された区画（startPoint）
    var c = G.Run(G.Tile(0, 0, 10, 8, 8, 7, 70), 1, Point3d.Unset, 0);
    var P = ((Mesh)c[7]).Vertices.P;
    Check("先頭7点・末尾3点の省略が正しい位置に入る", !P[6].set && P[7].set && Math.Abs(P[7].Z - 107) < 1e-6 && P[76].set && !P[77].set);
  }

  static void Clipping() {
    Section("合成: クリッピング");
    string big = G.Tile(0, 0, 10, 8, 8);
    var parts = new List<string> { G.Tile(5, 4, 5, 4, 8), G.Tile(0, 0, 5, 4, 8), G.Tile(5, 0, 5, 4, 8), G.Tile(0, 4, 5, 4, 8) };
    var bm = (Mesh)G.Run(big, 1, Point3d.Unset, 0)[7];
    var p = bm.Vertices.P[6 * 10 + 7];
    var c = G.Run(parts, 1, new Point3d(p.X, p.Y, 0), 8);
    Check("範囲が1区画の中だけなら、その区画だけ読む", Line(c, "Tiles:").Contains("1 used / 4 loaded"), Line(c, "Tiles:").Trim());
    var q = bm.Vertices.P[4 * 10 + 5];
    var e = G.Run(parts, 1, new Point3d(q.X, q.Y, 0), 14);
    var eBig = G.Run(big, 1, new Point3d(q.X, q.Y, 0), 14);
    Check("区画の境目をまたぐ切り抜き = 1区画で切り抜いた結果", FaceKeys((Mesh)e[7]).SequenceEqual(FaceKeys((Mesh)eBig[7])) && ((Mesh)e[7]).Faces.Count > 0);
    Check("範囲にかかる区画がないとき", G.Info(G.Run(parts, 1, new Point3d(0, 0, 0), 10)).Contains("クリッピング範囲にかかる区画がありません"));
  }

  // xml_data にファイルのパスを渡す（Read File を通さない）
  static void FilePaths() {
    Section("合成: ファイルのパスで渡す");
    string dir = Path.Combine(Path.GetTempPath(), "dem2mesh_tests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var parts = new List<string> { G.Tile(5, 4, 5, 4, 8), G.Tile(0, 0, 5, 4, 8), G.Tile(5, 0, 5, 4, 8), G.Tile(0, 4, 5, 4, 8) };
      var paths = new List<string>();
      for (int i = 0; i < parts.Count; i++) {
        string p = Path.Combine(dir, "tile" + i + ".xml");
        if (i == 1) File.WriteAllText(p, parts[i], new UTF8Encoding(true));   // BOM 付き
        else File.WriteAllText(p, parts[i], new UTF8Encoding(false));
        paths.Add(p);
      }
      string fromContent = G.Dump(G.Run(parts, 1, Point3d.Unset, 0));
      Check("パスで渡しても中身で渡したのと同じメッシュ（BOM 付きの UTF-8 を含む）", G.Dump(G.Run(paths, 1, Point3d.Unset, 0)) == fromContent);
      var mixed = new List<string> { paths[0], parts[1], "  \"" + paths[2] + "\"  ", parts[3] };
      Check("パスと中身を混ぜても、引用符付きのパスでも同じメッシュ", G.Dump(G.Run(mixed, 1, Point3d.Unset, 0)) == fromContent);

      // 古いGMLのように、XML 宣言が Shift_JIS のファイル
      string sjisPath = Path.Combine(dir, "sjis.xml");
      File.WriteAllText(sjisPath, "<?xml version=\"1.0\" encoding=\"Shift_JIS\"?>" + parts[1], Encoding.GetEncoding(932));
      var sj = G.Call(new List<string> { sjisPath }, 9, 1, Point3d.Unset, 0, 1);
      var sjNames = sj[9] as List<string>;
      Check("XML 宣言が Shift_JIS のファイルも読める（種別「地表面」も文字化けしない）", sjNames != null && sjNames.Count == 1 && sjNames[0] == "地表面", G.Info(sj).Split('\n')[0]);

      string missing = Path.Combine(dir, "nothing.xml");
      var err = G.Run(new List<string> { paths[0], missing }, 1, Point3d.Unset, 0);
      Check("存在しないパスは、どの区画か分かるエラー", G.Info(err).StartsWith("Error: 区画 2: ファイルが見つかりません（GMLの中身でもありません）: ") && G.Info(err).Contains("nothing.xml"), G.Info(err));
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  static void Errors() {
    Section("合成: 入力の誤り");
    string t = G.Tile(0, 0, 5, 4, 8);
    var z20 = G.Call(t, 20, 1, Point3d.Unset, 0, 0);
    Check("系の番号が20以上ならエラー（範囲外の配列エラーにしない）", G.Info(z20).Contains("jgd_zone は 0（自動判定）か、1〜19 の系の番号で指定してください（指定された値: 20）") && z20[7] == null, G.Info(z20));
    Check("系の番号が19ならメッシュを作る", G.Call(t, 19, 1, Point3d.Unset, 0, 0)[7] is Mesh);
    Check("セルの大きさが違う区画", G.Info(G.Run(new List<string> { t, G.Tile(0, 0, 5, 4, 8, 0, -1, G.D * 2) }, 1, Point3d.Unset, 0)).Contains("区画 2 のセルの大きさが区画 1 と違います"));
    G.Lat0 += G.D * 0.5; string shifted = G.Tile(0, 0, 5, 4, 8); G.Lat0 -= G.D * 0.5;
    Check("格子がずれた区画", G.Info(G.Run(new List<string> { t, shifted }, 1, Point3d.Unset, 0)).Contains("区画 2 の格子が区画 1 の格子と揃っていません"));
    Check("要素が足りないGML", G.Info(G.Run(new List<string> { t, "<r xmlns:gml='http://www.opengis.net/gml/3.2'></r>" }, 1, Point3d.Unset, 0)).Contains("区画 2: gml:lowerCorner が見つかりません"));
    Check("空の入力", G.Info(G.Run(new List<string> { "", null }, 1, Point3d.Unset, 0)).StartsWith("Wait:"));
    Check("離れすぎた区画", G.Info(G.Run(new List<string> { t, G.Tile(80000, -27000, 5, 4, 8) }, 1, Point3d.Unset, 0)).Contains("範囲が広すぎます"));
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
      var a = G.Call(G.Tile(0, 0, 5, 5, 5), z, 1, Point3d.Unset, 0, 0);
      var p = ((Mesh)a[7]).Vertices.P[2 * 5 + 2];
      double d = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
      Check(z + "系の原点が (0, 0)", d < 0.01, "ずれ " + d.ToString("F4") + " m");
    }
    G.Lat0 = lat0; G.Lon0 = lon0;
  }

  // 系の自動判定（地域メッシュの表）。正解は告示（平成14年国土交通省告示第9号）の適用区域
  static void ZoneDetection() {
    Section("合成: 系の自動判定（地域メッシュの表）");
    var flags = BindingFlags.NonPublic | BindingFlags.Static;
    object[] t = { null, null };
    typeof(Script_Instance).GetMethod("LoadZoneTable", flags).Invoke(null, t);
    var cells = (System.Collections.IDictionary)t[0]; var cands = (System.Collections.IDictionary)t[1];
    Check("埋め込んだ表が展開できる（あいまいなメッシュ 3359）", cands.Count == 3359 && cells.Count > 4571, "要素 " + cells.Count + "、あいまい " + cands.Count);

    var zf = typeof(Script_Instance).GetMethod("ZoneFromLatLon", flags);
    Func<double, double, object[]> Z = (lat, lon) => { object[] a = { lat, lon, null, null }; a[0] = zf.Invoke(null, a); return a; }; // {系, -, 決め方, 候補}

    // 都道府県庁所在地などの代表点（緯度, 経度, 正しい系）。以前の経度だけの判定では21地点が外れていた
    object[][] pts = {
      new object[] { "札幌市", 43.06, 141.35, 12 }, new object[] { "旭川市", 43.77, 142.37, 12 }, new object[] { "小樽市", 43.19, 141.00, 11 },
      new object[] { "函館市", 41.77, 140.73, 11 }, new object[] { "帯広市", 42.92, 143.20, 13 }, new object[] { "釧路市", 42.98, 144.38, 13 },
      new object[] { "青森市", 40.82, 140.74, 10 }, new object[] { "秋田市", 39.72, 140.10, 10 }, new object[] { "盛岡市", 39.70, 141.15, 10 },
      new object[] { "仙台市", 38.27, 140.87, 10 }, new object[] { "山形市", 38.24, 140.36, 10 }, new object[] { "福島市", 37.75, 140.47, 9 },
      new object[] { "水戸市", 36.34, 140.45, 9 }, new object[] { "東京都庁", 35.69, 139.69, 9 }, new object[] { "新潟市", 37.90, 139.02, 8 },
      new object[] { "長野市", 36.65, 138.18, 8 }, new object[] { "静岡市", 34.98, 138.38, 8 }, new object[] { "名古屋市", 35.18, 136.91, 7 },
      new object[] { "金沢市", 36.59, 136.63, 7 }, new object[] { "福井市", 36.06, 136.22, 6 }, new object[] { "津市", 34.73, 136.51, 6 },
      new object[] { "大阪市", 34.69, 135.50, 6 }, new object[] { "神戸市", 34.69, 135.18, 5 }, new object[] { "鳥取市", 35.50, 134.24, 5 },
      new object[] { "岡山市", 34.66, 133.93, 5 }, new object[] { "倉敷市", 34.59, 133.77, 5 }, new object[] { "松江市", 35.47, 133.05, 3 },
      new object[] { "広島市", 34.40, 132.46, 3 }, new object[] { "福山市", 34.49, 133.36, 3 }, new object[] { "山口市", 34.19, 131.47, 3 },
      new object[] { "高松市", 34.34, 134.04, 4 }, new object[] { "徳島市", 34.07, 134.56, 4 }, new object[] { "松山市", 33.84, 132.77, 4 },
      new object[] { "高知市", 33.56, 133.53, 4 }, new object[] { "福岡市", 33.61, 130.42, 2 }, new object[] { "大分市", 33.24, 131.61, 2 },
      new object[] { "長崎市", 32.74, 129.87, 1 }, new object[] { "対馬市", 34.20, 129.29, 1 }, new object[] { "鹿児島市", 31.56, 130.56, 2 },
      new object[] { "奄美市", 28.38, 129.49, 1 }, new object[] { "与論町", 27.05, 128.42, 1 }, new object[] { "那覇市", 26.21, 127.68, 15 },
      new object[] { "石垣市", 24.34, 124.16, 16 }, new object[] { "南大東村", 25.83, 131.23, 17 }, new object[] { "父島", 27.09, 142.19, 14 },
      new object[] { "沖ノ鳥島", 20.43, 136.08, 18 }, new object[] { "南鳥島", 24.29, 153.98, 19 } };
    var wrong = new List<string>();
    foreach (var p in pts) { var r = Z((double)p[1], (double)p[2]); if ((int)r[0] != (int)p[3]) wrong.Add(p[0] + "（正 " + p[3] + "、判定 " + r[0] + "）"); }
    Check("代表47地点がすべて告示どおりの系", wrong.Count == 0, wrong.Count == 0 ? "47/47" : string.Join(", ", wrong));

    // 広島県と岡山県の境（3系と5系）のメッシュ
    var tie = Z(34.5708333, 133.43125);   // 51336384: 候補 3/5、周りも 2:2
    Check("境界のメッシュで周りも同数なら、番号の小さい系（3系）", (int)tie[0] == 3 && tie[2].ToString() == "BoundaryTie" && (string)tie[3] == "3/5", tie[0] + " " + tie[2] + " " + tie[3]);
    var maj = Z(34.4791667, 133.44375);   // 51335375: 候補 3/5、周りは 3系が多い
    Check("境界のメッシュは周りの多数決（3系）", (int)maj[0] == 3 && maj[2].ToString() == "Boundary", maj[0] + " " + maj[2] + " " + maj[3]);
    var sea = Z(33.0, 138.0);             // 太平洋
    Check("表に無い地点（海上）は経度で代用", sea[2].ToString() == "NotInTable" && (int)sea[0] == 8, sea[0] + " " + sea[2]);

    // RunScript で jgd_zone = 0（自動判定）
    double lat0 = G.Lat0, lon0 = G.Lon0;
    try {
      G.Lat0 = 34.40; G.Lon0 = 132.46;   // 広島市
      var ok = G.Call(G.Tile(0, 0, 5, 4, 4), 0, 1, Point3d.Unset, 0, 0);
      Check("区画の系が表で決まれば警告なし", Line(ok, "JGD Zone").Contains("3 (Auto Detected: mesh table, 1/1 tiles)") && !Lines(ok)[0].StartsWith("Warning") && Line(ok, "[1]").Contains("zone 3 (table)"), Line(ok, "JGD Zone").Trim());
      G.Lat0 = 34.5708333 - 2 * G.D; G.Lon0 = 133.43125 - 2.5 * G.D;   // 同数の境界のメッシュの中
      var tieRun = G.Call(G.Tile(0, 0, 5, 4, 4), 0, 1, Point3d.Unset, 0, 0);
      Check("同数の境界の区画は Warning を出して小さい番号の系", Line(tieRun, "JGD Zone").Contains("3 (Auto") && Lines(tieRun)[0].StartsWith("Warning: 区画 1 は系の境界にあり、候補（3/5）が周りのメッシュでも同数"), Lines(tieRun)[0]);
      G.Lat0 = 34.4541667 - 2 * G.D; G.Lon0 = 133.45 - 5 * G.D;           // 東経133.45°の西が3系、東が5系
      var split = G.Call(new List<string> { G.Tile(0, 0, 5, 4, 4), G.Tile(5, 0, 5, 4, 4) }, 0, 1, Point3d.Unset, 0, 0);
      Check("区画で系が分かれると Warning（同数なら小さい番号の3系）", Line(split, "JGD Zone").Contains("3 (Auto Detected: mesh table, 1/2 tiles)")
            && Lines(split).Any(l => l.StartsWith("Warning: 区画によって系が違います（3系: 区画 1 / 5系: 区画 2）")) && Line(split, "[2]").Contains("zone 5"), Line(split, "JGD Zone").Trim());
      G.Lat0 = 33.0; G.Lon0 = 138.0;                                       // 海上
      var seaRun = G.Call(G.Tile(0, 0, 5, 4, 4), 0, 1, Point3d.Unset, 0, 0);
      Check("表に無い区画は Warning を出して経度で代用", Lines(seaRun)[0].StartsWith("Warning: 区画 1 は地域メッシュの表に無い"), Lines(seaRun)[0]);
      var manual = G.Call(G.Tile(0, 0, 5, 4, 4), 8, 1, Point3d.Unset, 0, 0);
      Check("系を指定したら、その系で作る（表と同じなら警告なし）", Line(manual, "JGD Zone").Contains("8 (Manual)") && !Lines(manual)[0].StartsWith("Warning") && !Line(manual, "[1]").Contains("zone"), Lines(manual)[0]);
      G.Lat0 = 34.40; G.Lon0 = 132.46;   // 広島市（3系）
      var wrongZone = G.Call(G.Tile(0, 0, 5, 4, 4), 9, 1, Point3d.Unset, 0, 0);
      Check("指定した系が表と違えば Warning（メッシュは指定どおり9系で作る）", Line(wrongZone, "JGD Zone").Contains("9 (Manual)") && wrongZone[7] is Mesh
            && Lines(wrongZone)[0].StartsWith("Warning: 指定された 9系は、地域メッシュの表で引いた系（3系、1/1 tiles）と違います"), Lines(wrongZone)[0]);
      var rightZone = G.Call(G.Tile(0, 0, 5, 4, 4), 3, 1, Point3d.Unset, 0, 0);
      Check("指定した系が表と同じなら警告なし", !Lines(rightZone)[0].StartsWith("Warning"), Lines(rightZone)[0]);
    } finally {
      G.Lat0 = lat0; G.Lon0 = lon0;
    }
  }

  // info の度分秒。秒が 60.00 にならない（繰り上がる）
  static void Dms() {
    Section("合成: 度分秒の表示");
    var f = typeof(Script_Instance).GetMethod("ToDMS", BindingFlags.NonPublic | BindingFlags.Static);
    Func<double, string> D = v => (string)f.Invoke(null, new object[] { v });
    string[][] cases = {
      new[] { "139.833333333333", "139°50'0.00\"" },   // 9系の原点。以前は 139°49'60.00"
      new[] { "134.333333333333", "134°20'0.00\"" },   // 5系の原点
      new[] { "132.166666666667", "132°10'0.00\"" },   // 3系の原点
      new[] { "34.433333333", "34°26'0.00\"" },        // 区画の北端（小数8〜9桁の値）。以前は 34°25'60.00"
      new[] { "34.4708333333", "34°28'15.00\"" },
      new[] { "35", "35°0'0.00\"" },
      new[] { "35.99999999", "36°0'0.00\"" } };      // 度まで繰り上がる
    foreach (var c in cases) {
      string got = D(double.Parse(c[0], CultureInfo.InvariantCulture));
      Check(c[0] + " → " + c[1], got == c[1], got);
    }
  }

  // PC の地域設定（小数点が「,」の言語など）に左右されない
  static void Culture() {
    Section("合成: 地域設定");
    var original = Thread.CurrentThread.CurrentCulture;
    string t = G.Tile(0, 0, 5, 4, 4);
    Thread.CurrentThread.CurrentCulture = new CultureInfo("ja-JP");
    var baseKeys = FaceKeys((Mesh)G.Run(t, 1, Point3d.Unset, 0)[7]);
    foreach (var name in new[] { "de-DE", "fr-FR", "ru-RU" }) {
      Thread.CurrentThread.CurrentCulture = new CultureInfo(name);
      var a = G.Run(t, 1, Point3d.Unset, 0);
      var m = a[7] as Mesh;
      Check(name + " でも日本語の設定と同じメッシュになる", m != null && FaceKeys(m).SequenceEqual(baseKeys), m == null ? G.Info(a).Split('\n')[0] : null);
    }
    Thread.CurrentThread.CurrentCulture = original;
  }

  // 出力は Rhino の文書の単位に換算する。target_point と size も文書の単位で受ける
  static void Units() {
    Section("合成: Rhino の文書の単位");
    string big = G.Tile(0, 0, 10, 8, 8);
    try {
      RhinoDoc.ActiveDoc = null;
      var none = G.Run(big, 1, Point3d.Unset, 0);
      Check("文書がないときはメートルのまま", Line(none, "Units:").Contains("Meters (no document)"), Line(none, "Units:").Trim());
      RhinoDoc.ActiveDoc = new RhinoDoc { ModelUnitSystem = UnitSystem.Meters };
      var m = G.Run(big, 1, Point3d.Unset, 0);
      RhinoDoc.ActiveDoc = new RhinoDoc { ModelUnitSystem = UnitSystem.Millimeters };
      var mm = G.Run(big, 1, Point3d.Unset, 0);
      var Pm = ((Mesh)m[7]).Vertices.P; var Pmm = ((Mesh)mm[7]).Vertices.P;
      double maxd = 0;
      for (int i = 0; i < Pm.Count; i++) maxd = Math.Max(maxd, Math.Max(Math.Abs(Pm[i].X * 1000 - Pmm[i].X), Math.Max(Math.Abs(Pm[i].Y * 1000 - Pmm[i].Y), Math.Abs(Pm[i].Z * 1000 - Pmm[i].Z))));
      Check("ミリメートルの文書では X・Y・Z がすべて1000倍", maxd < 1e-3, "最大差 " + maxd.ToString("E1") + " mm");
      Check("info に単位が出る", Line(mm, "Units:").Contains("Millimeters") && Line(mm, "Units:").Contains("1000"), Line(mm, "Units:").Trim());
      // 切り抜きの中心と大きさも文書の単位で渡す
      var q = Pm[4 * 10 + 5];
      RhinoDoc.ActiveDoc = new RhinoDoc { ModelUnitSystem = UnitSystem.Meters };
      int facesM = ((Mesh)G.Run(big, 1, new Point3d(q.X, q.Y, 0), 14)[7]).Faces.Count;
      RhinoDoc.ActiveDoc = new RhinoDoc { ModelUnitSystem = UnitSystem.Millimeters };
      int facesMm = ((Mesh)G.Run(big, 1, new Point3d(q.X * 1000, q.Y * 1000, 0), 14000)[7]).Faces.Count;
      Check("切り抜きも文書の単位で同じ範囲になる", facesM == facesMm && facesM > 0, "m: " + facesM + " 面 / mm: " + facesMm + " 面");
    } finally {
      RhinoDoc.ActiveDoc = null;
    }
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
      var a0 = G.Call(tile, 9, step, Point3d.Unset, 0, 0);
      var a1 = G.Call(tile, 9, step, Point3d.Unset, 0, 1);
      var a2 = G.Call(tile, 9, step, Point3d.Unset, 0, 2);
      var single = FaceKeys((Mesh)a0[7]);
      var parts = (List<Mesh>)a1[8];
      var union = parts.SelectMany(m => FaceKeys(m)).ToList(); union.Sort(StringComparer.Ordinal);
      string tag = "（step " + step + "）";
      Check("種別ごとのメッシュを合わせると1枚の面と一致（すき間も重なりもない）" + tag, single.SequenceEqual(union));
      Check("結合した1枚が1枚の面と一致" + tag, single.SequenceEqual(FaceKeys((Mesh)a2[7])));
      Check("type_output 1 では output_mesh が空" + tag, a1[7] == null);
      Check("頂点ごとに色が付く" + tag, parts.All(m => m.VertexColors.C.Count == m.Vertices.Count) && ((Mesh)a2[7]).VertexColors.C.Count == ((Mesh)a2[7]).Vertices.Count);
    }
    var names = (List<string>)G.Call(tile, 9, 1, Point3d.Unset, 0, 1)[9];
    Check("1セル幅の水路も内水面として残る", names.Contains("内水面") && names.Contains("地表面"), string.Join(", ", names));
  }

  static void SrsName() {
    Section("合成: srsName の表示と警告");
    string a24 = G.Tile(0, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2024.bl");
    string b24 = G.Tile(5, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2024.bl");
    string b11 = G.Tile(5, 0, 5, 4, 8, 0, -1, G.D, "fguuid:jgd2011.bl");
    var same = G.Run(new List<string> { a24, b24 }, 1, Point3d.Unset, 0);
    Check("表記がそろっていれば警告なし", !Lines(same)[0].StartsWith("Warning") && Line(same, "[2]").Contains("fguuid:jgd2024.bl"));
    var mixed = G.Run(new List<string> { a24, b11 }, 1, Point3d.Unset, 0);
    Check("JGD2011 と JGD2024 が混ざると先頭に警告", Lines(mixed)[0].StartsWith("Warning: srsName の違う区画が混ざっています") && Lines(mixed)[0].Contains("fguuid:jgd2011.bl: 区画 2"));
    Check("警告が出てもメッシュは作られる", mixed[7] is Mesh && ((Mesh)mixed[7]).Faces.Count == 9 * 3);
    var p = ((Mesh)G.Run(a24, 1, Point3d.Unset, 0)[7]).Vertices.P[1 * 5 + 2];
    var clipped = G.Run(new List<string> { a24, b11 }, 1, new Point3d(p.X, p.Y, 0), 6);
    Check("クリッピングで外れた区画は警告の対象外", !Lines(clipped)[0].StartsWith("Warning") && Line(clipped, "[2]").Contains("(skipped)"));
    Check("srsName のないGMLは「(srsName なし)」と表示", Line(G.Run(G.Tile(0, 0, 5, 4, 4), 1, Point3d.Unset, 0), "[1]").Contains("(srsName なし)"));
  }

  // sample フォルダの国土地理院GML（2次メッシュ 513253, DEM5A）を使うテスト
  static void RealData(string dir) {
    Section("実データ（sample）");
    if (dir == null || !Directory.Exists(dir)) {
      Skip("実データのテストすべて", "テスト用のデータ（2次メッシュ 513253 の DEM5A）が見つかりません。README「テスト」を参照");
      return;
    }
    Console.WriteLine("sample: " + dir);
    Func<string, string> FP = code => Directory.GetFiles(dir, "FG-GML-5132-53-" + code + "-DEM5A-*.xml").OrderBy(x => x).LastOrDefault();
    Func<string, string> F = code => { var f = FP(code); return f == null ? null : File.ReadAllText(f); };
    string t06 = F("06"), t07 = F("07"), t16 = F("16"), t17 = F("17"), t52 = F("52"), t53 = F("53"), t62 = F("62"), t63 = F("63");

    if (t62 == null || t63 == null) Skip("東西の境目（62・63）", "区画のファイルがありません");
    else {
      var a = G.Call(new List<string> { t62, t63 }, 0, 1, Point3d.Unset, 0, 0);
      var P = ((Mesh)a[7]).Vertices.P; int nX = 450, nY = 150;
      Check("62+63: 系の自動判定は3系（広島市付近、2区画とも表で決まる）", Line(a, "JGD Zone").Contains("3 (Auto Detected: mesh table, 2/2 tiles)") && !Lines(a)[0].StartsWith("Warning"), Line(a, "JGD Zone").Trim());
      Check("62+63: 450×150点で無効な点がない", P.Count == nX * nY && P.All(v => v.set));
      Check("62+63: info の度分秒に 60.00 秒が出ない", !Lines(a).Any(l => l.Contains("'60.00")), Line(a, "Lat Range").Trim());
      Check("62+63: 面の数 = 449×149（すき間なし）", ((Mesh)a[7]).Faces.Count == (nX - 1) * (nY - 1));
      var p62 = ((Mesh)G.Call(t62, 0, 1, Point3d.Unset, 0, 0)[7]).Vertices.P;
      var p63 = ((Mesh)G.Call(t63, 0, 1, Point3d.Unset, 0, 0)[7]).Vertices.P;
      double maxd = 0;
      for (int r = 0; r < nY; r++) for (int c = 0; c < 225; c++) {
        Point3d m1 = P[r * nX + c], s1 = p62[r * 225 + c], m2 = P[r * nX + 225 + c], s2 = p63[r * 225 + c];
        maxd = Math.Max(maxd, Math.Max(Math.Abs(m1.X - s1.X) + Math.Abs(m1.Y - s1.Y) + Math.Abs(m1.Z - s1.Z), Math.Abs(m2.X - s2.X) + Math.Abs(m2.Y - s2.Y) + Math.Abs(m2.Z - s2.Z)));
      }
      Check("62+63: まとめても1区画ずつ読んだ点と同じ位置", maxd < 1e-6, "最大差 " + maxd.ToString("E1") + " m");
      var byPath = G.Call(new List<string> { FP("62"), FP("63") }, 0, 1, Point3d.Unset, 0, 0);
      Check("62+63: ファイルのパスで渡しても同じメッシュ", G.Dump(byPath) == G.Dump(a));
      double inner = 0, seam = 0;
      for (int r = 0; r < nY; r++) { inner += P[r * nX + 224].X - P[r * nX + 223].X; seam += P[r * nX + 225].X - P[r * nX + 224].X; }
      Check("62+63: 境目の点の間隔 = 区画の中の間隔", Math.Abs(inner - seam) / nY < 0.001, "境目 " + (seam / nY).ToString("F3") + " m / 内側 " + (inner / nY).ToString("F3") + " m");
    }

    if (t52 == null || t53 == null || t62 == null || t63 == null) Skip("南北の境目（52・53・62・63）", "区画のファイルがありません");
    else {
      var b = G.Call(new List<string> { t53, t62, t52, t63 }, 0, 1, Point3d.Unset, 0, 0);
      var Q = ((Mesh)b[7]).Vertices.P; int qX = 450;
      int seamFaces = 0;
      for (int c = 0; c < qX - 1; c++) if (Q[149 * qX + c].set && Q[149 * qX + c + 1].set && Q[150 * qX + c].set && Q[150 * qX + c + 1].set) seamFaces++;
      Check("52+53+62+63（順不同）: 南北の境目に面が張られる", seamFaces == qX - 1, seamFaces + " / " + (qX - 1));
    }

    if (t06 == null || t07 == null || t16 == null || t17 == null) Skip("種別による分け方（06・07・16・17）", "区画のファイルがありません");
    else {
      var files = new List<string> { t06, t07, t16, t17 };
      var a0 = G.Call(files, 3, 1, Point3d.Unset, 0, 0);
      var a1 = G.Call(files, 3, 1, Point3d.Unset, 0, 1);
      var a2 = G.Call(files, 3, 1, Point3d.Unset, 0, 2);
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
      var ok = G.Call(new List<string> { t62, t63 }, 0, 1, Point3d.Unset, 0, 0);
      Check("実データの srsName が表示され、そろっていれば警告なし", !Lines(ok)[0].StartsWith("Warning") && Line(ok, "[1]").Contains("fguuid:"), Line(ok, "[1]").Trim());
      string old63 = t63.Replace("fguuid:jgd2024.bl", "fguuid:jgd2011.bl");
      if (old63 == t63) Skip("JGD2011 の区画が混ざったときの警告（実データ）", "sample の srsName が jgd2024 ではありません");
      else Check("JGD2011 と書き換えた区画が混ざると警告", Lines(G.Call(new List<string> { t62, old63 }, 0, 1, Point3d.Unset, 0, 0))[0].StartsWith("Warning"));
    }

    var all = Directory.GetFiles(dir, "*.xml").OrderBy(x => x).Select(File.ReadAllText).ToList();
    var sw = Stopwatch.StartNew();
    var z = G.Call(all, 0, 4, Point3d.Unset, 0, 2);
    Check("sample の全区画（" + all.Count + " 区画、step 4）を読み込める", z[7] is Mesh && !G.Info(z).StartsWith("Error"), sw.ElapsedMilliseconds + " ms, " + Line(z, "Output Mesh").Trim());
  }
}
