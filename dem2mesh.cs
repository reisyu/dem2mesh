using System;
using System.Collections.Generic;
using System.Xml;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Rhino;
using Rhino.Geometry;

public class Script_Instance : GH_ScriptInstance
{
  private const float NoData = -9999f;
  private const long MaxGridCells = 50000000; // 統合した格子の上限（float 1つ4バイトで約200MB）
  private const byte NoType = 255;

  // DEM構成点種別（仕様書 V5.3）と、種別ごとのメッシュの色。表にない種別はデータに出てきた順に後ろへ追加し、灰色にする
  private static readonly string[] KnownTypes = { "地表面", "表層面", "海水面", "内水面", "海水底面", "内水底面", "データなし", "その他" };
  private static readonly System.Drawing.Color[] KnownTypeColors = {
    System.Drawing.Color.FromArgb(196, 170, 130), System.Drawing.Color.FromArgb(110, 160, 90), System.Drawing.Color.FromArgb(40, 90, 170), System.Drawing.Color.FromArgb(90, 170, 220),
    System.Drawing.Color.FromArgb(30, 60, 120), System.Drawing.Color.FromArgb(60, 130, 160), System.Drawing.Color.FromArgb(160, 160, 160), System.Drawing.Color.FromArgb(160, 160, 160) };
  // 面の4隅の種別が同数のときに優先する順。細い川や水路が消えないよう、地表面を最後にする
  private static readonly string[] TiePriority = { "海水面", "内水面", "海水底面", "内水底面", "表層面", "その他", "データなし", "地表面" };

  // 1区画分のGML
  private class Tile {
    public int no; // 入力リストでの番号（1から）
    public string mesh, srs; // メッシュ番号、座標参照系（例: fguuid:jgd2024.bl）
    public XmlDocument doc; public XmlNamespaceManager ns;
    public double lLat, lLon, uLat, uLon;
    public int nx, ny, offset;
  }

