using System;
using System.Collections.Generic;
using System.Xml;
using System.Linq;
using System.Threading.Tasks;
using Rhino.Geometry;

public class Script_Instance : GH_ScriptInstance
{
  private void RunScript(
		string xml_data,
		int jgd_zone,
		int sample_step,
		Point3d target_point,
		double radius,
		ref object output_mesh,
		ref object info)
  {
    if (string.IsNullOrEmpty(xml_data)) return;

    // 1. XMLの読み込み
    XmlDocument doc = new XmlDocument();
    doc.LoadXml(xml_data);
    XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
    ns.AddNamespace("fgd", "http://fgd.gsi.go.jp/spec/2008/FGD_GMLSchema");
    ns.AddNamespace("gml", "http://www.opengis.net/gml/3.2");

    string[] lcParts = doc.SelectSingleNode(".//gml:lowerCorner", ns).InnerText.Trim().Split(' ');
    string[] ucParts = doc.SelectSingleNode(".//gml:upperCorner", ns).InnerText.Trim().Split(' ');
    double lLat = double.Parse(lcParts[0]); double lLon = double.Parse(lcParts[1]);
    double uLat = double.Parse(ucParts[0]); double uLon = double.Parse(ucParts[1]);

    string[] highParts = doc.SelectSingleNode(".//gml:high", ns).InnerText.Trim().Split(' ');
    int orig_nx = int.Parse(highParts[0]) + 1;
    int orig_ny = int.Parse(highParts[1]) + 1;

    int startX = 0, startY = 0;
    XmlNode startNode = doc.SelectSingleNode(".//gml:startPoint", ns);
    if (startNode != null) {
      string[] sParts = startNode.InnerText.Trim().Split(' ');
      startX = int.Parse(sParts[0]); startY = int.Parse(sParts[1]);
    }
    int offset = startY * orig_nx + startX;

    string tupleText = doc.SelectSingleNode(".//gml:tupleList", ns).InnerText.Trim();
    string[] lines = tupleText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    int totalCount = lines.Length;

    // 2. 座標系の設定
    double[] origin_lats = { 33, 33, 36, 33, 36, 36, 36, 36, 36, 40, 40, 40, 44, 26, 26, 26, 26, 20, 26 };
    double[] origin_lons = { 129.5, 131.0, 132.166666666667, 133.5, 134.333333333333, 136.0, 137.166666666667, 138.5, 139.833333333333, 140.833333333333, 140.25, 142.25, 144.25, 142.0, 127.5, 124.0, 131.0, 136.0, 154.0 };
    int zIdx = Math.Max(1, Math.Min(jgd_zone, 19)) - 1;
    double phi0_deg = origin_lats[zIdx];
    double lam0_deg = origin_lons[zIdx];

    double x_min, y_min, x_max, y_max;
    LatLonToJGD(lLat, lLon, phi0_deg, lam0_deg, out x_min, out y_min);
    LatLonToJGD(uLat, uLon, phi0_deg, lam0_deg, out x_max, out y_max);

    bool doClipping = (target_point != Point3d.Unset && radius > 0);
    
    // 3. サンプリング設定
    int step = Math.Max(1, sample_step);
    List<int> rows = new List<int>();
    for (int r = 0; r < orig_ny; r += step) rows.Add(r);
    if (rows.Count > 0 && rows.Last() < orig_ny - 1) rows.Add(orig_ny - 1);
    List<int> cols = new List<int>();
    for (int c = 0; c < orig_nx; c += step) cols.Add(c);
    if (cols.Count > 0 && cols.Last() < orig_nx - 1) cols.Add(orig_nx - 1);

    int new_nx = cols.Count;
    int new_ny = rows.Count;
    double dLat = (uLat - lLat) / (orig_ny - 1);
    double dLon = (uLon - lLon) / (orig_nx - 1);

    // 4. 頂点作成
    Point3d[] verts = new Point3d[new_nx * new_ny];
    bool[] isValid = new bool[new_nx * new_ny];

    Parallel.For(0, new_ny, rIdx => {
      int r = rows[rIdx];
      double phi = uLat - (r * dLat);
      for (int cIdx = 0; cIdx < new_nx; cIdx++) {
        int c = cols[cIdx];
        double lam = lLon + (c * dLon);
        double x, y;
        LatLonToJGD(phi, lam, phi0_deg, lam0_deg, out x, out y);
        int targetIdx = rIdx * new_nx + cIdx;
        bool isInside = !doClipping || (Math.Abs(x - target_point.X) <= radius && Math.Abs(y - target_point.Y) <= radius);
        int gridIdx = (r * orig_nx + c) - offset;
        if (isInside && gridIdx >= 0 && gridIdx < totalCount) {
          string[] p = lines[gridIdx].Split(',');
          string valStr = (p.Length >= 2) ? p[1] : p[0];
          float z;
          if (float.TryParse(valStr, out z) && z > -9000) {
            isValid[targetIdx] = true;
            verts[targetIdx] = new Point3d(x, y, z);
          }
        }
      }
    });

    // 5. メッシュ構築
    Mesh mesh = new Mesh();
    mesh.Vertices.AddVertices(verts);
    int faceCount = 0;
    for (int r = 0; r < new_ny - 1; r++) {
      for (int c = 0; c < new_nx - 1; c++) {
        int v1 = r * new_nx + c, v2 = v1 + 1, v3 = (r + 1) * new_nx + c + 1, v4 = (r + 1) * new_nx + c;
        if (isValid[v1] && isValid[v2] && isValid[v3] && isValid[v4]) {
          mesh.Faces.AddFace(v1, v2, v3, v4);
          faceCount++;
        }
      }
    }
    mesh.Vertices.CullUnused();

    // 6. 拡張情報パネルの構成
    List<string> log = new List<string>();
    log.Add("--- XML Metadata ---");
    log.Add(string.Format("Original Grid: {0} x {1}", orig_nx, orig_ny));
    log.Add(string.Format("Sampled Grid: {0} x {1}", new_nx, new_ny));
    log.Add(string.Format("Lat Range: {0:F6} to {1:F6}", lLat, uLat));
    log.Add(string.Format("Lon Range: {0:F6} to {1:F6}", lLon, uLon));
    log.Add("");
    log.Add("--- Projection ---");
    log.Add(string.Format("JGD2011 Zone: {0}", jgd_zone));
    log.Add(string.Format("Origin (Lat, Lon): {0}, {1}", phi0_deg, lam0_deg));
    log.Add(string.Format("XML Center (X, Y): {0:N3}, {1:N3}", (x_min + x_max) / 2, (y_min + y_max) / 2));
    log.Add(string.Format("XML Bounds X: {0:N4} to {1:N4}", x_min, x_max));
    log.Add(string.Format("XML Bounds Y: {0:N4} to {1:N4}", y_min, y_max));
    log.Add("");
    log.Add("--- Output Info ---");
    if (doClipping) {
      log.Add(string.Format("Mode: Clipping (±{0:N2}m)", radius));
      log.Add(string.Format("Target: X={0:N4}, Y={1:N4}", target_point.X, target_point.Y));
    } else {
      log.Add("Mode: Full Mesh");
    }
    log.Add(string.Format("Faces: {0:N0}", faceCount));
    
    if (faceCount == 0 && doClipping) log.Add("!! WARNING: Target is outside bounds !!");

    mesh.Normals.ComputeNormals();
    mesh.Compact();
    output_mesh = mesh;
    info = log;
  }

