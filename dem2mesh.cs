using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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
    public int zone; public ZoneHow zoneHow; public string zoneCands = ""; // 系の自動判定の結果（自動判定のときだけ）
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
      List<string> warnings = new List<string>(); // info の先頭に出す警告
      string zoneNote = "Manual";
      if (jgd_zone > 19)
        throw new Exception("jgd_zone は 0（自動判定）か、1〜19 の系の番号で指定してください（指定された値: " + jgd_zone + "）。");
      int activeZone = (jgd_zone <= 0) ? DetectZone(tiles, warnings, out zoneNote) : jgd_zone;
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
      log.Add(" JGD Zone: " + activeZone + " (" + zoneNote + ")");

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
        log.Add(string.Format(" [{0}] {1} {2}{3}{4}", t.no, t.mesh, t.srs == "" ? "(srsName なし)" : t.srs,
          jgd_zone <= 0 ? " zone " + t.zone + " (" + ZoneHowText(t) + ")" : "", used.Contains(t) ? "" : " (skipped)"));

      // メッシュに使った区画で srsName が混ざっていたら、先頭に警告を出す
      // JGD2011 と JGD2024 は平面位置は同じだが、標高の基準（測地成果2011 / 2024）が違う
      var srsGroups = used.GroupBy(t => t.srs == "" ? "(srsName なし)" : t.srs).ToList();
      if (srsGroups.Count > 1) {
        string detail = string.Join(" / ", srsGroups.Select(g => g.Key + ": 区画 " + string.Join(", ", g.Select(t => t.no))));
        warnings.Add("Warning: srsName の違う区画が混ざっています（" + detail + "）。");
        warnings.Add(" JGD2011 と JGD2024 の区画を混ぜると、標高の基準が違うため境目に数cm〜数十cmの段差が出ることがあります。");
        warnings.Add(" README「注意: 測地成果2011と測地成果2024」を参照してください。");
      }
      if (warnings.Count > 0) { log.InsertRange(0, warnings); log.Insert(warnings.Count, ""); }
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

  // 度 → 度分秒（秒は小数2桁）。先に秒の単位で丸めてから分・度に繰り上げる
  // （分と秒を切り捨ててから秒を丸めると、9系の原点 139°50′00″ が 139°49'60.00" のように表示されていた）
  private static string ToDMS(double dd) {
    string sign = dd < 0 ? "-" : "";
    long hundredths = (long)Math.Round(Math.Abs(dd) * 360000.0); // 1/100 秒の単位
    long d = hundredths / 360000, m = hundredths / 6000 % 60;
    double s = hundredths % 6000 / 100.0;
    return sign + string.Format(CultureInfo.InvariantCulture, "{0}°{1}'{2:F2}\"", d, m, s);
  }
  // --- 系の自動判定 ---
  // 系の範囲は都道府県（北海道は市町村、東京都・沖縄県・鹿児島県の島は緯度経度の線）で決まっていて、経度だけでは決まらない。
  // そこで、地域メッシュ（3次メッシュ、約1 km四方）ごとに系を引ける表を埋め込み、区画の中心のメッシュで系を決める。
  // DEM の区画は 5m・1m では3次メッシュそのもの、10m では2次メッシュなので、メッシュ単位の判定で足りる
  public enum ZoneHow { Table, Boundary, BoundaryTie, NotInTable }

  // 全区画の系を区画ごとに決め、多数決で1つに決める（同数なら番号の小さい系）
  private static int DetectZone(List<Tile> tiles, List<string> warnings, out string note) {
    int[] count = new int[20];
    foreach (Tile t in tiles) {
      string cands; ZoneHow how;
      t.zone = ZoneFromLatLon((t.lLat + t.uLat) / 2.0, (t.lLon + t.uLon) / 2.0, out how, out cands);
      t.zoneHow = how; t.zoneCands = cands;
      count[t.zone]++;
      if (how == ZoneHow.BoundaryTie)
        warnings.Add("Warning: 区画 " + t.no + " は系の境界にあり、候補（" + cands + "）が周りのメッシュでも同数のため、" + t.zone + "系にしました。対象地域の系を確かめて jgd_zone で選んでください。");
      else if (how == ZoneHow.NotInTable)
        warnings.Add("Warning: 区画 " + t.no + " は地域メッシュの表に無い（海上など）ため、経度から " + t.zone + "系と推定しました。対象地域の系を確かめて jgd_zone で選んでください。");
    }
    int best = 0;
    for (int z = 1; z < 20; z++) if (count[z] > count[best]) best = z;
    int kinds = count.Count(c => c > 0);
    if (kinds > 1) {
      var parts = new List<string>();
      for (int z = 1; z < 20; z++) if (count[z] > 0) parts.Add(z + "系: 区画 " + string.Join(", ", tiles.Where(t => t.zone == z).Select(t => t.no)));
      warnings.Add("Warning: 区画によって系が違います（" + string.Join(" / ", parts) + "）。区画の多い " + best + "系にしました（同数なら番号の小さい系）。");
    }
    note = "Auto Detected: mesh table, " + count[best] + "/" + tiles.Count + " tiles";
    return best;
  }

  private static string ZoneHowText(Tile t) {
    switch (t.zoneHow) {
      case ZoneHow.Boundary: return "boundary " + t.zoneCands + ", by neighbors";
      case ZoneHow.BoundaryTie: return "boundary " + t.zoneCands + ", tie";
      case ZoneHow.NotInTable: return "not in table, by longitude";
      default: return "table";
    }
  }

  // 地点の系を表で決める。境界のメッシュ（候補が複数）は周りの8メッシュの多数決、同数なら候補のうち番号の小さい系
  private static int ZoneFromLatLon(double lat, double lon, out ZoneHow how, out string cands) {
    Dictionary<string, char> cells; Dictionary<string, string> candidates;
    LoadZoneTable(out cells, out candidates);
    string mesh = MeshCode(lat, lon);
    char c = LookupMesh(cells, mesh);
    cands = "";
    if (c == '\0') { how = ZoneHow.NotInTable; return LongitudeZone(lon); }
    if (c != '?') { how = ZoneHow.Table; return ZoneNumber(c); }

    string cc = candidates[mesh];
    cands = string.Join("/", cc.Select(ch => ZoneNumber(ch)));
    double cLat, cLon; MeshCenter(mesh, out cLat, out cLon);
    int[] votes = new int[20];
    for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++) {
      if (a == 0 && b == 0) continue;
      char n = LookupMesh(cells, MeshCode(cLat + a * 0.5 / 60.0, cLon + b * 0.75 / 60.0));
      if (n != '\0' && n != '?' && cc.IndexOf(n) >= 0) votes[ZoneNumber(n)]++;
    }
    int best = -1; bool tie = false;
    foreach (char ch in cc) { // 候補は番号の小さい順に並んでいる
      int z = ZoneNumber(ch);
      if (best < 0 || votes[z] > votes[best]) { best = z; tie = false; }
      else if (votes[z] == votes[best]) tie = true;
    }
    how = (tie || votes[best] == 0) ? ZoneHow.BoundaryTie : ZoneHow.Boundary;
    return best;
  }

  private static char LookupMesh(Dictionary<string, char> cells, string mesh) {
    char c;
    if (cells.TryGetValue(mesh, out c)) return c;                 // 系が混ざる2次メッシュの中の3次メッシュ
    if (cells.TryGetValue(mesh.Substring(0, 6), out c)) return c; // 系が1つの2次メッシュ
    return '\0';
  }
  private static int ZoneNumber(char c) { return "0123456789ABCDEFGHIJ".IndexOf(c); } // 表の1文字 → 系（A=10 … J=19）

  // 3次メッシュコード（JIS X0410）
  private static string MeshCode(double lat, double lon) {
    int p = (int)Math.Floor(lat * 1.5); double latm = lat * 60 - p * 40;
    int q = (int)Math.Floor(latm / 5), r = (int)Math.Floor((latm - q * 5) / 0.5);
    int u = (int)Math.Floor(lon) - 100; double lonm = (lon - Math.Floor(lon)) * 60;
    int v = (int)Math.Floor(lonm / 7.5), w = (int)Math.Floor((lonm - v * 7.5) / 0.75);
    return p.ToString("00", CultureInfo.InvariantCulture) + u.ToString("00", CultureInfo.InvariantCulture) + q + v + r + w;
  }
  private static void MeshCenter(string m, out double lat, out double lon) {
    int p = int.Parse(m.Substring(0, 2), CultureInfo.InvariantCulture), u = int.Parse(m.Substring(2, 2), CultureInfo.InvariantCulture);
    int q = m[4] - '0', v = m[5] - '0', r = m[6] - '0', w = m[7] - '0';
    lat = (p * 40 + q * 5 + r * 0.5 + 0.25) / 60;
    lon = 100 + u + (v * 7.5 + w * 0.75 + 0.375) / 60;
  }

  // 表に無い地点（海上など）だけに使う。原点の経度に一番近い系で、陸地では35%のメッシュで外れる
  private static int LongitudeZone(double lon) {
    double[] lons = { 129.5, 131.0, 132.166667, 133.5, 134.333333, 136.0, 137.166667, 138.5, 139.833333, 140.833333, 140.25, 142.25, 144.25, 142.0, 127.5, 124.0, 131.0, 136.0, 154.0 };
    int b = 1; double min = 1000;
    for (int i = 0; i < lons.Length; i++) { double d = Math.Abs(lon - lons[i]); if (d < min) { min = d; b = i + 1; } }
    return b;
  }

  // 表を展開する（初回だけ）。cells: 6桁（2次）または8桁（3次）→ 系の1文字（'?' はあいまい）、candidates: 8桁 → 候補の文字列
  private static Dictionary<string, char> zoneCells;
  private static Dictionary<string, string> zoneCandidates;
  private static void LoadZoneTable(out Dictionary<string, char> cells, out Dictionary<string, string> candidates) {
    if (zoneCells == null) {
      var c = new Dictionary<string, char>(); var a = new Dictionary<string, string>();
      string text;
      using (var ms = new MemoryStream(Convert.FromBase64String(ZoneTableB64)))
      using (var gz = new GZipStream(ms, CompressionMode.Decompress))
      using (var sr = new StreamReader(gz, Encoding.UTF8)) text = sr.ReadToEnd();
      bool amb = false;
      foreach (string line in text.Split('\n')) {
        if (line.Length == 0) continue;
        if (line == "#") { amb = true; continue; }
        if (amb) { a[line.Substring(0, 8)] = line.Substring(9); continue; }
        string k = line.Substring(0, 6);
        if (line[6] != '*') { c[k] = line[6]; continue; }
        for (int i = 0; i < 100; i++) if (line[7 + i] != '-') c[k + (i / 10) + (i % 10)] = line[7 + i];
      }
      zoneCandidates = a; zoneCells = c;
    }
    cells = zoneCells; candidates = zoneCandidates;
  }

  // <zone-table> この部分は tools/build_zone_table.ps1 が作る。手で直さない
  // 3次メッシュ → 系 の表。総務省統計局「市区町村別メッシュ・コード一覧」（令和2年10月1日現在）を加工して作成。gzip + Base64
  // 陸の3次メッシュ 388757、あいまい 3359、2次メッシュ 4888（系が1つ 4571、混ざる 317）
  private const string ZoneTableB64 =
    "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" +
    "eYNun17Rez5vYva8YpbYWNhY2FjYWNi4jVdfyQvNpRNuGXgOS+/a1mUnto+y8ktlvS35LG+LyFXxMfKecZoxm2XVy2svPd/bdtp7vazzZo39x9coC193fN3x" +
    "dcfXHV/3w9d3ce13mSroQ7n+qv9j3On0s53c7cPG5fGAGrRBE1pQ5A15Q96Qpx9f6McX+vGlId+Qb8jTzy30c4sj58g5cu7QgKJH37MEeoFeoBfoBXqBHrm6" +
    "JHqJXqKX6CV6Sb0SffJoKfQL/UK/0C/0C/1Cv/rH/dLNQPm8Q+sH6Plcj7I6NnZs7NjYsbFjI21joW1sTedlvukd0hLvhvUvX3tZK+1vfRzfavAtyEZpZ6vB" +
    "N/i0p5X2tLaC3welHa20o9V57jynvay0l+PvhxyU5/k4676t2t6h5Q4t79CWgD/ok9bdWxs5rt134xplGTSgCcWmxKbabTo1n88Lrd97jbKwqbCpsKmwqbCp" +
    "P16++t7nV9EFr8pSFjZ12kKnLXRs7NjYsbEfNu5/uXzQPKlB26AFv+DXyfdBO887zzvPj/Kd73qc73qc73qc73ac73ac73ac73ac73Cc73Cc71Kc71L8/C7l" +
    "gy8IXp/S3n89/jkaZTk0oCe/oH1Qw0b7R2f8t9+FnCgFjbL41uHX59EfflEzruvDWP1m9tV3tKMs/GD4wU4b8IPhh4Yfmn3pW5HXv344NZ6fnmafP9sZZe17" +
    "rO++u3z1JfBH6CjtQOfL36Hph4YfGn5o+KHhh4YfHD84bfb4dsw5u3fOrp2za+fs2jm7ds6unbNr5+x6p8fZ55fO5V6j1ydi7PweGyrsjr9qKkdZho2GjYaN" +
    "tIWgLYSd8thIWxjn4875uHM+7pyPO+fjzvn4TuvfO2eRs6dR1psz5PlJw+Ue0PPy41N2+a/Nurtd83kdZTn1durt1Nupt8963/1645PPu76GflDv44zP+TbA" +
    "+TbA+TZgp+N5Ph7/aA/wF6dv89zkOpd1vpF3vpF3vpF3zr2dc2/n3Ns5994/I3r8MhduToxv5J436P3TURY2GjYaNho2GjbaKY+N7fFy//b35fyT05FRFjY2" +
    "bGzY2LCxYWM75bHxOFdwvvV3vvV3vvV3zoed8+H9EyrkA/lAPpA/21DyPHme8AvaBx3nMM45jHMOc3yKtbfBa7+03u2cvkbXPnBde+PovOLJfvHQHWUFNKEF" +
    "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" +
    "v+Fvw9+GXw2/Gv40/Gn4zfCb4TfDb4bfDP8Y/jH8Y/jH8I/hB8MPhh8MPxh+MOpr1Neor1FfO+q77HMMaEIL2gc1+Abf4DfuG/fOvXMf3Af3yX1yX5Rb3B9+" +
    "XsizhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8" +
    "W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFvIs4U8W8izhTxbyLOFPFv2OTvUoA3q0KFvxNmIsxFnI85m" +
    "p9wox4irEVcjrkZcjbgacTXiaMTRiKMRRyOORhyNuBlxM+JmxM2Im8UpX1DqTdyMuBlxM+JmxM2ImxE3I25G3Iy4GXEz4mbEzYibETcjbkbcjLgZcTPiRn+x" +
    "/5M9UPSPuK30F+s+hkMLCr/D7/CHHvm+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S" +
    "7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S7yv5vpLvK/m+ku8r+b6S" +
    "7yv5vpLvK/m+ku8r+b7ThBYUfeJvxN+IvxF/I/5G/I34G/E34m/E34i/EX8j/kb8jfgb8Tfib8TfiL8RfyP+RvyN+BvxN+JvxN+IvxF/I/5G/I34G/E34m/E" +
    "34i/EX8j/kb8jfgb8Tfib8TfiL8RfyP+RvyN+BvxN+JvxN+IvxF/I/5G/I34029EI/6NODbi0fBrwz9tr2c9xjzloH1QgzZoQBO61+OgAS0ozzv3/bgf/dFB" +
    "DdqgDg1oQguKvqFv6Bv6hr6hb+gb+oZ+Q7+h39Bv6Df0G/oN/Ya+o+/oO/qOvqPv6Dv6jn6gH+gH+oF+oB/oB/qBfqKf6Cf6iX6in+gn+ol+oV/oF/qFfqFf" +
    "6Bf6hX5Hv6Pf0e/od/Q7+h194m/E34i/EX8j/kb8jfgb8Tfib8TfiL8RfyP+RvyN+BvxN+JvxN+IvxF/I/5G/I34G/E34m7E3Yi7EXcj7kbcjbgb8TbibcTb" +
    "iLcRbyPeRryNOBtxNuJsxNmIsxFnI65GXI24GnE14mrEz4ifEb+jPykj7408NfLUyFMjT408NfLUyFMjT408NfLUyFMjT408NfLUyFMjT428NPLSyEsjL428" +
    "NPLSyEMjD408NPLQyEMjD428M/LOyDsj74y8M/LOyDcj34x8M/LNyDcj34x8M/LLyC8jv4z8MvLLyC8jn4y8MfLGyBsjb4y8MfLGyAsjL4y8MPLCaPdGuzfa" +
    "vdHujfZttG+jfRvt22i/Rvs12q/RPo32abRPoz0a7dFoj0Z7NNqh0Q6Ndmi0w/3fV67/HtTaoO2kDu2D+kH39go16O6/Rntse3scfOP5eC/trtHu2j4+DH5D" +
    "riE3yqW9tX0cGPeOnCPnyAXPg+fB8zifBzSh1CPRS/QSvUQv0Uv0sqDoF/qFfqFf6Bf6hX6hX+h39Dv6Hf2Ofke/o9/R70N/tNNGO22000Y7bbTTRjtttM+2" +
    "95NDP086yjfqY9THq/110G7Q/f74e+H/PaidNKB90Aa/wW8F5bnDd/gOP7gP7pP75L64L+4794c/nHbptMvj7/hCHRrQhBYUfUPf0Df0DX1D39A39Bt6Db2G" +
    "XkPPee48d557/+ugwfPgefA8+su+q/zXcQF+XOivX6Hn80T/3TeeB9quHQ60Q9B/n8OGxIbEhsSGpI652fLfU2Oi/57o+d/3vPdPnxdvs+UOHWUVthS2FLYU" +
    "/iziUMShsLH6y1XOZc9/J/qm6yirY2PHxo6NHRs7NnZs7NhIW93GgKOuWy4Omic1aBu0Hi9/XdfzDO1fI5oD/Rf26cP36HmiA17OFjTKouw6y3ZoQUd73caT" +
    "l7dvk8j+97t8P8oyqf/d9Zzogv8VJB67Q8BRFvXu1LsHNKH4oQ8/NCso9y2gCeV547k3qEORd+QdefqFrSeGohfoBXqBXqC39Rcf++oj912Oej7/PtHff1+s" +
    "vy/W30dZtNdGe22015bYmNiY2Jj1+7b9/vUcZeGH3ey/j6zZwUB/Hwj6998X+PsCii741/M97yir8AO508idRu60wg+FH0r88N73f12+V97zpBf6W+OhdQVg" +
    "Wx+0Y2PHRtp5o5032nmjnbc+bMRX5yv/vliKTpfeeujWpxcaZQ0b/TFs9IdBh41u8A2+teG/H69Gxm9AxxB6HNj+vX/ftJfVKLu974OuWDxnBP5+h852Nz04" +
    "vXX64a9X7cnb/m3f4aT9uJknP378/Rb9eM/78eNLaHy/tZflj7ucvGuVswW+aW1v291dXUdZ9nLXLr77GmW1QWOv3/Tz13L+D8sMg55l+6D5gPI8eZ48L54X" +
    "z4vndT6PQTtyHbmOXEeun3K50WBNd/xdr50yNw7mxsHcOJgbB3PjYG4czI2DuXEwJw7mxMGcOJgTB3PiYE68U/Qa8g35hnxDviHfkHfkHDlHzpHza473/ZfM" +
    "W46yAlsCWwJbAltCbLmb83zbPChYkwZr0mBNGszPgzVpMFcO5srBXDmYKwdz5WANGsxbg3lrMG8N5q2xrzGhtteVjuCo0kQfzj7n00vjeYdGDzPKalCHBjSh" +
    "Be2D2uN72sRcw9ysV56U9c4P7+q8aQ/0PEcJ6UlvOty7a5SFHww/GH4w/GD4wfBDu/xwNy+/Q9ca7Xnw+ED61eri/ax5lGVQbGzY2LCxYWPDxoaNvtt4v4qZ" +
    "6Hk9PQfZawS6PHSH3vjPsdGx0bHRsdGx0bHRsTFux0W5dGT8rC7va3XCURY2BjYGNgY2BjYGNgY25oj1Xe4dvOcZ1udEf5ASo6x9/jOXB1csftkfXGvLC6md" +
    "g7fvP1yfmB87FaOs9i+uE2ZLGWXh68TXia8TXye+LvrAMug+R/so7ptnrpJO9OPiXDPOY8r540TnBs8FR1nYWNhY2FjYWNjYaT8d+Y58R74j34d8O+bbOzVo" +
    "gzo0oPkt8Xi3unyea8pzvbKXVS93a6+vrie+OocbZfVBDT8YfqDPbfS5jT632fDD82aWP+vyl6yZP0Oy0hM0yiooNjZspM9t9LmNPrfR57Y2Y/X8uHv8R9co" +
    "q16mRzVq13z7w7XO7Wrx5ukoCz84fqBfb/TrjX690a83/0dt9qv+eo6ytrXy83fXGB/vNdzpjrLwQ+AHxo7G2NEYOxpjR4u82Wv4pH4fD7C3a8NR1i/aw4fo" +
    "969RFn5I/JD4IfED/XqjX295lxe3GfKPsmaUpX64uz7eXfoqGmXhh8IPhR8KPzB2NMaOVt+TF5/tVo2y7vPi47z//ZYxysIPHT90/MCY2BgTG2Ni65/0kx+3" +
    "95s638nRP3T68j5s9McDatAGdWhAc1BD3pA35A15Q95O+VGeN/Qaeg29hl5Dr6HXTj3sdPQdfUff0Xf0HX1Hnz7KA/1AP9AP9AP9QD/QJ7fHvkmwbxLsmwT7" +
    "JjtFP9FP9MmJsa8S7KsE+yrBvkqwr7JT9At92tLYbwn2W4L9lmC/JdhviXO/Zf+HbKBDP/Bf4L/Af4H/Av8F/gv8F/gv8F/gv8B/gf8C/wX+C/wX+C/wX+C/" +
    "wH+B/wL/Bf4L/Bf4L/Bf4L/Af4H/Av8F/gv8F/gv8F/gv8B/gf8C/wX+C/wX+C/xX8ZJx3uSemWe9w3q0PHexP7E/sT+xP7E/sTOxM7EzsTO7KdcQnc7k/2z" +
    "ZP8s2T9L9iySPYtkzyLZs0j2LJK1fbK2T9b2ydo+Wdsna/tknZ2ss5N1drLOTtbZyTo7WfMma95kzZuseZM1b7LmTdafyfozWX8m689k/ZmsP5PzyWQNlayh" +
    "kjVUsoZK1lDJOiZZxyTrmGQdk6xjknVLsm5J1i3JuiVZtyTrlmTdkqxbknVLvl633Izl70+7rjn8zY7++73tc195lPXZuuV7rlFWH9Twg+EH2lOjPTXa0926" +
    "RU6Pbk74Xq1MvjwfGmV9Nvf5+LqfGb1fT4yy8EPDDw0/kCeNPGnkSSNP9nXLmzhKlOea9Pm8X5/doFsfjbL6V/zx5LxnQ+dp2njnBW73v96U5fjB8QP538j/" +
    "Rv438r+R/438H2uKZE2RrCmSNUWypkjOnJMz552in+gn+vQPjf6h0T80+odG/9DoH8YcNpnDJnPYZA6bzGGTc9Sdok+/MeZ+ydwvmfslc79k7pecce4UffqV" +
    "MS9L5mXJvCyZlyXzsmRettOCjnY45mnJPC2ZpyXztGSelszTknnaTtFvj8/WDbfrAcmOv+WU+/mrfB5lYWPDxoaNDRsbNjZsbNio54Xfvqvwuj2/OS+8W01/" +
    "2Ef8dXdWfbMjMcrCD44fHD84fnD84PiBXNnmti+frdeeJDhR0d7i7vplX7LNn7/ct355x+HGl6Ms/BD4IfBD4IfAD+S8k/O+792+isVTToeft+PKJ/74dftI" +
    "/xN/nJ+CvOn5/3rOHvaurPhH45pY8ovMHdcoC18nvk58Tf/o9I9O/+j0j9t65uUcwZip3O5U3iGZD3yKRlnYWNhY2Egf7PTBTh/s9MHbmunlr6vIt+jaG519" +
    "15fnLK96u9HHbWv874mZRu/5Do2y8EMffgjGkmAsCcaSYCwJxpJgLAnGkmAsCcaSYCwJxpJgLAnGkmAsCcaSYCyJhj79fNDPB/180M8H/XzQzwf9/Fi7JmvX" +
    "ZO2arF2TtWuydk3WrsnaNVm7JmvXZO2arF2TtWuydk3WrsnaNVm7JmvXZO2arF2TtWuydk3WrsnaNVm7JmvXZO2arF2TtWuydk3WrsnaNVm7JmvXZO2arF2T" +
    "tWuydk3WrsnaNTfzH1CDNmhAE1pQ9Ih7Evck7kl8k/gm8U3im8Q3iVsSt/STT7nEJYlLEo8kHonfEz8nfk78mfgv6+RTD/yV+CvxT/bz+bC/8Evhl6K+RX2L" +
    "elU770d5RT2K9lfYW7Sfov0U9hbtovLkD78V9hbxriPexRq9WKMXa/RiLV6sxYu1eLHmLtbcxZq7WFsXa+tibV2srYu1dLGWLtbSxVq6WEsXa+hiDV2soYu1" +
    "crFWLtbGxdq4WBsXa+NibVysjYu18U4TWlD0DX1D39A39A19/NLwS8MvYy1WrMWKtVixFivWYsVarPg+tvg+tljDFGuYYg1TrGGKNUyxhinWMMUapljDFGuY" +
    "Yg1TrGGKNUyxhinWMMUapljDFGuYYg1TrGGKNUyxhinWMMUapljDFGuYYg1TrGGKNUyxhinWMMUapljDFGuYYg1TrGGKNUyxhinWMMUapviGsviGsviGcqcO" +
    "DWi+jDGTcVPWC/sU9eunlRe63TsfZdXfg45cc9qY08acNua0MaeNuX1ydvVNpzejrD/Zt/h0JfIOjbLwA7ni5IqTK06uOLni5Iof+xbjTfvHAU++49zQj++8" +
    "jrLIQycPnTx08tDJQyffnHxz8s3JNyefnHxy8mPMj4r5UTE/KuY/xfynmP8U859iXC3G1WIcLcbRYhwtxtFiHC3G0Z0ib8gTizGOFuPoTnneeO48d547/OA+" +
    "xv0YvzbqJzXokKuAH/Dj5Ds0Bk3kErlELpHLU66glF/oFXqMb9UH7by310mHXO/c9/N+L+/4e0F/7XSMi51xsTMedsbDznjYGQ8742FnPOyMh53xsDMOdsbB" +
    "zjjY+e1LZzzs7Cl3xsPOeNgZDzu/V+n8XqWzt9zZW+6Ml53x8vg7QlD0OvId+Y489R7jaWc87YynnfG0M552xtPOeNoZRzvjaGcc7YyjnXG0M452xtHO+NkZ" +
    "PzvjZ2f87IyfnfGzM152xsvOeNkZLzvjZWdc7IyLnXGxMy52xsXO+NcZ/zrjX2f864xvnfGtM751xq3OuNUZt/o2jba/B21QH/TIz53y3Hhu5/MYtMFv8NvJ" +
    "z0EdvsP3k1/QPmjwPHgePA+eJ+9J5BK5RC6RK/gFv+B37vu4X2LUb8mTjnouxX2d9w06yl86zzvPycOln88DmtC93OPvRf11UIO2Qe1ovwvz24U8Xsjj4+8O" +
    "QXluPOc9xnsa7xl5sNC+ly2eo/x+9LsbtUGXo5/eqUMDmtCCIm/IGXKGnCF3vrfxvPG88Xzvp/+P/8Ta1i3m6/9YA4fgFFyLYOH3EtwFi/ySgkV+DcGXTNZ6" +
    "vX9bJrngmNiEb8Jvgl1xCq6JQ3CKTAq/hN8FLw/BJrgJFvlV5FeRX0V+lXqtYv/0zzZkleAueBG8Tmyia6Jromui20TeBUcIFn4KvxSLTBd+F/4ieBWZWd9t" +
    "6HXBMbE1wSLThO8mWPjxECwyKfxSLDJd8CJ4VUxZ20LiUSW4C14ErxN3ke8iv4TgFCzyZ/s58JS3mDLbnEFwE+yCRTdTsLzn9M+Br3fu5/sTWxMs/PYQLLpN" +
    "5P2QyUdbH/Vogn1i7xPHMnEXPNoSuE68TX145+7xLRbNB977mQtv9R14W1Fu/aHHf9K2hUeZ4LYIFr6f/G11EC44LrzNAyfe2urEIiO6prpbTC+cl8z+jzT/" +
    "jyd4UVwTrxPXI/8nTrz1pSfe+96JhV9Tvq/9xNsU/iprwzWxCb8JdsUiH8IP4afwS/DWt0ws/EX4W15MPPnbOHza37e1/8nfsE9sJlhkmvC3tjqx8EP4qVhk" +
    "Svgl/C54EbwqPu3ZlnLb+BIXthPX4zHwNsfd/+mSQ/7AITgFl+AtjyYW/iL8RfjrxP4wwW1iewgWmSZ8v7C1xyJ4ndgEN8EuOASn4C54kfcvwt/a9sRTRm07" +
    "Y3rgEtwvvC1RSnCf2IRPXgwsfBd+hGDhp/DLBQu/C38RvCq+5LdQCDbBTbALDhcs/BR+Cr8Ed8U58SJ4vfC+Fp14eQhWfhPsgkOwvHMpwV3wMvEq71+Vf7WZ" +
    "fV078fTbvm6duLlgkXHhRxMs/DTBIlPCn21vX19OLL7ahsIT57bmEWwTm+Am2AWH4BRcitvEXbFPvCiOiVfFlw/LHoJN8PRnWRO+C3/6tmz6tixEJkUmRaaE" +
    "X8LviuU9i2KxZ7a3bSqhuAteBF9trNvsA3ubsduwCW4Tm8iYyJjKuOCYuAl2wSHyIfwUfgq/BHeR6cJfhH/1S9tScuvDs8DbfGziLngRLPJbuROnYHnPZs/E" +
    "It9FfhGZRWRW4a8nf58utoldcCh2waKbinPiEtwV18SL8Bfhrxe2fW52YUvBwm/Cb8J34YfgabOJzduY6IJFpgt/EXz5c/vvIdgEN8UxsQsOwak4Jy7BXWS6" +
    "8BcpaxEZtXPGfTNnEbxOfLXVHYtMCb8rFplF8DrxtpAQnIJL8HynxNol1hsWma2fn7gJdsFSbpN3bvOiieU9Lu9xeU+IfFzysc/3LmyCm2AvwV2wyITIpGKR" +
    "T5G/+o3jG0nBIlMi0yfe53sXXhTPeu3z9okvnxzj2sQluAu+bEixOcXmbDPXUuxPsT9bb4JFfrb/bLP9b7gEyzsXec8i71lEZuZI7muBiafuPv+fWPhN+LNd" +
    "pbSrlPaT0n5yn5NPfPHrMfvGesyc3bDyr/puc4wQnIKXiU1kTGRMZdaJm8g3kXfFYkMIP12wvCdFpppgke/CXxSLzIxXtRmjDQvfHoJN8CXfH9NvGy7BfeKp" +
    "2x+qO/v5Dct7THUXwevE7SFY3u/Cn21mm0c1wbNcM5ERO+2ysyK3+dj+L7jveKv8xC54K2ti4afwS3AXvAheL5zR14m3cWFi4a/Cn3ZmPhbBwjfhb2P9xF2w" +
    "yLjIbO1zYuFvbXXiECzyJTKz7hs2wU2wyC8iv4j8IvKr8NeLX16KXXAITsE18bRtw/KeLu/p8p6uun3iaX/5Iu9cRH5R+UXwOvEq75n13ZrelN8WzIKFP2O9" +
    "fxAzsQs/QnAKFpkUGfFniD9D/BPinxA/xCIyi8hIHUPrKDGN9dLt+1hzYRPcBLvgEJyK28QluAteFIsNq/DFthDb4nHK7F3U8j99AZvgpnid2AWH4BRcolvC" +
    "78JfBG/zjYkvvu1zyImFb4KbYBcciteJU3AJ7iLfhb8IXife28+F1wvvf1hZsAluE5tiFxyCa+Im/G3snlhl+sQuOBQvgteJU/jTP23rrCcuxbNee5954UXw" +
    "euFtWqS4TWyCm2BX7IJj4lCcE6fgUlwTd8GLyCzCXy+8f7M0sQluivvELjgULxOn4FK8Tjzb4TFGX3hRbBOvii8fbqGziU2xyMx2uE2qFMfELnwXfgg/FYtM" +
    "Cb+UnxN34Xfl18SL8Bfhr8KfsTvGlIn7xCH8UP4ycYpMKn+deNq/T6om7sJfQrDwVxc8ZSTv9kmS4DZxKe6CF8HTzn0+M7ELlnLFZsmRDcv7pS6SOyW5U/tZ" +
    "+cQpWGRMZExkmvCb8F1wmOAm2AXLe0J1xQbxrbTVDYt8qvz0Q83+sGr2h/s/XDixKY6Jm2BXnBOH8EP4KfwS3BWL/KK4BPeJV8Vn+1mqb+P+soJd8JYjEws/" +
    "hZ/CL+F3wYvgzYaJL37fz0knFr4J34TfBLvideLL5jX3byQm7oKXibfcmdgFh+AULO/s8p5F3rPIe9aLX4+H4G09OLEJFpl2yRxrzAun4BLcBS+KbeJ14H1j" +
    "rXEefWAT3BTHxK44Jw7BKbgEd8GL4PXCZpylHrgWwcLvXbDILMJfJz7PHAeeZZ3nEQduTbALFhnOpwYWec7iBxaZvPjNOKsduE9swm8pWPgufBd+CE7F8v5c" +
    "BF8+bK0egk+b9z+lP/YrBo6JTfhNsAsOwalY3lPCL+UvgteJu8h3kWfPcGDR7aK7iDx7dAOXYHnPKjLrJdNqWSdeJxaftG4hOAWX4D5xE92m/GViFxkXGVcZ" +
    "sSdEPoSfTbDIlAkWGfacBxaZ5eJ7PRbB68QmfBN+E+yCQ3AKLsFd8CL4ikU+9j7wwl2xTbwoboJ94lX4q/LP+O7H2i5Y+Cb8plhkXHCITAg/hX/FZcfC78Lv" +
    "wl8EryIz69IejyZY+Ca4CXaRd+HHQ7AJPuXrsZ+17XsRBzbBrQkWvgvfhR/CD+XHxKk4Jy7BXWS68Bd55yIy68U3fzTBwjfBTbArjolD+CH8FH5JWSV89gkH" +
    "Fhn2uwYWmVX468XfhrUueJl4+q3tZ1sTi3yKfIl8iXyJfKn8OnEX3S78RfjrxOf+24FNcZu4Cb8J3x+CRcZFJkQmBdeFfZ8/XNgEN8EueLbb41umiUOwyKfI" +
    "l+AueBG8Kr7ev/8uZmIT3AS74pg4BKfinLgEd8U18SL8Rfir8NeLn+LbFN+m+DZbE3lX3AUvE4fideKc2B+CTXALwSm4C14Ei+5sVyn9QEo/sPeVgkuwvF/q" +
    "cu4nDyzvn201z33mHe97lRc2xSLThN+E74qbYBccglPwrEuIzSE2n/v8Ay+Cpw8jRWb2Pxmz/8mcObvhJtgFh+AUPN+f4sMUH2bKO1PemfLOlHemvnPavO+p" +
    "TizvLJWRes1+MkvsKbGnxJ6afVSWlFUlMl1kush0kVkEz/6k6qE4JjbFKbgmboJdcIh8CD+FX4K74EXwKjbMvqXnzPGei+B1ETz5Ncf3LvXtUt/jtyEXbopF" +
    "3oXvwp9ttdfMi14h8ikyJbgLvsb6fUtg7Occ2ASzXz2w8F34LvwQfgpmz3Ng4Xfhd+XLO9nfPvAi8uvkJ/veB14Erxe2eKwTzzpuWPizvib1NamvSR3t3Ac+" +
    "cCqWd5bgabOdZyUH7oIXwdP+JvY3sXmbGgiedjaJy4ZFJkqwyIfIs7c5sMiX8LvgRfA6cT6m/dvq4cR+nnMN7IJDcE7MecTAotuE74JDcCoW3VIsNnTBi+BV" +
    "5NeLHz79H+6Cpz/jPFs8cAo/hV/CL+HPvAjvIrMIf534PE8cuE08/R/iz/1vC50424x1tpm/xzddE68Tl8iXyJfIl8jPNnN8u3XhRfiL8NeJ/aF4luUWglOw" +
    "yDcXLPKzzaR7EyzyITL5ECz8mcvVpp3VTHEXvAheJ26CXXAITsHTt6U2+IzLhme53kWmm+AmOAXLe7q+Z9q/f2MwsQsOwfLORXTXC3d/KF4nNsHTP13abZd+" +
    "oEs/sCfnxCE4FfvEJbgLXhSH4Jx4VXz6bXmcZxwHLsGcCwycE7PnP7DILCpTgrvgRbCUtco71+udtu/NXtgEN8GuOCcOxTVxKu6Cl4lL8Tpxv/A2JAq2RbDw" +
    "m/BdcAieNqTYkL2E3wUvgtcSfPErp58rp58r18mvpnjK1PTncV45cQnuguU9Lu8JFyzvDNFNkSnB3QQ3wSLDuc/AIr9M+f4wwSl41qXbQ7DIm7zHXHAIlnc2" +
    "eU8T3Sa6TXTbpdvzsUxsiteJm/Cb8F3407c9Q/gp/CrBwu/C78JfxM5FZBaRmTm74VN+fWyXYJvYhN8Ui4wL34Ufwk/BJbgLvtrJaiJjImOPq72t9rja24YX" +
    "xT7x1V+tx97yxCE4JzaRMZExlSnBfeKmeJnYBYfideIUfgq/hF/C74LXiW36qtkieL2wP67+cMMmOASn4BLcBS+Cpw1usy25meAm2AWH4BLcBU/7Q2wO8XmI" +
    "zzcsMlffuOMSLLouuld/uOMQLO/JJljkywSLTBf+InVZhC91FB+G+DDPPN1Puc7vnA9sgptgFxyCU3ApXifugtnjHdgFh+AUXBOvorsqvwsWG9ZZ7rnPcOBF" +
    "sU28Cn+9+MdvmSe+3mnn+cvAJbhP3EOwyHeVWQTL+6d/zBd5zyLyi8hP/9h5vjOwyIv90QTP+Nq+Nr9wCD9LcBcsMuKHmD7fsAlugkNwCpb3LKK7iK74J8Q/" +
    "+WiCZ13SHoJNsMi3KVOPCx+/Q594EbxObCLTSrDwXfghOAWX4OmT1rrwF+GvIXjyXew/z5gGboIvH7rlVRdvwm+PmHj6bf+YQ7DITx/uP9qf2IXvwg/h54n3" +
    "PcCxZhm4T2yCm2BXvEwcwk/FIlOCu+J14kX4i/BXsXOV968iv17ydu4jDVwTs28wsMg04bvgEJyCS3BXLO9cFIsNq+KrLk3838T/Tfzfzm9WQ/b9DhwpWGRS" +
    "+Cn8En4JvyuW9y+C1ebpfz/3kwdeJzbBbeJkLX9gFxyKbeIUXIrbxF34XfiL4FVk1osfYltI245zH/XA07chbT7OfdSBRT5FPkW+hF/C74rlnYvg6fM4v58/" +
    "sD0EC3/6OSVGKTHKc8/2wKbYJpb3ZBO+Cw7FbeIUXIK74EXwqthPXPkQbIpDcAquiZtgFxyiG6IbIpMikyJTgrvgRbG8Z1V8xXpbqypeBK8TN8EuOATnxDVj" +
    "2ssEN8EpuAR3wYvg9cTLNqcN5rcbZl0/sAsWGdahA4u8PwSLTAg/hJ+CS3AXvAhe5T3rxT/Oay7Mmn3gRbDIdJFZBLNfNLDy53vyWgssxxxp4mmP+MfEDyZ+" +
    "MPGDZQo/hV8Xv8WjBHfBi+B1YhN5E3kXmRRcgru8swt/Ef4i/OmfJv5p4p+WTbALDsEpeNbdpY77H82ZuAl2waH4stOlLJeyXHy+/7J84hJ+Cb8Lvwt/Ef4i" +
    "/PXC4UsJ7oIXwevEq8ivkx+zrIhZVsTiE898iVhFflWZEDzfnw8TPN8v7TykrwjpB/a/yzxxCE7RTeGX4C4yXfhSX+kHQvqB9NmPpbO/NHBNPG1On/3bcQ51" +
    "4SYyLjikrBB+Ci6RKeF34XfhL8LXusw2kyH1CqmX9AkpfUJKvqT0CSm5mRLf8hnfEv+U+KHED7Wfr104hB/CT8GlOCbugheRWYS/Cn+21S6x7hK77pYTN+E3" +
    "4bvimjgEp+BS3AWvE3fhz/6z++w/9z+4LNgmXhS3iWkDW4N5nHOAA5vgJtgFh+AUXIK74EXwemE75ycHLsFdMHPagYW/Tr7Yb2K/if1WzEUP7MIPwSl41qWd" +
    "c60Dm+Am2AWHYObzAwu/hN8FL4LXC/s59zuwCWa+OrDwOadO+d3rwClYdJmHDyy6KfwywcKfsb7WVps/9j+cShvbcZvYhN8Eu2KRp084cCgOwSm4BHfBi+B1" +
    "4hSZFJkUmZr4nD+kzC1T5pYDN8EueNqci+IUXIKnbblOXOtDsAlugq9yLR9d8DqxCd8WwVOmHg/B851likNwTtwU18QuOASn4BLdEn4Xfld+F7wIlrosoruI" +
    "7iK6y5Tvy6x7XxWb4CbYBV8+aed5+oGn31o34Tfhu+AQnIJLsbynuuBl4i4y04etTx+23kW3i+4ieBWZ9eJ7n23Mz99aHtiE3wS74BCcgktwVyzvv+K7n2c9" +
    "Jl4Um+Am2CdeFceFrS2CZ1nmwnfhX33Revyt44m7YNEN0U0pN+U9Ke9JeU/Ke1LeU/KeEn4XvvjNFuGLH0z80GL6s6XgEiz+b+L/duXOGo8rF3acE5viEtwn" +
    "boJdcCheJ07hp/BL+FeO7Fhk+pSxa/zasQue8naNazsWeROZ9hAs8k1kXGRc+CH8FNxFZhG8TtweU77J+yWmITENiWlITFPqm3aNFzsWfhO+Cw7FIp+CS3AX" +
    "vAie9UppYyltrMTOEttKbCvx+fF3zCYuwV2w6IbIh8iEyMxcLsnlkvw9/tbZxCLfhb8InrnZpR12mznVzRTnxE2wK66JQ3GfOAWX4C54EcwYsf+hJ3soXiY2" +
    "wU2wCw7F68Qp/JL3l/AZ1wYWmS4yi8jw/czAF9/sIdgEN8V9Yle8CF4nnnWxMx8HNsFt4hCZEJlQGRccglPwtLmlyKTIpMp0wYtgsb+EX5Pv+RBsgqfN5z7G" +
    "gbvgRfCq+KpjO/cWBlZ+F7xMbCJjITgFl2B5j+l71omb6M720ELsOfdYBk7BIm8PwSa4CZZ3ip2h5c52eJwDTiz8EBtCZFL4JbgLXgTPfHGpl8f0rYcJv7lg" +
    "kXHhu/BD+CH8FH4pFpku/C78RbHYNvsBj1Xx5bf/r6l7S3AQ1aIwPKWwr/B4Tvf8x9QFKut/+7JrSRRRo7FMxI+e8oCNXrLDAWs7iu869rHDCRc95KZNnrC2" +
    "o8wf7fKAjQ7Z6ZSDLjnplgtuesJLPp8B/ve+OCce9wX+sh8PJaf8NyRkZJqZghte8t9HDhltTuT/TpdkTLsw7d8ppGww2lxoZ6mdfduzjLqh7g2jHqhnwagX" +
    "6v2DB2ywwwEnjDbRh/tWdhn5hfq69XouZdwXEy/Ojue+SL64Q7CeSx7fH/YYvw4Yb3j2r/cFp95H6Gu0FKjvI9816ntLvkamUZ/wuu5z5f31ufL+edBDNtjh" +
    "gBMuuOEJL/pdrtj/MPgOo+MJL3kpM76hcDzkRReMadeEb/u5zyrlglseP3jABqMdQ90cDhh5R8aV2b9UIRvsMPMJF6zlHUM2LNe+A0zW++5PmbLmYX+avE64" +
    "aM2nNeqN+kT7Exmt033lRo6GWddyZSJTCReMTAeM/ER9yYX1W1i/hb4q9FU5M2gnfjCmRd/u/9C4TmTQ5/s/NGS0nwkX3LD6cP+3toz2sR73r4hdNzxhbWu1" +
    "rybIQx6wwQ4HnHDBDU9Y46e8l6z5PE8Zvta+ohzzH9rnVKD90Fg9T/WVG1b7gfmJhbzGVcVKuGC0yXnTfqxS+4FzcJPVZhrsyDjqgXrSyBTqhXr/4AEb7DDa" +
    "QT8n+jbRt/tJT9cDNtjhgBMuuGn1Z2NZGsvSWJbGsjSWpbEsjTHTWq7GtnB+yeZ6wAY7HJg2WE+44IYnvOREm1rv+6tIGG0m2km0U2inMG/ah5xfyrlWf7br" +
    "mNv7jPraYIe1/9z/ug0HnHDBaCfRTmJ+Em0WMlhGxzLGe/xtX79z1vR5wAY7HLTDISdcdMqNeqM+UZ+oL7zXQmbdzDhnfZ8HHbLBTqOdgBMuuOGJdibqmk87" +
    "dw1+HnTKBjtd8JIDmUBmX+W8nrCm3XcMyAM22OWgNf+VdMKan/NN57Xm7Xy7ea353MdoGfO8rs8T9K4nvCasev9+8oCNHrLDASdcmLZQb9TndWDdBdZLYL2c" +
    "p2Bfq98i1W/n6djXjUwj08hMZBat/P71YlnTlraj2E+okZFXv0VpvxFlmFbbXZTGeZxv0D9rTAbGZKbmLXPABjsccMLqz0R/Jvow0Yd57lS4nvJCXn14frlB" +
    "nvCStS2fp2/LyGi7Pk/lvta2VpHIlMPKnLvWrg12mPmE9b6p7aW+c/DHaHOgzWebqv2Ik/0kzf//89nlogNOuOSmW570lNd17m/urwds9JId9YCTRr4aRqZR" +
    "n/C6Pr96K094yePzOE9AlgdsstEuOx1wygEnXHTJTbc8UV/X51eI5JYHPWWjl+zy/nb5OughJ1y0yQ1PeNFfH/59Wv3bb//z7+u/sSGj/jc2ZGQa9b++klFf" +
    "t34ulX7eTx+QldlPGZAdDhjTGvKGvCH/tx+Wkf/rZxmZRL3gppGf8EJm3XrtY/31mDDqhrrTyATqgXoW3DDyiXwh3/BMGPUV8M2cp6Rda311DdQNdYfV5+dp" +
    "aDIyiYzWxf55AblRn/BdF/sGoQEb7HDA3zI+T+uWG57wkg3TGqZ11APOgFEvh5Fpg5G543AbGS277WtEMutqZ1+rfH2eXHB9x+d5fLvsqDvqUXDDyGt5/4x8" +
    "oV6od8DITIeR0bo+d5J9HuoHH+qH8ySF17GfmHw9Boy6IW/IOOrvfib2vVHz2ecfr+v4jpXHAzbYP//N8c/hkAdsdMoOB5zIJ+qFeuN9G/WJ+kR93frYd3Fd" +
    "94RRfz9jPEZmob5URz8M9MNAP4x9t821wW4w2nFk3uPvY+QD+cB7BabNH4x26tbt+/xwPOgpG+qGuqPuqAecBSOfyJTBDgecMNostFlos9Fmo82J+rr2va1d" +
    "D9plg50OOTBtIBPIaL38ecCYNjFtId/wvD7buzzkQD1YNzlplwtueNIhL/qur/xpuzjfwV0P2JAx1pfsqHvBDU8Y0wamDUwbmFbj9nwHdz1+8ICZcdmQN9a1" +
    "jMPhoBPWfGJ/mCNRL0xbyDSN/KQbxrJP9dtYyCxkljL7DsvX5zsgecAmD9plg+94tvPkmuf4ZefJNdfv5//HyLyfIR8XjPz7Weux8vX7yYMesqFuqDvqASdc" +
    "13vAwSkP2OiCW3Y44IQLbnrKE17X5z+d5CUPGX1l6B9D/1i950rHTrscyAcyiXrBjfdq1CfqE/V17ZlLXnL9BmzyQH2gruV1LK9jPDjGg2M8OMaDY1kcy+JY" +
    "Fsey+Hd+8efAegmsl3Nd9DrhghvWGIh9Z9X1Ql19FVjvgX4ILHtg2RNjKTHP51d7ZdQNdljLlXiv5HthLCXGT2L8JPo50beJcVLot8K+qNCHhe2o0IeFPiz0" +
    "YaEPC+OtMN4K460w3gpjrLDsVY56oB6oa+w11kVjXTT6v9H/jf4/TzOUUU/Ui0am4YnMRP3tn/1TAd+8Hb/ngI9Rf/eBj5F5zwEfI/Oe9z1GplB/z/Ueo/6e" +
    "6z1GZsnfcSRxHEkcRxLHkcRxJHFcSBwXEseFxyUb3bKj7qgHPeWkl1zyt786blrz/21Hj01e9O1Dqx8d8qBTNtSN9YJbduQdeUfemZ9ywAkX3PSSp9x438b7" +
    "Nt63HflAPpDB/HzXTo8zYExbDiOjdWetdWfdBmPa+eX3w8foIQectMlFB5xyI3PnYRv5Zr7glic95UV/fejniZmfx91+dX0mcX3mMeqGutPIBG1y0i4X3PBE" +
    "fqK+0P7d1jww/4F5Pv9pdu00pr1jdbvhKQfy4XDACaPNYDtLbnjKFj854YIbnvCi7/Km/WiTB+1wwAmXbMgYMsZMw1N2esmBuvoqXX1ynjgvD9hghzWfXrTm" +
    "2ZsuuOEJa95C6yKjNT8xac1baNxW/GiHA055IDOQGcgY7HDASZdccMMTXtcdP7rlgfpA3WDN2/55BxiZQCZp5IvGtI16oz5Rn6gvesJ3XXdqWzv3hMsmL2QW" +
    "Mu96/1sp534q2eGCWx7IvOv9ccKYdmBaQ96QN2QcDnrKCRfc9JInvK7H91n3eMAGOxxwyt/n/OOih9y0yRPWerH6wQN+zuv/A7IvZSIUsgEA";
  // </zone-table>
  private float GetRawHeight(int c, int r, float[] grid, int nx, int ny) {
    if (c < 0 || c >= nx || r < 0 || r >= ny) return NoData;
    return grid[r * nx + c];
  }
}
