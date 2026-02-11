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
		double size,
		int mode,
		ref object info,
		ref object output_mesh)
  {
    if (string.IsNullOrEmpty(xml_data)) {
      info = "Wait: xml_data が未入力です。";
      return;
    }

    try {
      XmlDocument doc = new XmlDocument();
      doc.LoadXml(xml_data);
      XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
      ns.AddNamespace("gml", "http://www.opengis.net/gml/3.2");

      string[] lcParts = doc.SelectSingleNode(".//gml:lowerCorner", ns).InnerText.Trim().Split(' ');
      string[] ucParts = doc.SelectSingleNode(".//gml:upperCorner", ns).InnerText.Trim().Split(' ');
      double lLat = double.Parse(lcParts[0]); double lLon = double.Parse(lcParts[1]);
      double uLat = double.Parse(ucParts[0]); double uLon = double.Parse(ucParts[1]);

      string[] hiParts = doc.SelectSingleNode(".//gml:high", ns).InnerText.Trim().Split(' ');
      int orig_nx = int.Parse(hiParts[0]) + 1;
      int orig_ny = int.Parse(hiParts[1]) + 1;

      int startX = 0, startY = 0;
      XmlNode sn = doc.SelectSingleNode(".//gml:startPoint", ns);
      if (sn != null) {
        string[] sParts = sn.InnerText.Trim().Split(' ');
        startX = int.Parse(sParts[0]); startY = int.Parse(sParts[1]);
      }
      int offset = startY * orig_nx + startX;
      string tupleList = doc.SelectSingleNode(".//gml:tupleList", ns).InnerText.Trim();
      string[] lines = tupleList.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

      int activeZone = (jgd_zone <= 0) ? AutoDetectJGDZone((lLat + uLat) / 2.0, (lLon + uLon) / 2.0) : jgd_zone;
      double[] origin_lats = { 33, 33, 36, 33, 36, 36, 36, 36, 36, 40, 40, 40, 44, 26, 26, 26, 26, 20, 26 };
      double[] origin_lons = { 129.5, 131.0, 132.166666666667, 133.5, 134.333333333333, 136.0, 137.166666666667, 138.5, 139.833333333333, 140.833333333333, 140.25, 142.25, 144.25, 142.0, 127.5, 124.0, 131.0, 136.0, 154.0 };
      double phi0_deg = origin_lats[activeZone - 1];
      double lam0_deg = origin_lons[activeZone - 1];

      bool doClipping = (target_point != Point3d.Unset && size > 0);
      double half = size / 2.0;
      int step = Math.Max(1, sample_step);

      List<int> rows = new List<int>();
      for (int r = 0; r < orig_ny; r += step) rows.Add(r);
      if (rows.Count > 0 && rows.Last() != orig_ny - 1) rows.Add(orig_ny - 1);
      List<int> cols = new List<int>();
      for (int c = 0; c < orig_nx; c += step) cols.Add(c);
      if (cols.Count > 0 && cols.Last() != orig_nx - 1) cols.Add(orig_nx - 1);

      int nY = rows.Count; int nX = cols.Count;
      Point3d[] verts = new Point3d[nX * nY];
      bool[] isValid = new bool[nX * nY];

      Parallel.For(0, nY, rIdx => {
        int r = rows[rIdx];
        double lat = uLat - (r * (uLat - lLat) / (orig_ny - 1));
        for (int cIdx = 0; cIdx < nX; cIdx++) {
          int c = cols[cIdx];
          double lon = lLon + (c * (uLon - lLon) / (orig_nx - 1));
          double pX, pY;
          
          // mode 1, 2 では 6次展開(Exact) を使用
          if (mode == 0) LatLonToJGD_Fast(lat, lon, phi0_deg, lam0_deg, out pX, out pY);
          else LatLonToJGD_Exact(lat, lon, phi0_deg, lam0_deg, out pX, out pY);

          int idx = rIdx * nX + cIdx;
          if (!doClipping || (Math.Abs(pY - target_point.X) <= half && Math.Abs(pX - target_point.Y) <= half)) {
            float z = (mode == 2) ? GetBilinearHeight(c, r, lines, orig_nx, offset) : GetRawHeight(c, r, lines, orig_nx, offset);
            if (z > -9000) { verts[idx] = new Point3d(pY, pX, z); isValid[idx] = true; }
          }
        }
      });

      Mesh mesh = new Mesh();
      mesh.Vertices.AddVertices(verts);
      int faceCount = 0;
      for (int r = 0; r < nY - 1; r++) {
        for (int c = 0; c < nX - 1; c++) {
          int v1 = r * nX + c; int v2 = v1 + 1;
          int v3 = (r + 1) * nX + c + 1; int v4 = (r + 1) * nX + c;
          if (isValid[v1] && isValid[v2] && isValid[v3] && isValid[v4]) { mesh.Faces.AddFace(v1, v4, v3, v2); faceCount++; }
        }
      }
      mesh.Vertices.CullUnused();
      mesh.Normals.ComputeNormals();
      output_mesh = mesh;

      List<string> log = new List<string>();
      log.Add("== Source Info ==");
      log.Add(string.Format(" Lat Range: {0} to {1}", ToDMS(lLat), ToDMS(uLat)));
      log.Add(string.Format(" Lon Range: {0} to {1}", ToDMS(lLon), ToDMS(uLon)));
      log.Add(string.Format(" Original Size: {0} x {1}", orig_nx, orig_ny));
      log.Add("");
      log.Add("== Current Settings ==");
      log.Add(" JGD Zone: " + activeZone + (jgd_zone <= 0 ? " (Auto Detected)" : " (Manual)"));
      log.Add(" Mode: " + (mode == 2 ? "High (6th Order + Bilinear)" : (mode == 1 ? "Standard (6th Order)" : "Fast (3rd Order)")));
      log.Add(string.Format(" Output Mesh: {0}x{1} pts, {2} faces", nX, nY, faceCount));
      info = log;

    } catch (Exception ex) { info = "Error: " + ex.Message; }
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
  private float GetRawHeight(int c, int r, string[] lines, int nx, int offset) {
    int idx = (r * nx + c) - offset;
    if (idx < 0 || idx >= lines.Length) return -9999f;
    string[] p = lines[idx].Split(',');
    float z; return float.TryParse(p.Length >= 2 ? p[1] : p[0], out z) ? z : -9999f;
  }
  private float GetBilinearHeight(double c, double r, string[] lines, int nx, int offset) {
    int c0 = (int)Math.Floor(c); int r0 = (int)Math.Floor(r);
    int c1 = Math.Min(c0 + 1, nx - 1); int r1 = Math.Min(r0 + 1, (lines.Length + offset) / nx - 1);
    float z00 = GetRawHeight(c0, r0, lines, nx, offset); float z10 = GetRawHeight(c1, r0, lines, nx, offset);
    float z01 = GetRawHeight(c0, r1, lines, nx, offset); float z11 = GetRawHeight(c1, r1, lines, nx, offset);
    if (z00 < -9000) return -9999f;
    double dc = c - c0, dr = r - r0;
    return (float)(z00 * (1 - dc) * (1 - dr) + z10 * dc * (1 - dr) + z01 * (1 - dc) * dr + z11 * dc * dr);
  }
}