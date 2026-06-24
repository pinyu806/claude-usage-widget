using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeUsageWidget
{
    static class Program
    {
        static Mutex singleInstance;

        [DllImport("user32.dll")] static extern int RegisterWindowMessage(string msg);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string win);
        // 第二實例靠此訊息把既有 widget 召回右下角（取代靜默退出）
        public static readonly uint WM_SHOW = (uint)RegisterWindowMessage("ClaudeUsageWidget_ShowExisting_v1");

        [STAThread]
        static void Main()
        {
            // 同機單例：避免開機自啟與手動雙開造成同帳號輪詢疊加 → 429
            bool createdNew;
            singleInstance = new Mutex(true, "ClaudeUsageWidget_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 已有實例在跑：定向通知它現身（移回右下角），自己退出，而非靜默
                // 註：HWND_BROADCAST 送不到 ShowInTaskbar=false 的工具視窗，故用 FindWindow 定向
                IntPtr ex = FindWindow(null, "Claude 用量");
                if (ex != IntPtr.Zero && WM_SHOW != 0) PostMessage(ex, WM_SHOW, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            // api.anthropic.com 需 TLS 1.2；只啟用 1.2，不向下相容 1.0/1.1（避免降級）
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Widget());
            GC.KeepAlive(singleInstance);
        }
    }

    class Stat { public double Util = -1; public int RemainMin = 0; public string Reset = "--"; public bool Found = false; }

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

        // 輪詢/節流參數（降低 429：成功後 5 分鐘才自動再打；右鍵「立即重新整理」可即時刷新）
        const int tickMs = 30000;     // timer 心跳：每 30s 檢查一次（多數心跳會被下方節流擋掉、不打 API）
        int refreshSec = 300;         // 成功後幾秒內不自動再打（穩定時的實際更新頻率；可由選單調整）
        const int minRetrySec = 20;   // 任何觸發的最短重試間隔（防養熱）
        const int alertPct = 90;      // 用量首次跨過此百分比時跳系統匣通知

        // 開機自啟（寫 HKCU Run；選單版，取代外部 .cmd）
        const string RUN_KEY = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RUN_NAME = "ClaudeUsageWidget";
        // 穩定時實際每 5 分鐘打一次；失敗/尚無資料時靠 30s 心跳快速重試（受 429 退避保護），避免首筆資料等太久

        // 保護「顯示用」共享狀態（背景緒寫、UI 緒讀）＋ fetch reentrancy
        readonly object stateLock = new object();
        bool fetching = false;
        bool fetchManual = false;

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
        string planOverride = "";   // 自訂方案顯示文字（空＝用 credentials 的 subscriptionType）
        bool isHovered = false;
        const int smallWidth = 106;
        const int smallHeight = 46;
        const int largeWidth = 260;
        const int largeHeight = 172;
        System.Windows.Forms.Timer hoverTimer;

        // 重用的字型（建立一次，避免每次 OnPaint new Font 造成 GDI 物件累積）
        readonly Font fHead = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        readonly Font fSub = new Font("Segoe UI", 7.5f);
        readonly Font fBig = new Font("Segoe UI", 19f, FontStyle.Bold);
        readonly Font fLabel = new Font("Segoe UI", 8f, FontStyle.Bold);
        readonly Font fTiny = new Font("Segoe UI", 7f);
        readonly Font fPercent = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        readonly Font fTime = new Font("Segoe UI", 8.0f);

        NotifyIcon tray;
        bool alerted5 = false, alerted7 = false;  // 通知去重：跨過閾值通知一次，回落後重置

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
            menu.Items.Add("立即重新整理", null, (s, e) => Refresh2(true));
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

            var mPlan = new ToolStripMenuItem("方案顯示");
            var mPlanAuto = new ToolStripMenuItem("自動 (依帳號)", null, (s, e) => SetPlan(""));
            var mPlanPro = new ToolStripMenuItem("Pro", null, (s, e) => SetPlan("Pro"));
            var mPlanMax5 = new ToolStripMenuItem("Max 5x", null, (s, e) => SetPlan("Max 5x"));
            var mPlanMax20 = new ToolStripMenuItem("Max 20x", null, (s, e) => SetPlan("Max 20x"));
            var mPlanTeam = new ToolStripMenuItem("Team", null, (s, e) => SetPlan("Team"));
            var mPlanCustom = new ToolStripMenuItem("自訂…", null, (s, e) => SetPlanCustom());
            mPlan.DropDownItems.Add(mPlanAuto);
            mPlan.DropDownItems.Add(mPlanPro);
            mPlan.DropDownItems.Add(mPlanMax5);
            mPlan.DropDownItems.Add(mPlanMax20);
            mPlan.DropDownItems.Add(mPlanTeam);
            mPlan.DropDownItems.Add(mPlanCustom);

            var mInterval = new ToolStripMenuItem("更新頻率");
            var mInt1 = new ToolStripMenuItem("1 分鐘 (較易 429)", null, (s, e) => SetInterval(60));
            var mInt5 = new ToolStripMenuItem("5 分鐘", null, (s, e) => SetInterval(300));
            var mInt10 = new ToolStripMenuItem("10 分鐘", null, (s, e) => SetInterval(600));
            mInterval.DropDownItems.Add(mInt1);
            mInterval.DropDownItems.Add(mInt5);
            mInterval.DropDownItems.Add(mInt10);

            var mAutoStart = new ToolStripMenuItem("開機時啟動", null, (s, e) => ToggleAutoStart());

            menu.Items.Add(mSmall);
            menu.Items.Add(mLarge);
            menu.Items.Add(mAuto);
            menu.Items.Add(mOpacity);
            menu.Items.Add(mPlan);
            menu.Items.Add(mInterval);
            menu.Items.Add(mAutoStart);
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

                mPlanAuto.Checked = planOverride == "";
                mPlanPro.Checked = planOverride == "Pro";
                mPlanMax5.Checked = planOverride == "Max 5x";
                mPlanMax20.Checked = planOverride == "Max 20x";
                mPlanTeam.Checked = planOverride == "Team";
                mPlanCustom.Checked = planOverride != "" && planOverride != "Pro" && planOverride != "Max 5x" && planOverride != "Max 20x" && planOverride != "Team";

                mInt1.Checked = refreshSec == 60;
                mInt5.Checked = refreshSec == 300;
                mInt10.Checked = refreshSec == 600;
                mAutoStart.Checked = IsAutoStart();
            };

            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragOff = e.Location; } };
            MouseMove += (s, e) => { if (dragging) Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y); };
            MouseUp += (s, e) => dragging = false;

            timer = new System.Windows.Forms.Timer(); timer.Interval = tickMs; timer.Tick += (s, e) => Refresh2(false);
            timer.Start();

            hoverTimer = new System.Windows.Forms.Timer(); hoverTimer.Interval = 100; hoverTimer.Tick += HoverTimer_Tick;
            hoverTimer.Start();

            // 系統匣圖示：供「接近上限」氣球通知；雙擊召回 widget；右鍵同選單
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = "Claude 用量";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowAtDefault();

            // widget 本體雙擊 → 開官方用量頁
            DoubleClick += (s, e) => OpenUsage();

            Refresh2(false);
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

        // 單一 fetch 在途：避免多執行緒同時讀寫憑證 / 競爭刷新
        // manual=true（右鍵立即重新整理）會略過「成功快取窗」，但仍守最短重試與 429 退避
        void Refresh2(bool manual)
        {
            lock (stateLock)
            {
                if (fetching) return;
                fetching = true;
                fetchManual = manual;
            }
            var t = new Thread(Fetch); t.IsBackground = true; t.Start();
        }

        void Fetch()
        {
            bool manual;
            lock (stateLock) { manual = fetchManual; }
            try
            {
                DateTime now = DateTime.UtcNow;
                // 429 退避：冷卻期內不打 usage（仍重繪倒數）
                if (haveData && now < nextAllowedUtc) { Invoke2(); return; }
                // 成功後更新窗：自動觸發時距上次成功不到 refreshSec 則沿用快取（手動刷新略過此窗）
                if (!manual && haveData && (now - lastOkUtc).TotalSeconds < refreshSec && !stale && backoffSec == 0) { Invoke2(); return; }
                // 最短重試間隔（防養熱）：任何觸發都守
                if (haveData && (now - lastTryUtc).TotalSeconds < minRetrySec) { Invoke2(); return; }
                lastTryUtc = now;
                try
                {
                    object cred = ParseJson(File.ReadAllText(credPath));
                    object oauth = GetVal(cred, "claudeAiOauth");
                    string tok = GetStr(oauth, "accessToken");
                    string rtok = GetStr(oauth, "refreshToken");
                    string subType = GetStr(oauth, "subscriptionType");
                    object expV = GetVal(oauth, "expiresAt");
                    if (string.IsNullOrEmpty(tok)) { Fail("找不到 token"); return; }

                    // 到期前 60s 主動 refresh（避免直接吃 401）
                    long nowMs = (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
                    if (expV != null && !string.IsNullOrEmpty(rtok))
                    {
                        long expMs = 0;
                        try { expMs = Convert.ToInt64(expV); } catch { }
                        if (expMs > 0 && expMs - nowMs < 60000)
                        {
                            string nt = TryRefresh(rtok, now);
                            if (nt != null) tok = nt;
                        }
                    }

                    object root = ParseJson(GetUsage(tok));
                    Stat f = ParseStat(root, "five_hour");
                    Stat s = ParseStat(root, "seven_day");
                    if (!f.Found && !s.Found) { Fail("資料格式異常（API 可能已變更）"); return; }
                    lock (stateLock)
                    {
                        five = f; seven = s; sub = subType == null ? "" : subType;
                        haveData = true; stale = false; status = "";
                        backoffSec = 0; nextAllowedUtc = DateTime.MinValue;
                    }
                    lastOkUtc = DateTime.UtcNow;
                }
                catch (WebException we)
                {
                    int code = StatusOf(we);
                    if (code == 429) { SetBackoff(we, now); Fail("限流(429)"); }
                    else if (code == 401 || code == 403)
                    {
                        // token 過期 → 試 refresh 一次再重打 usage
                        string rtok = GetStr(GetVal(SafeParse(credPath), "claudeAiOauth"), "refreshToken");
                        string nt = !string.IsNullOrEmpty(rtok) ? TryRefresh(rtok, now) : null;
                        bool ok = false;
                        if (nt != null)
                        {
                            try
                            {
                                object root = ParseJson(GetUsage(nt));
                                Stat f = ParseStat(root, "five_hour");
                                Stat s = ParseStat(root, "seven_day");
                                if (f.Found || s.Found)
                                {
                                    lock (stateLock)
                                    {
                                        five = f; seven = s;
                                        haveData = true; stale = false; status = "";
                                        backoffSec = 0; nextAllowedUtc = DateTime.MinValue;
                                    }
                                    lastOkUtc = DateTime.UtcNow;
                                    ok = true;
                                }
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
            finally
            {
                lock (stateLock) { fetching = false; }
            }
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
                object r = ParseJson(body);
                string na = GetStr(r, "access_token");
                if (string.IsNullOrEmpty(na)) { refreshNextUtc = now.AddSeconds(60); return null; }
                string nr = GetStr(r, "refresh_token");
                object eiV = GetVal(r, "expires_in");
                long newExp = 0;
                if (eiV != null)
                {
                    try { newExp = (long)(DateTime.UtcNow - Epoch).TotalMilliseconds + Convert.ToInt64(eiV) * 1000L; }
                    catch { }
                }
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

        // 只替換三個欄位值，保留其餘 JSON 結構；UTF-8 無 BOM；原子置換避免寫一半毀憑證
        void WriteCreds(string access, string refresh, long expMs)
        {
            string tmp = credPath + ".tmp";
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
                File.WriteAllText(tmp, c, new UTF8Encoding(false));
                // 同卷原子置換：避免行程中斷導致 .credentials.json 被截斷
                if (File.Exists(credPath)) File.Replace(tmp, credPath, null);
                else File.Move(tmp, credPath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
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

        static object SafeParse(string p)
        {
            try { return ParseJson(File.ReadAllText(p)); } catch { return null; }
        }

        void Fail(string msg)
        {
            lock (stateLock)
            {
                if (haveData) { stale = true; status = msg; }
                else { status = msg; }
            }
            Invoke2();
        }

        void Invoke2()
        {
            if (IsHandleCreated) { try { BeginInvoke((Action)(() => { Invalidate(); CheckAlerts(); })); } catch { } }
        }

        // --- JSON helpers（用 JavaScriptSerializer 取代脆弱的 regex 解析）---
        static object ParseJson(string s)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser.DeserializeObject(s);
        }

        static object GetVal(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static string GetStr(object o, string key)
        {
            object v = GetVal(o, key);
            return v == null ? null : v.ToString();
        }

        static Stat ParseStat(object root, string key)
        {
            var st = new Stat();
            object seg = GetVal(root, key);
            if (seg == null) return st;
            st.Found = true;
            object u = GetVal(seg, "utilization");
            if (u != null)
            {
                try { st.Util = Convert.ToDouble(u, CultureInfo.InvariantCulture); } catch { }
            }
            string ra = GetStr(seg, "resets_at");
            if (!string.IsNullOrEmpty(ra))
            {
                // 單獨容錯：resets_at 格式異常時保留已解析的百分比，僅倒數留預設，不毀整次 fetch
                try
                {
                    DateTimeOffset r = DateTimeOffset.Parse(ra, CultureInfo.InvariantCulture);
                    st.RemainMin = Math.Max(0, (int)Math.Round((r.UtcDateTime - DateTime.UtcNow).TotalMinutes));
                    st.Reset = r.LocalDateTime.ToString("MM/dd HH:mm");
                }
                catch { }
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
                    if (lines.Length > 2)
                    {
                        planOverride = lines[2].Trim();
                    }
                    if (lines.Length > 3)
                    {
                        int sec;
                        if (int.TryParse(lines[3].Trim(), out sec) && sec >= 30 && sec <= 3600)
                        {
                            refreshSec = sec;
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
                    currentOpacity.ToString(CultureInfo.InvariantCulture),
                    planOverride,
                    refreshSec.ToString()
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

        void SetPlan(string text)
        {
            planOverride = text == null ? "" : text;
            SaveConfig();
            Invalidate();
        }

        void SetPlanCustom()
        {
            string r = PromptText("自訂方案顯示文字（留空＝自動依帳號）", planOverride);
            if (r != null) SetPlan(r);
        }

        static string PromptText(string caption, string current)
        {
            using (var f = new Form())
            {
                f.Text = caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(300, 96);
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.TopMost = true;
                var tb = new TextBox(); tb.Text = current == null ? "" : current;
                tb.Left = 12; tb.Top = 14; tb.Width = 276;
                var ok = new Button(); ok.Text = "確定"; ok.Width = 75; ok.Left = 132; ok.Top = 52; ok.DialogResult = DialogResult.OK;
                var cancel = new Button(); cancel.Text = "取消"; cancel.Width = 75; cancel.Left = 213; cancel.Top = 52; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        void SetInterval(int sec)
        {
            refreshSec = sec;
            SaveConfig();
        }

        static bool IsAutoStart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false)) return k != null && k.GetValue(RUN_NAME) != null; }
            catch { return false; }
        }

        void ToggleAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null) return;
                    if (k.GetValue(RUN_NAME) != null) k.DeleteValue(RUN_NAME, false);
                    else k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch { }
        }

        void OpenUsage()
        {
            try { System.Diagnostics.Process.Start("https://claude.ai/settings/usage"); }
            catch { }
        }

        void ShowAtDefault()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            TopMost = false; TopMost = true;
            Invalidate();
        }

        // UI 緒：用量首次跨過閾值跳系統匣通知（回落後重置，下個週期可再通知）
        void CheckAlerts()
        {
            double u5, u7; bool hd;
            lock (stateLock) { u5 = five.Util; u7 = seven.Util; hd = haveData; }
            if (!hd) return;
            CheckOne(u5, ref alerted5, "5 小時視窗");
            CheckOne(u7, ref alerted7, "7 天每週");
        }

        void CheckOne(double util, ref bool flag, string label)
        {
            if (util >= alertPct)
            {
                if (!flag)
                {
                    flag = true;
                    if (tray != null)
                    {
                        tray.BalloonTipTitle = "Claude 用量警示";
                        tray.BalloonTipText = label + " 用量已達 " + Math.Round(util) + "%";
                        tray.ShowBalloonTip(5000);
                    }
                }
            }
            else flag = false;
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

        void DrawSmallPanel(Graphics g, bool hd, string statusText, Stat f, Stat s)
        {
            using (var bTxt = new SolidBrush(C_TXT))
            {
                if (!hd)
                {
                    var rect = new RectangleF(6, (Height - 40) / 2.0f, Width - 12, 40);
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        using (var bw = new SolidBrush(C_WARN))
                            g.DrawString(statusText, fSub, bw, rect, sf);
                    }
                    return;
                }

                float x = 8.0f;
                float y1 = 5.0f;
                float y2 = 23.0f;

                // Draw Line 1 (5H)
                double pct1 = f.Util < 0 ? 0 : f.Util;
                string pct1Str = Math.Round(pct1) + "%";
                string time1Str = RemTxt(f.RemainMin);

                var szPct1 = g.MeasureString(pct1Str, fPercent);

                using (var bp1 = new SolidBrush(BarColor(pct1)))
                {
                    g.DrawString(pct1Str, fPercent, bp1, x, y1);
                    g.DrawString(time1Str, fTime, bTxt, x + szPct1.Width + 4, y1 + 2.0f);
                }

                // Draw Line 2 (7D)
                double pct2 = s.Util < 0 ? 0 : s.Util;
                string pct2Str = Math.Round(pct2) + "%";
                string time2Str = RemTxt(s.RemainMin);

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

            // snapshot 共享狀態，避免與背景 fetch 緒 torn read
            Stat f, s; string st, sb2; bool sl, hd;
            lock (stateLock) { f = five; s = seven; st = status; sb2 = sub; sl = stale; hd = haveData; }

            Color borderCol = sl ? C_WARN : C_LINE;
            using (var pen = new Pen(borderCol)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            bool isSmall = (currentMode == DisplayMode.Small) || (currentMode == DisplayMode.Auto && !isHovered);
            if (isSmall)
            {
                DrawSmallPanel(g, hd, st, f, s);
                return;
            }

            using (var bTxt = new SolidBrush(C_TXT))
            using (var bSub = new SolidBrush(C_SUB))
            {
                // header
                g.DrawString("CLAUDE 用量", fHead, bTxt, 14, 10);
                string hr = sl ? "⏳ 快取" : (hd ? DateTime.Now.ToString("HH:mm:ss") : "");
                var hrSz = g.MeasureString(hr, fSub);
                using (var bHr = new SolidBrush(sl ? C_WARN : C_SUB))
                    g.DrawString(hr, fSub, bHr, Width - 14 - hrSz.Width, 12);
                string planText = planOverride != "" ? planOverride : (sb2 != "" ? sb2.ToUpper() : "");
                if (planText != "")
                    g.DrawString(planText, fSub, bSub, 14, 26);

                if (!hd)
                {
                    using (var bw = new SolidBrush(C_WARN))
                        g.DrawString(st, fLabel, bw, 14, 70);
                    return;
                }

                DrawRow(g, 44, "5H 視窗", f, bTxt, bSub, fLabel, fBig, fTiny);
                DrawRow(g, 100, "7D 每週", s, bTxt, bSub, fLabel, fBig, fTiny);

                if (sl && st != "")
                    using (var bw = new SolidBrush(C_WARN))
                        g.DrawString("⚠ " + st, fTiny, bw, 14, 152);
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

        protected override void WndProc(ref Message m)
        {
            // 第二實例廣播：把走失/被蓋住的 widget 召回右下角現身
            if (Program.WM_SHOW != 0 && m.Msg == (int)Program.WM_SHOW)
                ShowAtDefault();
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (fHead != null) fHead.Dispose();
                if (fSub != null) fSub.Dispose();
                if (fBig != null) fBig.Dispose();
                if (fLabel != null) fLabel.Dispose();
                if (fTiny != null) fTiny.Dispose();
                if (fPercent != null) fPercent.Dispose();
                if (fTime != null) fTime.Dispose();
                if (timer != null) timer.Dispose();
                if (hoverTimer != null) hoverTimer.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