  private void RunScript(
        List<string> xml_data,
        int jgd_zone,
        int sample_step,
        Point3d target_point,
        double size,
        int type_output,
        ref object info,
        ref object output_mesh,
        ref object type_meshes,
        ref object type_names)
  {
    List<string> sources = (xml_data == null) ? new List<string>() : xml_data.Where(s => !string.IsNullOrEmpty(s)).ToList();
    if (sources.Count == 0) {
      info = "Wait: xml_data が未入力です。";
      return;
    }

    try {
      // --- 各区画の範囲と格子を読む ---
      List<Tile> tiles = new List<Tile>();
      for (int i = 0; i < sources.Count; i++) {
        try { tiles.Add(ParseTile(sources[i], i + 1)); }
        catch (Exception ex) { throw new Exception("区画 " + (i + 1) + ": " + ex.Message); }
      }

      // 全区画でセルの大きさが同じでないと1つの格子にまとめられない
      // セルは秒単位で決まっている（1m: 0.04″, 5m: 0.2″, 10m: 0.4″）ので、四隅の丸め誤差を除くため 0.0001″ に丸める
      double cellLat = Math.Round((tiles[0].uLat - tiles[0].lLat) / tiles[0].ny * 3600.0, 4) / 3600.0;
      double cellLon = Math.Round((tiles[0].uLon - tiles[0].lLon) / tiles[0].nx * 3600.0, 4) / 3600.0;
      foreach (Tile t in tiles) {
        if (Math.Abs((t.uLat - t.lLat) / t.ny - cellLat) > cellLat * 1e-3 || Math.Abs((t.uLon - t.lLon) / t.nx - cellLon) > cellLon * 1e-3)
          throw new Exception("区画 " + t.no + " のセルの大きさが区画 1 と違います（5mと10mなど、解像度の違うデータは混ぜられません）。");
        double fc = (t.lLon - tiles[0].lLon) / cellLon, fr = (t.uLat - tiles[0].uLat) / cellLat;
        if (Math.Abs(fc - Math.Round(fc)) > 0.01 || Math.Abs(fr - Math.Round(fr)) > 0.01)
          throw new Exception("区画 " + t.no + " の格子が区画 1 の格子と揃っていません。");
      }

      // --- 平面直角座標系の設定（全区画で共通） ---
      double allLLat = tiles.Min(t => t.lLat), allULat = tiles.Max(t => t.uLat);
      double allLLon = tiles.Min(t => t.lLon), allULon = tiles.Max(t => t.uLon);
      int activeZone = (jgd_zone <= 0) ? AutoDetectJGDZone((allLLat + allULat) / 2.0, (allLLon + allULon) / 2.0) : jgd_zone;
      double[] origin_lats = { 33, 33, 36, 33, 36, 36, 36, 36, 36, 40, 44, 44, 44, 26, 26, 26, 26, 20, 26 };
      double[] origin_lons = { 129.5, 131.0, 132.166666666667, 133.5, 134.333333333333, 136.0, 137.166666666667, 138.5, 139.833333333333, 140.833333333333, 140.25, 142.25, 144.25, 142.0, 127.5, 124.0, 131.0, 136.0, 154.0 };
      double phi0_deg = origin_lats[activeZone - 1];
      double lam0_deg = origin_lons[activeZone - 1];
      Projection proj = new Projection(phi0_deg, lam0_deg);

      // 座標はメートルで計算し、出力だけ Rhino の文書の単位に換算する
      // target_point と size は文書の単位で入ってくるので、メートルに戻して比べる
      string unitName;
      double unit = ModelUnitScale(out unitName);
      bool doClipping = (target_point != Point3d.Unset && size > 0);
      double half = size / 2.0 / unit;
      double clipE = target_point.X / unit, clipN = target_point.Y / unit;
      int step = Math.Max(1, sample_step);

      // クリッピング範囲にかからない区画は標高を読まずに飛ばす
      List<Tile> used = doClipping ? tiles.Where(t => TouchesClip(t, proj, clipE, clipN, half)).ToList() : tiles;
      if (used.Count == 0) {
        info = "クリッピング範囲にかかる区画がありません。";
        return;
      }

      // --- 使う区画をすべて覆う1つの格子に標高を並べる ---
      double lLat = used.Min(t => t.lLat), uLat = used.Max(t => t.uLat);
      double lLon = used.Min(t => t.lLon), uLon = used.Max(t => t.uLon);
      int orig_nx = (int)Math.Round((uLon - lLon) / cellLon);
      int orig_ny = (int)Math.Round((uLat - lLat) / cellLat);
      if ((long)orig_nx * orig_ny > MaxGridCells)
        throw new Exception(string.Format("区画をまとめた範囲が広すぎます（{0} x {1} セル）。離れた区画を同時に読み込んでいないか確認してください。", orig_nx, orig_ny));

      float[] heights = new float[orig_nx * orig_ny];
      byte[] types = new byte[orig_nx * orig_ny]; // DEM構成点種別（typeNames の番号）
      for (int i = 0; i < heights.Length; i++) { heights[i] = NoData; types[i] = NoType; }
      List<string> typeNames = new List<string>(KnownTypes);
      foreach (Tile t in used) {
        int col0 = (int)Math.Round((t.lLon - lLon) / cellLon), row0 = (int)Math.Round((uLat - t.uLat) / cellLat);
        try { LoadHeights(t, heights, types, typeNames, orig_nx, col0, row0); }
        catch (Exception ex) { throw new Exception("区画 " + t.no + ": " + ex.Message); }
      }

      List<int> rows = new List<int>();
      for (int r = 0; r < orig_ny; r += step) rows.Add(r);
      if (rows.Count > 0 && rows.Last() != orig_ny - 1) rows.Add(orig_ny - 1);
      List<int> cols = new List<int>();
      for (int c = 0; c < orig_nx; c += step) cols.Add(c);
      if (cols.Count > 0 && cols.Last() != orig_nx - 1) cols.Add(orig_nx - 1);

      int nY = rows.Count; int nX = cols.Count;
      Point3d[] verts = new Point3d[nX * nY];
      bool[] isValid = new bool[nX * nY];
      byte[] vType = new byte[nX * nY];

      // 標高点はセルの中心にある（範囲を点の数で等分し、半セル内側から始まる）
      Parallel.For(0, nY, rIdx => {
        int r = rows[rIdx];
        double lat = uLat - (r + 0.5) * cellLat;
        for (int cIdx = 0; cIdx < nX; cIdx++) {
          int c = cols[cIdx];
          double lon = lLon + (c + 0.5) * cellLon;
          double pX, pY;

          proj.Compute(lat, lon, out pX, out pY);

          // 平面直角座標の X は北、Y は東。Rhino では東を X、北を Y にする
          int idx = rIdx * nX + cIdx;
          if (!doClipping || (Math.Abs(pY - clipE) <= half && Math.Abs(pX - clipN) <= half)) {
            float z = GetRawHeight(c, r, heights, orig_nx, orig_ny);
            if (z > -9000) { verts[idx] = new Point3d(pY * unit, pX * unit, z * unit); isValid[idx] = true; vType[idx] = types[r * orig_nx + c]; }
          }
        }
      });

      Mesh mesh = null;
      List<Mesh> typeMeshList = null; List<string> typeNameList = null;
      int[] faceCountByType = new int[typeNames.Count];
      int faceCount = 0;
      if (type_output <= 0) {
        // 種別を使わない: 1枚のメッシュ（従来どおり）
        mesh = new Mesh();
        mesh.Vertices.AddVertices(verts);
        for (int r = 0; r < nY - 1; r++) {
          for (int c = 0; c < nX - 1; c++) {
            int v1 = r * nX + c; int v2 = v1 + 1;
            int v3 = (r + 1) * nX + c + 1; int v4 = (r + 1) * nX + c;
            if (isValid[v1] && isValid[v2] && isValid[v3] && isValid[v4]) { mesh.Faces.AddFace(v1, v4, v3, v2); faceCount++; }
          }
        }
        mesh.Vertices.CullUnused();
        mesh.Normals.ComputeNormals();
      } else {
        // 種別ごとに別のメッシュを作る。面の種別は4隅の多数決（同数なら TiePriority の順）
        // 境目の頂点は両側のメッシュに同じ座標で複製されるので、メッシュの間に隙間はできない
        int[] tieRank = typeNames.Select(n => { int i = Array.IndexOf(TiePriority, n); return i >= 0 ? i : Array.IndexOf(TiePriority, "その他"); }).ToArray();
        Mesh[] byType = new Mesh[typeNames.Count];
        int[][] localIdx = new int[typeNames.Count][];
        for (int r = 0; r < nY - 1; r++) {
          for (int c = 0; c < nX - 1; c++) {
            int v1 = r * nX + c; int v2 = v1 + 1;
            int v3 = (r + 1) * nX + c + 1; int v4 = (r + 1) * nX + c;
            if (!(isValid[v1] && isValid[v2] && isValid[v3] && isValid[v4])) continue;
            int ft = FaceType(vType[v1], vType[v2], vType[v3], vType[v4], tieRank);
            if (byType[ft] == null) {
              byType[ft] = new Mesh();
              localIdx[ft] = new int[nX * nY];
              for (int i = 0; i < localIdx[ft].Length; i++) localIdx[ft][i] = -1;
            }
            Mesh m = byType[ft]; int[] map = localIdx[ft];
            m.Faces.AddFace(LocalVertex(m, map, v1, verts), LocalVertex(m, map, v4, verts), LocalVertex(m, map, v3, verts), LocalVertex(m, map, v2, verts));
            faceCountByType[ft]++; faceCount++;
          }
        }
        typeMeshList = new List<Mesh>(); typeNameList = new List<string>();
        for (int k = 0; k < typeNames.Count; k++) {
          if (byType[k] == null) continue;
          byType[k].Normals.ComputeNormals();
          byType[k].VertexColors.CreateMonotoneMesh(TypeColor(typeNames[k]));
          typeMeshList.Add(byType[k]);
          typeNameList.Add(typeNames[k]);
        }
        if (type_output >= 2) {
          // 種別ごとのメッシュを1枚に結合する。境目の頂点は溶接しない（溶接すると境目の色が混ざるため）
          mesh = new Mesh();
          foreach (Mesh m in typeMeshList) mesh.Append(m);
          mesh.Normals.ComputeNormals();
        }
      }
      output_mesh = mesh;
      type_meshes = typeMeshList;
      type_names = typeNameList;

      // --- 情報出力の構築 ---
      List<string> log = new List<string>();
      log.Add("== Source Info ==");
      log.Add(" Tiles: " + used.Count + " used / " + tiles.Count + " loaded" + (used.Count < tiles.Count ? " (others outside clipping)" : ""));
      log.Add(string.Format(" Lat Range: {0} to {1}", ToDMS(lLat), ToDMS(uLat)));
      log.Add(string.Format(" Lon Range: {0} to {1}", ToDMS(lLon), ToDMS(uLon)));
      log.Add(string.Format(" Original Size: {0} x {1}", orig_nx, orig_ny));
      log.Add("");
      log.Add("== Current Settings ==");
      log.Add(" JGD Zone: " + activeZone + (jgd_zone <= 0 ? " (Auto Detected)" : " (Manual)"));

      // 原点の緯度経度を出力
      log.Add(string.Format(" Origin Lat: {0} ({1})", ToDMS(phi0_deg), phi0_deg));
      log.Add(string.Format(" Origin Lon: {0} ({1})", ToDMS(lam0_deg), lam0_deg));

      log.Add(string.Format(" Units: {0} (1 m = {1} units)", unitName, unit.ToString("G", CultureInfo.InvariantCulture)));
      log.Add(string.Format(" Output Mesh: {0}x{1} pts, {2} faces", nX, nY, faceCount));
      log.Add(" Type Output: " + (type_output >= 2 ? "Split by Type + Joined" : (type_output == 1 ? "Split by Type" : "Single Mesh")));
      if (type_output > 0) {
        for (int k = 0; k < typeNames.Count; k++) {
          if (faceCountByType[k] == 0) continue;
          System.Drawing.Color col = TypeColor(typeNames[k]);
          log.Add(string.Format("  - {0}: {1} faces (RGB {2},{3},{4})", typeNames[k] == "" ? "(種別なし)" : typeNames[k], faceCountByType[k], col.R, col.G, col.B));
        }
      }

      // --- 区画ごとの座標参照系 ---
      log.Add("");
      log.Add("== Tiles ==");
      foreach (Tile t in tiles)
        log.Add(string.Format(" [{0}] {1} {2}{3}", t.no, t.mesh, t.srs == "" ? "(srsName なし)" : t.srs, used.Contains(t) ? "" : " (skipped)"));

      // メッシュに使った区画で srsName が混ざっていたら、先頭に警告を出す
      // JGD2011 と JGD2024 は平面位置は同じだが、標高の基準（測地成果2011 / 2024）が違う
      var srsGroups = used.GroupBy(t => t.srs == "" ? "(srsName なし)" : t.srs).ToList();
      if (srsGroups.Count > 1) {
        string detail = string.Join(" / ", srsGroups.Select(g => g.Key + ": 区画 " + string.Join(", ", g.Select(t => t.no))));
        log.Insert(0, "Warning: srsName の違う区画が混ざっています（" + detail + "）。");
        log.Insert(1, " JGD2011 と JGD2024 の区画を混ぜると、標高の基準が違うため境目に数cm〜数十cmの段差が出ることがあります。");
        log.Insert(2, " README「注意: 測地成果2011と測地成果2024」を参照してください。");
        log.Insert(3, "");
      }
      info = log;

    } catch (Exception ex) { info = "Error: " + ex.Message; }
  }

