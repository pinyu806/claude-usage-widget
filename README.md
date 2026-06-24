# Claude 用量 Widget

桌面常駐小工具，即時顯示 Claude Code 官方用量額度（5 小時視窗 / 7 天每週），與 `/usage` 同一份數字。

---

## ⚠️ 免責聲明（請先閱讀）

- **非官方工具**：本工具呼叫的 `GET /api/oauth/usage` 與 token refresh 端點皆為 **Anthropic 未公開、未承諾相容的內部端點**，並沿用 Claude Code 的公開 OAuth `client_id`。Anthropic 隨時可能變更或停用，屆時本工具會失效；也可能與服務條款有所牴觸，**請自行評估風險後使用**。
- **會讀寫你的登入憑證**：本工具會讀取 `~/.claude/.credentials.json` 取得 OAuth token，並在 token 快過期時**改寫**該檔（更新 accessToken / refreshToken / expiresAt 三個欄位，採原子置換寫入）。所有處理皆在**本機**完成，**不上傳任何第三方**。
- **不負擔保**：依 MIT 授權「按現狀」提供，使用後果自負（詳見 [LICENSE](LICENSE) 與 [SECURITY.md](SECURITY.md)）。

> 建議自行檢視 [`src/ClaudeUsageWidget.cs`](src/ClaudeUsageWidget.cs) 原始碼後再使用，或自行編譯（見下方）。

---

## 特色功能

- **多種面板模式**：
  - **小面板** (106x46)：極簡精簡版，僅顯示 5H 與 7D 用量百分比與重置倒數，極省桌面空間。
  - **大面板** (260x172)：完整詳細版，包含各時段用量百分比、進度條、剩餘時間、具體重置時間、訂閱類型與異常警示。
  - **自動模式（預設）**：平常以精簡的「小面板」呈現，當滑鼠指標移到小工具上（Hover）時，會自動展開成「大面板」，滑鼠離開後會自動縮回。
- **透明度調整**：支援 10% - 100% 透明度設定，可完美融入桌面背景。
- **設定自動存檔**：自動將您的「顯示模式」與「透明度」儲存於本機，下次啟動時自動載入。
- **自動 Token 刷新**：Token 快過期時會自動使用本機的 `refreshToken` 換取新 Token 並寫回憑證檔，無需手動重新登入。
- **接近上限通知**：5H 或 7D 用量首次超過 90% 時，跳一次 Windows 系統匣通知提醒。
- **防限流指數退避**：當 API 被限流 (429) 時，會自動進行指數退避 (Exponential Backoff)，避免持續頻繁呼叫加劇限流。
- **介面優化**：圓角無邊框、預設置頂、可滑鼠左鍵拖曳移動位置、滑鼠右鍵選單。

## 前置需求

1. Windows 10 / 11（內建 .NET Framework 4.x，免安裝）
2. 已安裝並登入 **Claude Code**（CLI），確保存在：
   `C:\Users\<你的帳號>\.claude\.credentials.json`
   小工具會讀取此檔案中的 OAuth token 來呼叫 Anthropic 官方端點。

## 取得與執行

本 repo **不再附帶預編譯的 `.exe`**（避免要求使用者執行無法驗證的二進位檔）。請二選一：

- **A. 自行編譯**（推薦，最安心）：見下方「重新編譯」。
- **B. 下載 Release**：至 [Releases](../../releases) 下載 `ClaudeUsageWidget.exe`，雙擊執行。

> Release 的 exe 由本 repo 原始碼以下方指令編譯。若不放心，請改用 A 自行編譯比對。

### 右鍵選單功能
在小工具上按滑鼠右鍵，可進行以下設定：
- **立即重新整理**：手動即時更新當前用量。
- **小面板** / **大面板** / **自動模式**：切換面板顯示樣式。
- **透明度**：調整視窗不透明度（10% ~ 100%）。
- **方案顯示**：選擇大面板顯示的訂閱方案文字（自動／Pro／Max 5x／Max 20x／Team／自訂）。因為官方 API 與本機憑證都沒有可靠的真實方案來源（例如 Max 用戶的 `subscriptionType` 常被標成 `pro`），可在此手動指定正確文字。
- **更新頻率**：背景自動更新間隔（1／5／10 分鐘）。預設 5 分鐘；1 分鐘較即時但較易觸發限流 (429)。
- **開機時啟動**：勾選後登入時自動啟動（寫入 HKCU Run，免用 .cmd）。
- **關閉**：結束小工具程式。