  // --- 座標変換メソッド (変更なし) ---
  private void LatLonToJGD(double lat_deg, double lon_deg, double phi0_deg, double lam0_deg, out double x, out double y) {
    double phi = lat_deg * Math.PI / 180.0;
    double lam = lon_deg * Math.PI / 180.0;
    double phi0 = phi0_deg * Math.PI / 180.0;
    double lam0 = lam0_deg * Math.PI / 180.0;
    double a = 6378137.0; double f = 1.0 / 298.257222101; double m0 = 0.9999;
    double n = f / (2.0 - f);
    double a_bar = (a / (1.0 + n)) * (1.0 + (1.0/4.0)*Math.Pow(n, 2) + (1.0/64.0)*Math.Pow(n, 4) + (1.0/256.0)*Math.Pow(n, 6));
    double dlh = lam - lam0;
    double sin_phi = Math.Sin(phi);
    double e_tmp = 2.0 * Math.Sqrt(n) / (1.0 + n);
    double t_arg = 0.5 * Math.Log((1.0 + sin_phi) / (1.0 - sin_phi)) - e_tmp * (0.5 * Math.Log((1.0 + e_tmp * sin_phi) / (1.0 - e_tmp * sin_phi)));
    double t = (Math.Exp(t_arg) - Math.Exp(-t_arg)) / 2.0;
    double xi_prime = Math.Atan(t / Math.Cos(dlh));
    double eta_arg = Math.Sin(dlh) / Math.Sqrt(1.0 + t * t);
    double eta_prime = 0.5 * Math.Log((1.0 + eta_arg) / (1.0 - eta_arg));
    double alpha1 = (1.0/2.0)*n - (2.0/3.0)*Math.Pow(n,2) + (5.0/16.0)*Math.Pow(n,3) + (41.0/180.0)*Math.Pow(n,4) - (127.0/288.0)*Math.Pow(n,5) + (7891.0/37800.0)*Math.Pow(n,6);
    double alpha2 = (13.0/48.0)*Math.Pow(n,2) - (3.0/5.0)*Math.Pow(n,3) + (557.0/1440.0)*Math.Pow(n,4) + (281.0/630.0)*Math.Pow(n,5) - (1983433.0/1935360.0)*Math.Pow(n,6);
    double alpha3 = (61.0/240.0)*Math.Pow(n,3) - (103.0/140.0)*Math.Pow(n,4) + (15061.0/26880.0)*Math.Pow(n,5) + (167603.0/181440.0)*Math.Pow(n,6);
    double alpha4 = (49561.0/161280.0)*Math.Pow(n,4) - (179.0/168.0)*Math.Pow(n,5) + (6601661.0/7257600.0)*Math.Pow(n,6);
    double alpha5 = (34729.0/80640.0)*Math.Pow(n,5) - (3418889.0/1995840.0)*Math.Pow(n,6);
    double alpha6 = (212378941.0/319334400.0)*Math.Pow(n,6);
    double sumX = 0; double sumY = 0;
    double[] alphas = { alpha1, alpha2, alpha3, alpha4, alpha5, alpha6 };
    for (int j = 1; j <= 6; j++) {
      double aj = alphas[j-1];
      sumX += aj * Math.Cos(2 * j * xi_prime) * (Math.Exp(2 * j * eta_prime) - Math.Exp(-2 * j * eta_prime)) / 2.0;
      sumY += aj * Math.Sin(2 * j * xi_prime) * (Math.Exp(2 * j * eta_prime) + Math.Exp(-2 * j * eta_prime)) / 2.0;
    }
    x = a_bar * m0 * (eta_prime + sumX);
    y = a_bar * m0 * (xi_prime + sumY);
    double s0 = CalculateS(phi0, a, f);
    y -= s0 * m0;
  }