  // --- GMLの読み込み ---
  private Tile ParseTile(string xml, int no) {
    Tile t = new Tile();
    t.no = no;
    t.doc = new XmlDocument();
    t.doc.LoadXml(xml);
    t.ns = new XmlNamespaceManager(t.doc.NameTable);
    t.ns.AddNamespace("gml", "http://www.opengis.net/gml/3.2");
    t.ns.AddNamespace("fgd", "http://fgd.gsi.go.jp/spec/2008/FGD_GMLSchema");

    XmlNode meshNode = t.doc.SelectSingleNode(".//fgd:mesh", t.ns);
    t.mesh = (meshNode != null) ? meshNode.InnerText.Trim() : "-";
    XmlNode env = t.doc.SelectSingleNode(".//gml:Envelope", t.ns);
    XmlAttribute srsAttr = (env != null) ? env.Attributes["srsName"] : null;
    t.srs = (srsAttr != null) ? srsAttr.Value.Trim() : "";

    string[] lcParts = SplitSpaces(ReadNode(t, "gml:lowerCorner"));
    string[] ucParts = SplitSpaces(ReadNode(t, "gml:upperCorner"));
    t.lLat = ParseDouble(lcParts[0]); t.lLon = ParseDouble(lcParts[1]);
    t.uLat = ParseDouble(ucParts[0]); t.uLon = ParseDouble(ucParts[1]);

    string[] hiParts = SplitSpaces(ReadNode(t, "gml:high"));
    t.nx = ParseInt(hiParts[0]) + 1;
    t.ny = ParseInt(hiParts[1]) + 1;

    int startX = 0, startY = 0;
    XmlNode sn = t.doc.SelectSingleNode(".//gml:startPoint", t.ns);
    if (sn != null) {
      string[] sParts = SplitSpaces(sn.InnerText);
      startX = ParseInt(sParts[0]); startY = ParseInt(sParts[1]);
    }
    t.offset = startY * t.nx + startX;
    return t;
  }