### 系統匣圖示與雙擊操作

- 工作列**系統匣**會有一個圖示：用量接近上限時由它跳通知；**雙擊系統匣圖示**可把小工具召回右下角。
- **雙擊小工具本體**會開啟官方用量頁面。

### 設定檔路徑
面板模式與透明度設定會儲存於：
`C:\Users\<你的帳號>\.claude\.widget_mode.txt`
- 第一行：面板模式代碼（0 = 小面板，1 = 大面板，2 = 自動模式）
- 第二行：透明度數值（0.1 ~ 1.0）
- 第三行：自訂方案顯示文字（留空＝自動使用憑證的 `subscriptionType`）
- 第四行：背景更新間隔秒數（30 ~ 3600；預設 300）

### 開機自動啟動

- **推薦：右鍵選單**「開機時啟動」勾選即可（寫入登錄，免額外檔案）。
- 或：雙擊 `安裝開機自啟.cmd` 建立啟動捷徑、`移除開機自啟.cmd` 移除（指向同資料夾的 `ClaudeUsageWidget.exe`）。

## 顯示說明（大面板）

| 區塊 | 說明 |
|------|------|
| 標題列右側 | 最後更新時間；若顯示「⏳ 快取」表示當前連線失敗，正顯示上一次成功取得的資料 |
| 訂閱類型 | 顯示當前帳號類型（例如 PRO） |
| 5H 視窗 | 5 小時滾動視窗使用率百分比、視覺化進度條、重置倒數與具體重置時間點 |
| 7D 每週 | 7 天每週使用率百分比、視覺化進度條、重置倒數與具體重置時間點 |
| 底部 ⚠ 字樣 | 當出現錯誤（如限流 429、Token 失效需重新登入、資料格式異常等）時的異常提示 |

## 原理

- **用量獲取**：發送 `GET https://api.anthropic.com/api/oauth/usage`（Header 帶 Bearer Access Token）。回應以 JSON 反序列化解析（`five_hour` / `seven_day` 的 `utilization` 與 `resets_at`）。
- **自動重新整理**：成功後最多每 5 分鐘更新一次（可由右鍵「更新頻率」調整；以 30 秒心跳檢查、失敗時快速重試）。同一時間只允許一個背景請求在途，避免並發競爭。
- **Token 刷新**：當發現 `expiresAt` 即將到期（60 秒內）或 API 回傳 401/403 時，會發送 `POST https://console.anthropic.com/v1/oauth/token` 換取新 Token，並以**原子置換**方式更新本機的 `.credentials.json`（只改三個欄位，保留其餘結構，UTF-8 無 BOM）。

## 疑難排解

| 現象 | 處理方式 |
|------|------|
| 顯示「找不到 token」 | 尚未登入 Claude Code。請先在命令提示字元跑 `claude` 登入一次。 |
| 顯示「請開 Claude Code 重新登入」 | Refresh Token 也已過期。請在終端機執行 `claude` 重新登入。 |
| 顯示「限流(429)」 | 官方 API 端點暫時限制呼叫。小工具會自動進入冷卻退避時間，稍候會自行重試。 |
| 顯示「資料格式異常」 | API 回應格式可能已變更（欄位改名等）。請至 Issues 回報。 |
| 小工具移出螢幕外不見了 | **再執行一次 exe 即可**：因為是單一實例，它不會開新視窗，而是把既有的小工具召回到右下角預設位置。 |
| 方案顯示不對（例如 Max 卻顯示 PRO） | 官方憑證的 `subscriptionType` 對 Max 用戶常標成 `pro`。請右鍵 →「方案顯示」選擇正確方案或自訂文字。 |

## 重新編譯（開發用）

若您修改了原始碼，可在 PowerShell 中執行以下指令重新編譯：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /out:ClaudeUsageWidget.exe `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  src\ClaudeUsageWidget.cs
```

> ⚠️ **注意**：該編譯器為 C# 5.0，不可使用 C# 6.0 以上的新語法（例如字串插值 `$""`、空值條件運算子 `?.`、模式匹配 `is X y` 等）。

## 授權

[MIT](LICENSE)