  private double CalculateS(double phi, double a, double f) {
    double n = f / (2.0 - f);
    double A0 = 1.0 + (1.0/4.0)*Math.Pow(n, 2) + (1.0/64.0)*Math.Pow(n, 4) + (1.0/256.0)*Math.Pow(n, 6);
    double A1 = - (3.0/2.0) * (n - (1.0/8.0)*Math.Pow(n, 3) - (1.0/64.0)*Math.Pow(n, 5));
    double A2 = (15.0/16.0) * (Math.Pow(n, 2) - (1.0/4.0)*Math.Pow(n, 4) - (1.0/64.0)*Math.Pow(n, 6));
    double A3 = - (35.0/48.0) * (Math.Pow(n, 3) - (5.0/16.0)*Math.Pow(n, 5));
    double A4 = (315.0/512.0) * (Math.Pow(n, 4) - (1.0/4.0)*Math.Pow(n, 6));
    double A5 = - (693.0/1280.0) * Math.Pow(n, 5);
    double A6 = (1001.0/2048.0) * Math.Pow(n, 6);
    double sumA = A0 * phi + A1 * Math.Sin(2 * phi) + A2 * Math.Sin(4 * phi) + A3 * Math.Sin(6 * phi) + A4 * Math.Sin(8 * phi) + A5 * Math.Sin(10 * phi) + A6 * Math.Sin(12 * phi);
    return (a / (1.0 + n)) * sumA;
  }
}