using System;
using System.Collections.Generic;
using System.Xml;
using System.Linq;
using System.Threading.Tasks;
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
        int mode,
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

      bool doClipping = (target_point != Point3d.Unset && size > 0);
      double half = size / 2.0;
      int step = Math.Max(1, sample_step);

      // クリッピング範囲にかからない区画は標高を読まずに飛ばす
      List<Tile> used = doClipping ? tiles.Where(t => TouchesClip(t, phi0_deg, lam0_deg, target_point, half)).ToList() : tiles;
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

          // mode 1, 2 では 6次展開(Exact) を使用
          if (mode == 0) LatLonToJGD_Fast(lat, lon, phi0_deg, lam0_deg, out pX, out pY);
          else LatLonToJGD_Exact(lat, lon, phi0_deg, lam0_deg, out pX, out pY);

          int idx = rIdx * nX + cIdx;
          if (!doClipping || (Math.Abs(pY - target_point.X) <= half && Math.Abs(pX - target_point.Y) <= half)) {
            float z = (mode == 2) ? GetBilinearHeight(c, r, heights, orig_nx, orig_ny) : GetRawHeight(c, r, heights, orig_nx, orig_ny);
            if (z > -9000) { verts[idx] = new Point3d(pY, pX, z); isValid[idx] = true; vType[idx] = types[r * orig_nx + c]; }
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

      log.Add(" Mode: " + (mode == 2 ? "High (6th Order + Bilinear)" : (mode == 1 ? "Standard (6th Order)" : "Fast (3rd Order)")));
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
    t.lLat = double.Parse(lcParts[0]); t.lLon = double.Parse(lcParts[1]);
    t.uLat = double.Parse(ucParts[0]); t.uLon = double.Parse(ucParts[1]);

    string[] hiParts = SplitSpaces(ReadNode(t, "gml:high"));
    t.nx = int.Parse(hiParts[0]) + 1;
    t.ny = int.Parse(hiParts[1]) + 1;

    int startX = 0, startY = 0;
    XmlNode sn = t.doc.SelectSingleNode(".//gml:startPoint", t.ns);
    if (sn != null) {
      string[] sParts = SplitSpaces(sn.InnerText);
      startX = int.Parse(sParts[0]); startY = int.Parse(sParts[1]);
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
      if (!float.TryParse(p.Length >= 2 ? p[1] : p[0], out z) || z <= -9000) continue;
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

  // 区画の四隅を投影した範囲がクリッピング範囲にかかるか
  private bool TouchesClip(Tile t, double phi0_deg, double lam0_deg, Point3d tp, double half) {
    double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
    foreach (double lat in new[] { t.lLat, t.uLat }) {
      foreach (double lon in new[] { t.lLon, t.uLon }) {
        double pX, pY;
        LatLonToJGD_Exact(lat, lon, phi0_deg, lam0_deg, out pX, out pY);
        minN = Math.Min(minN, pX); maxN = Math.Max(maxN, pX);
        minE = Math.Min(minE, pY); maxE = Math.Max(maxE, pY);
      }
    }
    const double margin = 1.0; // 区画の辺のわずかな曲がりの分
    return maxE + margin >= tp.X - half && minE - margin <= tp.X + half && maxN + margin >= tp.Y - half && minN - margin <= tp.Y + half;
  }

  // --- 高精度座標変換ロジック (国土地理院 測量計算規則 準拠) ---
  private void LatLonToJGD_Exact(double lat, double lon, double lat0, double lon0, out double X, out double Y) { ComputeJGD(lat, lon, lat0, lon0, 6, out X, out Y); }
  private void LatLonToJGD_Fast(double lat, double lon, double lat0, double lon0, out double X, out double Y) { ComputeJGD(lat, lon, lat0, lon0, 3, out X, out Y); }

  private void ComputeJGD(double lat_deg, double lon_deg, double phi0_deg, double lam0_deg, int order, out double X, out double Y) {
    double phi = lat_deg * Math.PI / 180.0; double lam = lon_deg * Math.PI / 180.0;
    double phi0 = phi0_deg * Math.PI / 180.0; double lam0 = lam0_deg * Math.PI / 180.0;

    double a = 6378137.0; double f = 1.0 / 298.257222101; double m0 = 0.9999;
    double n = f / (2.0 - f);
    double a_bar = (a / (1.0 + n)) * (1.0 + 0.25 * n * n + (1.0/64.0) * Math.Pow(n, 4) + (1.0/256.0) * Math.Pow(n, 6));

    // 6次展開係数
    double[] alpha = new double[7];
    alpha[1] = 1.0/2.0*n - 2.0/3.0*n*n + 5.0/16.0*Math.Pow(n,3) + 41.0/180.0*Math.Pow(n,4) - 127.0/288.0*Math.Pow(n,5) + 7891.0/37800.0*Math.Pow(n,6);
    alpha[2] = 13.0/48.0*n*n - 3.0/5.0*n*n*n + 557.0/1440.0*Math.Pow(n,4) + 281.0/630.0*Math.Pow(n,5) - 1983433.0/1935360.0*Math.Pow(n,6);
    alpha[3] = 61.0/240.0*Math.Pow(n,3) - 103.0/140.0*Math.Pow(n,4) + 15061.0/26880.0*Math.Pow(n,5) + 167603.0/181440.0*Math.Pow(n,6);
    alpha[4] = 49561.0/161280.0*Math.Pow(n,4) - 179.0/168.0*Math.Pow(n,5) + 6601661.0/7257600.0*Math.Pow(n,6);
    alpha[5] = 34729.0/80640.0*Math.Pow(n,5) - 3418889.0/1995840.0*Math.Pow(n,6);
    alpha[6] = 212378941.0/319334400.0*Math.Pow(n,6);

    double dlh = lam - lam0;
    double e_tmp = 2.0 * Math.Sqrt(n) / (1.0 + n);
    double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - e_tmp * Math.Atanh(e_tmp * Math.Sin(phi)));
    double xi_p = Math.Atan(t / Math.Cos(dlh));
    double eta_p = Math.Atanh(Math.Sin(dlh) / Math.Sqrt(1.0 + t * t));

    double sX = 0, sE = 0;
    for (int j = 1; j <= order; j++) {
      sX += alpha[j] * Math.Sin(2 * j * xi_p) * Math.Cosh(2 * j * eta_p);
      sE += alpha[j] * Math.Cos(2 * j * xi_p) * Math.Sinh(2 * j * eta_p);
    }
    X = a_bar * m0 * (xi_p + sX); Y = a_bar * m0 * (eta_p + sE);

    // 子午線弧長 S0 (高精度版)
    double A0 = 1.0 + 1.0/4.0*n*n + 1.0/64.0*Math.Pow(n,4) + 1.0/256.0*Math.Pow(n,6);
    double A1 = -3.0/2.0*(n - 1.0/8.0*Math.Pow(n,3) - 1.0/64.0*Math.Pow(n,5));
    double A2 = 15.0/16.0*(n*n - 1.0/4.0*Math.Pow(n,4) - 1.0/16.0*Math.Pow(n,6));
    double A3 = -35.0/48.0*(Math.Pow(n,3) - 5.0/16.0*Math.Pow(n,5));
    double A4 = 315.0/512.0*(Math.Pow(n,4) - 1.0/3.0*Math.Pow(n,6));
    double A5 = -693.0/1280.0*Math.Pow(n,5);
    double A6 = 1001.0/2048.0*Math.Pow(n,6);

    double s0 = (a / (1.0 + n)) * (A0*phi0 + A1*Math.Sin(2*phi0) + A2*Math.Sin(4*phi0) + A3*Math.Sin(6*phi0) + A4*Math.Sin(8*phi0) + A5*Math.Sin(10*phi0) + A6*Math.Sin(12*phi0));
    X -= s0 * m0;
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
  private float GetBilinearHeight(double c, double r, float[] grid, int nx, int ny) {
    int c0 = (int)Math.Floor(c); int r0 = (int)Math.Floor(r);
    int c1 = Math.Min(c0 + 1, nx - 1); int r1 = Math.Min(r0 + 1, ny - 1);
    float z00 = GetRawHeight(c0, r0, grid, nx, ny); float z10 = GetRawHeight(c1, r0, grid, nx, ny);
    float z01 = GetRawHeight(c0, r1, grid, nx, ny); float z11 = GetRawHeight(c1, r1, grid, nx, ny);
    if (z00 < -9000) return NoData;
    double dc = c - c0, dr = r - r0;
    return (float)(z00 * (1 - dc) * (1 - dr) + z10 * dc * (1 - dr) + z01 * (1 - dc) * dr + z11 * dc * dr);
  }
}