  // 区画の標高と種別を、統合した格子の (col0, row0) を左上として書き込む
  private void LoadHeights(Tile t, float[] grid, byte[] types, List<string> typeNames, int gnx, int col0, int row0) {
    string[] lines = ReadNode(t, "gml:tupleList").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    int total = t.nx * t.ny;
    for (int i = 0; i < lines.Length && t.offset + i < total; i++) {
      string[] p = lines[i].Split(',');
      float z;
      if (!float.TryParse(p.Length >= 2 ? p[1] : p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out z) || z <= -9000) continue;
      int k = t.offset + i;
      int gIdx = (row0 + k / t.nx) * gnx + col0 + k % t.nx;
      if (grid[gIdx] > -9000) continue; // 区画が重なる場合は先に読んだ値を優先
      grid[gIdx] = z;
      string name = p.Length >= 2 ? p[0].Trim() : "";
      int ti = typeNames.IndexOf(name);
      if (ti < 0) {
        if (typeNames.Count >= NoType) ti = typeNames.IndexOf("その他"); // 種別が多すぎる場合はまとめる
        else { typeNames.Add(name); ti = typeNames.Count - 1; }
      }
      types[gIdx] = (byte)ti;
    }
  }

  // 面の種別: 4隅で一番多い種別。同数なら tieRank の小さい方
  private int FaceType(int a, int b, int c, int d, int[] tieRank) {
    int[] q = { a, b, c, d };
    int best = -1, bestN = 0;
    for (int i = 0; i < 4; i++) {
      int n = 0;
      for (int j = 0; j < 4; j++) if (q[j] == q[i]) n++;
      if (n > bestN || (n == bestN && tieRank[q[i]] < tieRank[best])) { best = q[i]; bestN = n; }
    }
    return best;
  }

