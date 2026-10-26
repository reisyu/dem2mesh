// 総務省統計局「市区町村別メッシュ・コード一覧」（都道府県別CSV）から、3次メッシュ → 平面直角座標系の系 の表を作り、
// dem2mesh.cs の <zone-table> の部分に gzip + Base64 で埋め込む
// 使い方: build_zone_table.exe <CSVのフォルダ> <dem2mesh.cs> <出力フォルダ>
// .NET Framework 4.x の csc（C# 5）でビルドするため、文字列補間などは使わない
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;

public static class BuildZoneTable {
  // 告示（平成14年国土交通省告示第9号）の適用区域。01 北海道・13 東京都・46 鹿児島県・47 沖縄県は下で個別に決める
  static readonly Dictionary<string, int> PrefZone = new Dictionary<string, int> {
    { "02", 10 }, { "03", 10 }, { "04", 10 }, { "05", 10 }, { "06", 10 }, { "07", 9 }, { "08", 9 }, { "09", 9 }, { "10", 9 }, { "11", 9 },
    { "12", 9 }, { "14", 9 }, { "15", 8 }, { "16", 7 }, { "17", 7 }, { "18", 6 }, { "19", 8 }, { "20", 8 }, { "21", 7 }, { "22", 8 },
    { "23", 7 }, { "24", 6 }, { "25", 6 }, { "26", 6 }, { "27", 6 }, { "28", 5 }, { "29", 6 }, { "30", 6 }, { "31", 5 }, { "32", 3 },
    { "33", 5 }, { "34", 3 }, { "35", 3 }, { "36", 4 }, { "37", 4 }, { "38", 4 }, { "39", 4 }, { "40", 2 }, { "41", 2 }, { "42", 1 },
    { "43", 2 }, { "44", 2 }, { "45", 2 } };

  // 北海道は市町村コードで決める。振興局ごとのコードの範囲は、CSV の町村名で確かめてある
  static int HokkaidoZone(string code) {
    int n = int.Parse(code.Substring(2), CultureInfo.InvariantCulture);
    if (new[] { 202, 203, 233, 236 }.Contains(n)) return 11;                  // 函館市・小樽市・伊達市・北斗市
    if (n >= 330 && n <= 349) return 11;                                      // 渡島総合振興局の町
    if (n >= 360 && n <= 379) return 11;                                      // 檜山振興局の町
    if (n >= 390 && n <= 409) return 11;                                      // 後志総合振興局の町村
    if (new[] { 571, 575, 584 }.Contains(n)) return 11;                       // 胆振のうち豊浦町・壮瞥町・洞爺湖町
    if (new[] { 206, 207, 208, 211, 223 }.Contains(n)) return 13;             // 釧路市・帯広市・北見市・網走市・根室市
    if (new[] { 543, 544, 545, 546, 547, 549, 550, 552, 564 }.Contains(n)) return 13; // オホーツクの美幌・津別・斜里・清里・小清水・訓子府・置戸・佐呂間・大空
    if (n >= 630 && n <= 649) return 13;                                      // 十勝総合振興局の町村
    if (n >= 660 && n <= 669) return 13;                                      // 釧路総合振興局の町村
    if (n >= 690 && n <= 700) return 13;                                      // 根室振興局の町村（北方領土の6村を含む）
    return 12;
  }
  static int TokyoZone(double lat, double lon) { if (lat >= 28) return 9; if (lon >= 143) return 19; if (lon >= 140.5) return 14; return 18; }
  static int OkinawaZone(double lat, double lon) { if (lon >= 130) return 17; if (lon >= 126) return 15; return 16; }
  static int KagoshimaZone(double lat, double lon) {
    // 北緯27〜32°・東経128°18′〜130°の島（奄美群島は東経130°13′まで）。本土はこの範囲にかからない
    if (lat >= 27 && lat < 32 && lon >= 128.3 && lon < 130) return 1;
    if (lat >= 27 && lat < 28.6 && lon >= 128.3 && lon < 130 + 13 / 60.0) return 1;
    return 2;
  }

  // 3次メッシュコード（JIS X0410）→ 中心の緯度経度
  static void MeshCenter(string m, out double lat, out double lon) {
    int p = int.Parse(m.Substring(0, 2)), u = int.Parse(m.Substring(2, 2)), q = m[4] - '0', v = m[5] - '0', r = m[6] - '0', w = m[7] - '0';
    lat = (p * 40 + q * 5 + r * 0.5 + 0.25) / 60;
    lon = 100 + u + (v * 7.5 + w * 0.75 + 0.375) / 60;
  }
  static char ZoneChar(int z) { return "0123456789ABCDEFGHIJ"[z]; } // 系 → 1文字（1〜9, A=10 … J=19）

