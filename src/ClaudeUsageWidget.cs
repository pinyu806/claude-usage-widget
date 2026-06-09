using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeUsageWidget
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // api.anthropic.com 需 TLS 1.2；.NET Framework 預設可能是 TLS 1.0 → 握手失敗
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Widget());
        }
    }

    class Stat { public double Util = -1; public int RemainMin = 0; public string Reset = "--"; }

    enum DisplayMode
    {
        Small = 0,
        Large = 1,
        Auto = 2
    }

    class Widget : Form
    {
        // palette (對齊網頁版)
        static readonly Color C_BG = ColorTranslator.FromHtml("#181b22");
        static readonly Color C_LINE = ColorTranslator.FromHtml("#262b34");
        static readonly Color C_TXT = ColorTranslator.FromHtml("#e6e9ef");
        static readonly Color C_SUB = ColorTranslator.FromHtml("#8b93a3");
        static readonly Color C_OK = ColorTranslator.FromHtml("#3fb950");
        static readonly Color C_WARN = ColorTranslator.FromHtml("#d29922");
        static readonly Color C_BAD = ColorTranslator.FromHtml("#f85149");

        readonly string credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude\\.credentials.json");

        // Claude Code 公開 OAuth client_id + token 端點（refresh 用）
        const string CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string TOKEN_URL = "https://console.anthropic.com/v1/oauth/token";
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Stat five = new Stat(), seven = new Stat();
        string sub = "";
        string status = "載入中…";
        bool stale = false;
        DateTime lastTryUtc = DateTime.MinValue;
        DateTime lastOkUtc = DateTime.MinValue;
        bool haveData = false;
        // usage 端點 429 指數退避
        DateTime nextAllowedUtc = DateTime.MinValue;
        int backoffSec = 0;
        // refresh 端點退避（refresh 本身也會 429）
        DateTime refreshNextUtc = DateTime.MinValue;
        Point dragOff; bool dragging = false;
        System.Windows.Forms.Timer timer;
        DisplayMode currentMode = DisplayMode.Auto;
        double currentOpacity = 1.0;
        bool isHovered = false;
        const int smallWidth = 106;
        const int smallHeight = 46;
        const int largeWidth = 260;
        const int largeHeight = 172;
        System.Windows.Forms.Timer hoverTimer;

        public Widget()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;

            LoadConfig();
            Opacity = currentOpacity;
            int startW = largeWidth;
            int startH = largeHeight;
            bool isSmall = (currentMode == DisplayMode.Small) || (currentMode == DisplayMode.Auto);
            if (isSmall)
            {
                startW = smallWidth;
                startH = smallHeight;
            }
            Width = startW; Height = startH;

            BackColor = C_BG;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Text = "Claude 用量";

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Region = RoundRegion(Width, Height, isSmall ? 10 : 14);

            var menu = new ContextMenuStrip();
            menu.Items.Add("立即重新整理", null, (s, e) => Refresh2());
            menu.Items.Add("-");

            var mSmall = new ToolStripMenuItem("小面板", null, (s, e) => SetMode(DisplayMode.Small));
            var mLarge = new ToolStripMenuItem("大面板", null, (s, e) => SetMode(DisplayMode.Large));
            var mAuto = new ToolStripMenuItem("自動模式 (滑鼠暫留)", null, (s, e) => SetMode(DisplayMode.Auto));

            var mOpacity = new ToolStripMenuItem("透明度");
            for (int i = 10; i >= 1; i--)
            {
                double op = i / 10.0;
                string text = (i * 10) + "%";
                var item = new ToolStripMenuItem(text);
                item.Click += (s2, e2) => SetOpacity(op);
                mOpacity.DropDownItems.Add(item);
            }

            menu.Items.Add(mSmall);
            menu.Items.Add(mLarge);
            menu.Items.Add(mAuto);
            menu.Items.Add(mOpacity);
            menu.Items.Add("-");
            menu.Items.Add("關閉", null, (s, e) => Close());
            ContextMenuStrip = menu;

            menu.Opening += (s, e) =>
            {
                mSmall.Checked = currentMode == DisplayMode.Small;
                mLarge.Checked = currentMode == DisplayMode.Large;
                mAuto.Checked = currentMode == DisplayMode.Auto;

                foreach (ToolStripMenuItem item in mOpacity.DropDownItems)
                {
                    double opVal;
                    string t = item.Text.Replace("%", "");
                    if (double.TryParse(t, out opVal))
                    {
                        item.Checked = Math.Abs((opVal / 100.0) - currentOpacity) < 0.05;
                    }
                }
            };

            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragOff = e.Location; } };
            MouseMove += (s, e) => { if (dragging) Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y); };
            MouseUp += (s, e) => dragging = false;

            timer = new System.Windows.Forms.Timer(); timer.Interval = 60000; timer.Tick += (s, e) => Refresh2();
            timer.Start();

            hoverTimer = new System.Windows.Forms.Timer(); hoverTimer.Interval = 100; hoverTimer.Tick += HoverTimer_Tick;
            hoverTimer.Start();

            Refresh2();
        }

        static Region RoundRegion(int w, int h, int r)
        {
            var p = new GraphicsPath();
            p.AddArc(0, 0, r, r, 180, 90);
            p.AddArc(w - r, 0, r, r, 270, 90);
            p.AddArc(w - r, h - r, r, r, 0, 90);
            p.AddArc(0, h - r, r, r, 90, 90);
            p.CloseFigure();
            return new Region(p);
        }

        void Refresh2()
        {
            var t = new Thread(Fetch); t.IsBackground = true; t.Start();
        }

        void Fetch()
        {
            DateTime now = DateTime.UtcNow;
            // 429 退避：冷卻期內不打 usage（仍重繪倒數）
            if (haveData && now < nextAllowedUtc) { Invoke2(); return; }
            // 成功快取 60s
            if (haveData && (now - lastOkUtc).TotalSeconds < 60 && !stale && backoffSec == 0) { Invoke2(); return; }
            // 最短重試間隔 20s（防養熱）
            if (haveData && (now - lastTryUtc).TotalSeconds < 20) { Invoke2(); return; }
            lastTryUtc = now;
            try
            {
                string cred = File.ReadAllText(credPath);
                string tok = Match1(cred, "\"accessToken\"\\s*:\\s*\"([^\"]+)\"");
                string rtok = Match1(cred, "\"refreshToken\"\\s*:\\s*\"([^\"]+)\"");
                sub = Match1(cred, "\"subscriptionType\"\\s*:\\s*\"([^\"]+)\"");
                string expS = Match1(cred, "\"expiresAt\"\\s*:\\s*([0-9]+)");
                if (string.IsNullOrEmpty(tok)) { Fail("找不到 token"); return; }

                // 到期前 60s 主動 refresh（避免直接吃 401）
                long nowMs = (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
                if (expS != "" && rtok != "")
                {
                    long expMs;
                    if (long.TryParse(expS, out expMs) && expMs - nowMs < 60000)
                    {
                        string nt = TryRefresh(rtok, now);
                        if (nt != null) tok = nt;
                    }
                }

                string body = GetUsage(tok);
                five = ParseStat(body, "five_hour");
                seven = ParseStat(body, "seven_day");
                haveData = true; stale = false; status = ""; lastOkUtc = DateTime.UtcNow;
                backoffSec = 0; nextAllowedUtc = DateTime.MinValue;
            }
            catch (WebException we)
            {
                int code = StatusOf(we);
                if (code == 429) { SetBackoff(we, now); Fail("限流(429)"); }
                else if (code == 401 || code == 403)
                {
                    // token 過期 → 試 refresh 一次再重打 usage
                    string rtok = Match1(SafeRead(credPath), "\"refreshToken\"\\s*:\\s*\"([^\"]+)\"");
                    string nt = rtok != "" ? TryRefresh(rtok, now) : null;
                    bool ok = false;
                    if (nt != null)
                    {
                        try
                        {
                            string body = GetUsage(nt);
                            five = ParseStat(body, "five_hour"); seven = ParseStat(body, "seven_day");
                            haveData = true; stale = false; status = ""; lastOkUtc = DateTime.UtcNow;
                            backoffSec = 0; nextAllowedUtc = DateTime.MinValue;
                            ok = true;
                        }
                        catch { }
                    }
                    if (!ok) Fail("請開 Claude Code 重新登入");
                }
                else Fail("連線失敗");
            }
            catch (Exception ex) { Fail(ex.Message); }
            Invoke2();
        }

        string GetUsage(string tok)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
            req.Method = "GET";
            req.Timeout = 20000;
            req.Headers["Authorization"] = "Bearer " + tok;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            req.Headers["anthropic-version"] = "2023-06-01";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        // refresh 成功回新 accessToken；失敗回 null（並設 refresh 退避）
        string TryRefresh(string rtok, DateTime now)
        {
            if (now < refreshNextUtc) return null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(TOKEN_URL);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 20000;
                string payload = "{\"grant_type\":\"refresh_token\",\"refresh_token\":\"" + rtok +
                                 "\",\"client_id\":\"" + CLIENT_ID + "\"}";
                byte[] data = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    body = sr.ReadToEnd();
                string na = Match1(body, "\"access_token\"\\s*:\\s*\"([^\"]+)\"");
                if (na == "") { refreshNextUtc = now.AddSeconds(60); return null; }
                string nr = Match1(body, "\"refresh_token\"\\s*:\\s*\"([^\"]+)\"");
                string ei = Match1(body, "\"expires_in\"\\s*:\\s*([0-9]+)");
                long newExp = 0; long sec;
                if (ei != "" && long.TryParse(ei, out sec))
                    newExp = (long)(DateTime.UtcNow - Epoch).TotalMilliseconds + sec * 1000L;
                WriteCreds(na, nr, newExp);
                refreshNextUtc = DateTime.MinValue;
                return na;
            }
            catch (WebException we)
            {
                int code = StatusOf(we);
                refreshNextUtc = now.AddSeconds(code == 429 ? 120 : 60);
                return null;
            }
            catch { refreshNextUtc = now.AddSeconds(60); return null; }
        }

        // 只替換三個欄位值，保留其餘 JSON 結構；UTF-8 無 BOM
        void WriteCreds(string access, string refresh, long expMs)
        {
            try
            {
                string c = File.ReadAllText(credPath);
                c = Regex.Replace(c, "(\"accessToken\"\\s*:\\s*\")[^\"]*(\")",
                                  m => m.Groups[1].Value + access + m.Groups[2].Value);
                if (!string.IsNullOrEmpty(refresh))
                    c = Regex.Replace(c, "(\"refreshToken\"\\s*:\\s*\")[^\"]*(\")",
                                      m => m.Groups[1].Value + refresh + m.Groups[2].Value);
                if (expMs > 0)
                    c = Regex.Replace(c, "(\"expiresAt\"\\s*:\\s*)[0-9]+",
                                      m => m.Groups[1].Value + expMs.ToString());
                File.WriteAllText(credPath, c, new UTF8Encoding(false));
            }
            catch { }
        }

        void SetBackoff(WebException we, DateTime now)
        {
            int ra = 0;
            try
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null)
                {
                    string h = hr.Headers["Retry-After"];
                    if (!string.IsNullOrEmpty(h)) int.TryParse(h, out ra);
                }
            }
            catch { }
            backoffSec = backoffSec == 0 ? 120 : Math.Min(backoffSec * 2, 900);
            nextAllowedUtc = now.AddSeconds(Math.Max(backoffSec, ra));
        }

        static int StatusOf(WebException we)
        {
            HttpWebResponse hr = we.Response as HttpWebResponse;
            return hr != null ? (int)hr.StatusCode : 0;
        }

        static string SafeRead(string p)
        {
            try { return File.ReadAllText(p); } catch { return ""; }
        }

        void Fail(string msg)
        {
            if (haveData) { stale = true; status = msg; }
            else { status = msg; }
            Invoke2();
        }

        void Invoke2()
        {
            if (IsHandleCreated) { try { BeginInvoke((Action)(() => Invalidate())); } catch { } }
        }

        static string Match1(string s, string pat)
        {
            var m = Regex.Match(s, pat);
            return m.Success ? m.Groups[1].Value : "";
        }

        static Stat ParseStat(string json, string key)
        {
            var st = new Stat();
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\{([^}]*)\\}");
            if (!m.Success) return st;
            string seg = m.Groups[1].Value;
            string u = Match1(seg, "\"utilization\"\\s*:\\s*([0-9.]+)");
            if (u != "") st.Util = double.Parse(u, CultureInfo.InvariantCulture);
            string ra = Match1(seg, "\"resets_at\"\\s*:\\s*\"([^\"]+)\"");
            if (ra != "")
            {
                DateTimeOffset r = DateTimeOffset.Parse(ra, CultureInfo.InvariantCulture);
                st.RemainMin = Math.Max(0, (int)Math.Round((r.UtcDateTime - DateTime.UtcNow).TotalMinutes));
                st.Reset = r.LocalDateTime.ToString("MM/dd HH:mm");
            }
            return st;
        }

        static Color BarColor(double pct)
        {
            if (pct < 70) return C_OK;
            if (pct < 90) return C_WARN;
            return C_BAD;
        }

        static string RemTxt(int min)
        {
            int d = min / 1440;
            int h = (min % 1440) / 60;
            int m = min % 60;
            if (d > 0) return d + "d " + h + "h " + m + "m";
            if (h > 0) return h + "h " + m + "m";
            return m + "m";
        }

        void LoadConfig()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude\\.widget_mode.txt");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length > 0)
                    {
                        int val;
                        if (int.TryParse(lines[0].Trim(), out val) && val >= 0 && val <= 2)
                        {
                            currentMode = (DisplayMode)val;
                        }
                    }
                    if (lines.Length > 1)
                    {
                        double op;
                        if (double.TryParse(lines[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out op) && op >= 0.1 && op <= 1.0)
                        {
                            currentOpacity = op;
                        }
                    }
                }
            }
            catch { }
        }

        void SaveConfig()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string path = Path.Combine(dir, ".widget_mode.txt");
                string[] lines = new string[] {
                    ((int)currentMode).ToString(),
                    currentOpacity.ToString(CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(path, lines);
            }
            catch { }
        }

        void SetOpacity(double op)
        {
            currentOpacity = op;
            Opacity = op;
            SaveConfig();
        }

        void ApplySizeAndRegion()
        {
            int targetW = largeWidth;
            int targetH = largeHeight;

            if (currentMode == DisplayMode.Small)
            {
                targetW = smallWidth;
                targetH = smallHeight;
            }
            else if (currentMode == DisplayMode.Large)
            {
                targetW = largeWidth;
                targetH = largeHeight;
            }
            else // Auto
            {
                if (isHovered)
                {
                    targetW = largeWidth;
                    targetH = largeHeight;
                }
                else
                {
                    targetW = smallWidth;
                    targetH = smallHeight;
                }
            }

            if (Width != targetW || Height != targetH)
            {
                int oldW = Width;
                int oldH = Height;
                int newX = Location.X + oldW - targetW;
                int newY = Location.Y + oldH - targetH;

                var screen = Screen.FromControl(this).WorkingArea;
                if (newX < screen.Left) newX = screen.Left;
                if (newY < screen.Top) newY = screen.Top;

                Location = new Point(newX, newY);
                Width = targetW;
                Height = targetH;
                bool isSmall = (currentMode == DisplayMode.Small) || (currentMode == DisplayMode.Auto && !isHovered);
                Region = RoundRegion(Width, Height, isSmall ? 10 : 14);
                Invalidate();
            }
        }

        void SetMode(DisplayMode mode)
        {
            currentMode = mode;
            SaveConfig();
            isHovered = false;
            ApplySizeAndRegion();
        }

        void HoverTimer_Tick(object sender, EventArgs e)
        {
            if (dragging) return;
            if (ContextMenuStrip != null && ContextMenuStrip.Visible) return;

            if (currentMode == DisplayMode.Auto)
            {
                bool contains = Bounds.Contains(Cursor.Position);
                if (contains != isHovered)
                {
                    isHovered = contains;
                    ApplySizeAndRegion();
                }
            }
        }

        void DrawSmallPanel(Graphics g)
        {
            using (var fPercent = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var fTime = new Font("Segoe UI", 8.0f))
            using (var fStatus = new Font("Segoe UI", 7.5f))
            using (var bTxt = new SolidBrush(C_TXT))
            using (var bSub = new SolidBrush(C_SUB))
            {
                if (!haveData)
                {
                    var rect = new RectangleF(6, (Height - 40) / 2.0f, Width - 12, 40);
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        using (var bw = new SolidBrush(C_WARN))
                            g.DrawString(status, fStatus, bw, rect, sf);
                    }
                    return;
                }

                float x = 8.0f;
                float y1 = 5.0f;
                float y2 = 23.0f;

                // Draw Line 1 (5H)
                double pct1 = five.Util < 0 ? 0 : five.Util;
                string pct1Str = Math.Round(pct1) + "%";
                string time1Str = RemTxt(five.RemainMin);

                var szPct1 = g.MeasureString(pct1Str, fPercent);

                using (var bp1 = new SolidBrush(BarColor(pct1)))
                {
                    g.DrawString(pct1Str, fPercent, bp1, x, y1);
                    g.DrawString(time1Str, fTime, bTxt, x + szPct1.Width + 4, y1 + 2.0f);
                }

                // Draw Line 2 (7D)
                double pct2 = seven.Util < 0 ? 0 : seven.Util;
                string pct2Str = Math.Round(pct2) + "%";
                string time2Str = RemTxt(seven.RemainMin);

                var szPct2 = g.MeasureString(pct2Str, fPercent);

                using (var bp2 = new SolidBrush(BarColor(pct2)))
                {
                    g.DrawString(pct2Str, fPercent, bp2, x, y2);
                    g.DrawString(time2Str, fTime, bTxt, x + szPct2.Width + 4, y2 + 2.0f);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(C_BG);

            Color borderCol = stale ? C_WARN : C_LINE;
            using (var pen = new Pen(borderCol)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            bool isSmall = (currentMode == DisplayMode.Small) || (currentMode == DisplayMode.Auto && !isHovered);
            if (isSmall)
            {
                DrawSmallPanel(g);
                return;
            }

            var fHead = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var fSub = new Font("Segoe UI", 7.5f);
            var fBig = new Font("Segoe UI", 19f, FontStyle.Bold);
            var fLabel = new Font("Segoe UI", 8f, FontStyle.Bold);
            var fTiny = new Font("Segoe UI", 7f);

            using (var bTxt = new SolidBrush(C_TXT))
            using (var bSub = new SolidBrush(C_SUB))
            {
                // header
                g.DrawString("CLAUDE 用量", fHead, bTxt, 14, 10);
                string hr = stale ? "⏳ 快取" : (haveData ? DateTime.Now.ToString("HH:mm:ss") : "");
                var hrSz = g.MeasureString(hr, fSub);
                using (var bHr = new SolidBrush(stale ? C_WARN : C_SUB))
                    g.DrawString(hr, fSub, bHr, Width - 14 - hrSz.Width, 12);
                if (sub != "")
                {
                    string s2 = sub.ToUpper();
                    g.DrawString(s2, fSub, bSub, 14, 26);
                }

                if (!haveData)
                {
                    using (var bw = new SolidBrush(C_WARN))
                        g.DrawString(status, fLabel, bw, 14, 70);
                    return;
                }

                DrawRow(g, 44, "5H 視窗", five, bTxt, bSub, fLabel, fBig, fTiny);
                DrawRow(g, 100, "7D 每週", seven, bTxt, bSub, fLabel, fBig, fTiny);

                if (stale && status != "")
                    using (var bw = new SolidBrush(C_WARN))
                        g.DrawString("⚠ " + status, fTiny, bw, 14, 152);
            }
        }

        void DrawRow(Graphics g, int y, string label, Stat st, Brush bTxt, Brush bSub,
                     Font fLabel, Font fBig, Font fTiny)
        {
            int pad = 14, w = Width - pad * 2;
            double pct = st.Util < 0 ? 0 : st.Util;
            g.DrawString(label, fLabel, bSub, pad, y);
            string ps = Math.Round(pct) + "%";
            var psSz = g.MeasureString(ps, fBig);
            using (var bp = new SolidBrush(BarColor(pct)))
                g.DrawString(ps, fBig, bp, Width - pad - psSz.Width, y - 6);
            // bar
            int by = y + 22, bh = 6;
            using (var bt = new SolidBrush(C_LINE))
                FillRound(g, bt, pad, by, w, bh, 3);
            int fw = (int)Math.Round(w * Math.Min(pct, 100) / 100.0);
            if (fw > 0) using (var bf = new SolidBrush(BarColor(pct)))
                FillRound(g, bf, pad, by, fw, bh, 3);
            // reset line
            string rt = "重置 " + RemTxt(st.RemainMin) + " · " + st.Reset;
            g.DrawString(rt, fTiny, bSub, pad, by + 9);
        }

        static void FillRound(Graphics g, Brush b, int x, int y, int w, int h, int r)
        {
            if (w < 2 * r) r = Math.Max(0, w / 2);
            var p = new GraphicsPath();
            p.AddArc(x, y, r, r, 180, 90);
            p.AddArc(x + w - r, y, r, r, 270, 90);
            p.AddArc(x + w - r, y + h - r, r, r, 0, 90);
            p.AddArc(x, y + h - r, r, r, 90, 90);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }
}