  // 種別ごとのメッシュに頂点を追加し（追加済みならその番号を返し）、メッシュ内での番号を返す
  private int LocalVertex(Mesh m, int[] map, int v, Point3d[] verts) {
    if (map[v] < 0) map[v] = m.Vertices.Add(verts[v]);
    return map[v];
  }

  private System.Drawing.Color TypeColor(string name) {
    int i = Array.IndexOf(KnownTypes, name);
    return i >= 0 ? KnownTypeColors[i] : System.Drawing.Color.FromArgb(160, 160, 160);
  }

  private string ReadNode(Tile t, string name) {
    XmlNode node = t.doc.SelectSingleNode(".//" + name, t.ns);
    if (node == null) throw new Exception(name + " が見つかりません。");
    return node.InnerText.Trim();
  }
  private string[] SplitSpaces(string s) { return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); }

  // GMLの数値は常に「.」が小数点。PC の地域設定（ドイツ語なら「,」）に左右されないように読む
  private static double ParseDouble(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
  private static int ParseInt(string s) { return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture); }

  // 区画の四隅を投影した範囲がクリッピング範囲にかかるか（すべてメートル）
  private bool TouchesClip(Tile t, Projection proj, double clipE, double clipN, double half) {
    double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
    foreach (double lat in new[] { t.lLat, t.uLat }) {
      foreach (double lon in new[] { t.lLon, t.uLon }) {
        double pX, pY;
        proj.Compute(lat, lon, out pX, out pY);
        minN = Math.Min(minN, pX); maxN = Math.Max(maxN, pX);
        minE = Math.Min(minE, pY); maxE = Math.Max(maxE, pY);
      }
    }
    const double margin = 1.0; // 区画の辺のわずかな曲がりの分
    return maxE + margin >= clipE - half && minE - margin <= clipE + half && maxN + margin >= clipN - half && minN - margin <= clipN + half;
  }

  // メートルから Rhino の文書の単位への倍率（文書がなければメートルのまま）
  private static double ModelUnitScale(out string unitName) {
    RhinoDoc doc = RhinoDoc.ActiveDoc;
    if (doc == null) { unitName = "Meters (no document)"; return 1.0; }
    unitName = doc.ModelUnitSystem.ToString();
    return RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
  }

  // --- 座標変換（国土地理院 測量計算規則のガウス・クリューゲル図法、6次展開） ---
  // 国土地理院の測量計算サイト（bl2xy）と 0.05 mm 以内で一致することを確かめてある
  // 楕円体（GRS80）と原点で決まる値はコンストラクタで1回だけ計算し、点ごとには緯度経度で変わる部分だけを計算する
  // （点ごとに係数を計算し直していたときは、6次が3次の約2.7倍遅かった）
  private class Projection {
    private readonly double[] alpha = new double[7];
    private readonly double am0, s0m0, e, lam0;

    public Projection(double phi0_deg, double lam0_deg) {
      const double a = 6378137.0, f = 1.0 / 298.257222101, m0 = 0.9999;
      double n = f / (2.0 - f);
      double n2 = n * n, n3 = n2 * n, n4 = n3 * n, n5 = n4 * n, n6 = n5 * n;
      am0 = (a / (1.0 + n)) * (1.0 + n2 / 4.0 + n4 / 64.0 + n6 / 256.0) * m0;

      alpha[1] = 1.0/2.0*n - 2.0/3.0*n2 + 5.0/16.0*n3 + 41.0/180.0*n4 - 127.0/288.0*n5 + 7891.0/37800.0*n6;
      alpha[2] = 13.0/48.0*n2 - 3.0/5.0*n3 + 557.0/1440.0*n4 + 281.0/630.0*n5 - 1983433.0/1935360.0*n6;
      alpha[3] = 61.0/240.0*n3 - 103.0/140.0*n4 + 15061.0/26880.0*n5 + 167603.0/181440.0*n6;
      alpha[4] = 49561.0/161280.0*n4 - 179.0/168.0*n5 + 6601661.0/7257600.0*n6;
      alpha[5] = 34729.0/80640.0*n5 - 3418889.0/1995840.0*n6;
      alpha[6] = 212378941.0/319334400.0*n6;
      e = 2.0 * Math.Sqrt(n) / (1.0 + n);

      // 原点の子午線弧長 S0
      double phi0 = phi0_deg * Math.PI / 180.0;
      lam0 = lam0_deg * Math.PI / 180.0;
      double A0 = 1.0 + n2/4.0 + n4/64.0 + n6/256.0;
      double A1 = -3.0/2.0*(n - n3/8.0 - n5/64.0);
      double A2 = 15.0/16.0*(n2 - n4/4.0 - n6/16.0);
      double A3 = -35.0/48.0*(n3 - 5.0/16.0*n5);
      double A4 = 315.0/512.0*(n4 - n6/3.0);
      double A5 = -693.0/1280.0*n5;
      double A6 = 1001.0/2048.0*n6;
      s0m0 = (a / (1.0 + n)) * (A0*phi0 + A1*Math.Sin(2*phi0) + A2*Math.Sin(4*phi0) + A3*Math.Sin(6*phi0) + A4*Math.Sin(8*phi0) + A5*Math.Sin(10*phi0) + A6*Math.Sin(12*phi0)) * m0;
    }

    // 緯度経度（度）→ 平面直角座標（メートル。X は北向き、Y は東向き）
    public void Compute(double lat_deg, double lon_deg, out double X, out double Y) {
      double phi = lat_deg * Math.PI / 180.0, dlh = lon_deg * Math.PI / 180.0 - lam0, sp = Math.Sin(phi);
      double t = Math.Sinh(Math.Atanh(sp) - e * Math.Atanh(e * sp));
      double xi = Math.Atan(t / Math.Cos(dlh));
      double eta = Math.Atanh(Math.Sin(dlh) / Math.Sqrt(1.0 + t * t));

      // sin/cos(2jξ)・sinh/cosh(2jη) は加法定理の漸化式で求め、三角関数・双曲線関数の呼び出しを各1回にする
      double s1 = Math.Sin(2 * xi), c1 = Math.Cos(2 * xi), sh1 = Math.Sinh(2 * eta), ch1 = Math.Cosh(2 * eta);
      double s = s1, c = c1, sh = sh1, ch = ch1, sX = 0, sE = 0;
      for (int j = 1; j <= 6; j++) {
        sX += alpha[j] * s * ch;
        sE += alpha[j] * c * sh;
        double sNext = s * c1 + c * s1, cNext = c * c1 - s * s1, shNext = sh * ch1 + ch * sh1, chNext = ch * ch1 + sh * sh1;
        s = sNext; c = cNext; sh = shNext; ch = chNext;
      }
      X = am0 * (xi + sX) - s0m0;
      Y = am0 * (eta + sE);
    }
  }

  private string ToDMS(double dd) {
    int d = (int)dd; double m_r = (dd - d) * 60; int m = (int)m_r; double s = (m_r - m) * 60;
    return string.Format("{0}°{1}'{2:F2}\"", d, m, s);
  }
  private int AutoDetectJGDZone(double lat, double lon) {
    double[] lons = { 129.5, 131.0, 132.166667, 133.5, 134.333333, 136.0, 137.166667, 138.5, 139.833333, 140.833333, 140.25, 142.25, 144.25, 142.0, 127.5, 124.0, 131.0, 136.0, 154.0 };
    int b = 1; double min = 1000;
    for (int i = 0; i < lons.Length; i++) { double d = Math.Abs(lon - lons[i]); if (d < min) { min = d; b = i + 1; } }
    return b;
  }
  private float GetRawHeight(int c, int r, float[] grid, int nx, int ny) {
    if (c < 0 || c >= nx || r < 0 || r >= ny) return NoData;
    return grid[r * nx + c];
  }
}