  public static int Main(string[] args) {
    Console.OutputEncoding = Encoding.UTF8;
    if (args.Length < 3) { Console.WriteLine("使い方: build_zone_table.exe <CSVのフォルダ> <dem2mesh.cs> <出力フォルダ>"); return 2; }
    string csvDir = args[0], csPath = args[1], outDir = args[2];
    var sjis = Encoding.GetEncoding(932);

    // 3次メッシュごとに、そこにかかる市区町村の系を集める
    var meshZones = new Dictionary<string, SortedSet<int>>();
    int rows = 0;
    for (int i = 1; i <= 47; i++) {
      string pref = i.ToString("00");
      string file = Path.Combine(csvDir, pref + ".csv");
      if (!File.Exists(file)) { Console.WriteLine("CSVがありません: " + file); return 2; }
      foreach (string line in File.ReadAllText(file, sjis).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Skip(1)) {
        string[] c = line.Split(',');
        if (c.Length < 3 || c[2].Length != 8 || !c[2].All(char.IsDigit)) continue;
        rows++;
        string code = c[0], mesh = c[2];
        double lat, lon; MeshCenter(mesh, out lat, out lon);
        int z;
        if (pref == "01") z = HokkaidoZone(code);
        else if (pref == "13") z = TokyoZone(lat, lon);
        else if (pref == "46") z = KagoshimaZone(lat, lon);
        else if (pref == "47") z = OkinawaZone(lat, lon);
        else z = PrefZone[pref];
        SortedSet<int> set;
        if (!meshZones.TryGetValue(mesh, out set)) { set = new SortedSet<int>(); meshZones[mesh] = set; }
        set.Add(z);
      }
    }

    // 2次メッシュ（10×10の3次）単位にまとめる。中の系が1つなら1行、混ざるなら3次を1文字ずつ（陸でない所は "-"、あいまいは "?"）
    var by2 = new SortedDictionary<string, Dictionary<string, SortedSet<int>>>(StringComparer.Ordinal);
    foreach (var kv in meshZones) {
      string k = kv.Key.Substring(0, 6);
      Dictionary<string, SortedSet<int>> sub;
      if (!by2.TryGetValue(k, out sub)) { sub = new Dictionary<string, SortedSet<int>>(); by2[k] = sub; }
      sub[kv.Key.Substring(6)] = kv.Value;
    }
    var lines = new List<string>(); var ambLines = new List<string>(); int uniform = 0, mixed = 0;
    foreach (var kv in by2) {
      var zs = new HashSet<int>(); bool amb = false;
      foreach (var s in kv.Value.Values) { if (s.Count > 1) amb = true; zs.UnionWith(s); }
      if (!amb && zs.Count == 1) { uniform++; lines.Add(kv.Key + ZoneChar(zs.First())); continue; }
      mixed++;
      var grid = new StringBuilder();
      for (int r = 0; r < 10; r++) for (int w = 0; w < 10; w++) {
        SortedSet<int> s;
        if (!kv.Value.TryGetValue(r.ToString() + w.ToString(), out s)) grid.Append('-');
        else if (s.Count > 1) { grid.Append('?'); ambLines.Add(kv.Key + r + w + ":" + new string(s.Select(z => ZoneChar(z)).ToArray())); }
        else grid.Append(ZoneChar(s.First()));
      }
      lines.Add(kv.Key + "*" + grid);
    }
    string table = string.Join("\n", lines) + "\n#\n" + string.Join("\n", ambLines) + "\n";

    // gzip + Base64
    byte[] raw = new UTF8Encoding(false).GetBytes(table);
    string b64;
    using (var ms = new MemoryStream()) {
      using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
      b64 = Convert.ToBase64String(ms.ToArray());
    }
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "zone_table.txt"), table, new UTF8Encoding(false));

    // dem2mesh.cs の <zone-table> と </zone-table> の間を置き換える
    string cs = File.ReadAllText(csPath, Encoding.UTF8);
    const string begin = "  // <zone-table>", end = "  // </zone-table>";
    int a = cs.IndexOf(begin, StringComparison.Ordinal), b = cs.IndexOf(end, StringComparison.Ordinal);
    if (a < 0 || b < a) { Console.WriteLine("dem2mesh.cs に <zone-table> の目印がありません"); return 2; }
    var block = new StringBuilder();
    block.Append(begin + " この部分は tools/build_zone_table.ps1 が作る。手で直さない\n");
    block.Append("  // 3次メッシュ → 系 の表。総務省統計局「市区町村別メッシュ・コード一覧」（令和2年10月1日現在）を加工して作成。gzip + Base64\n");
    block.Append(string.Format(CultureInfo.InvariantCulture, "  // 陸の3次メッシュ {0}、あいまい {1}、2次メッシュ {2}（系が1つ {3}、混ざる {4}）\n", meshZones.Count, ambLines.Count, by2.Count, uniform, mixed));
    block.Append("  private const string ZoneTableB64 =\n");
    for (int i = 0; i < b64.Length; i += 120) {
      string chunk = b64.Substring(i, Math.Min(120, b64.Length - i));
      block.Append("    \"" + chunk + "\"" + (i + 120 < b64.Length ? " +\n" : ";\n"));
    }
    string updated = cs.Substring(0, a) + block + cs.Substring(b);
    File.WriteAllText(csPath, updated, new UTF8Encoding(false));

    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "CSVの行数 {0} / 陸の3次メッシュ {1} / あいまい {2} / 2次メッシュ {3}（系が1つ {4}、混ざる {5}）",
      rows, meshZones.Count, ambLines.Count, by2.Count, uniform, mixed));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "表 {0:F1} KB → gzip + Base64 {1:F1} KB を {2} に埋め込みました", raw.Length / 1024.0, b64.Length / 1024.0, csPath));
    return 0;
  }
}
