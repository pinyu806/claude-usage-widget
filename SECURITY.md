# 安全性說明 / Security

本工具會接觸到你的 Claude Code 登入憑證，這份文件說明它**實際做了什麼**，方便你自行評估風險。

## 它讀什麼、寫什麼

| 檔案 | 動作 | 說明 |
|------|------|------|
| `~/.claude/.credentials.json` | **讀** | 取得 `accessToken` / `refreshToken` / `subscriptionType` / `expiresAt` |
| `~/.claude/.credentials.json` | **寫** | 僅在 token 快過期或被拒（401/403）時，更新其中 `accessToken` / `refreshToken` / `expiresAt` 三個欄位；其餘欄位原樣保留 |
| `~/.claude/.widget_mode.txt` | 讀 / 寫 | 儲存顯示模式與透明度（非敏感） |

憑證寫回採**原子置換**（先寫入 `.credentials.json.tmp`，再以 `File.Replace` 置換），避免行程中斷導致原檔被截斷而無法登入。同一時間僅允許一個背景請求在途，避免多執行緒同時改寫憑證。

## 它連到哪裡

| 端點 | 用途 |
|------|------|
| `GET https://api.anthropic.com/api/oauth/usage` | 取得用量（Authorization: Bearer） |
| `POST https://console.anthropic.com/v1/oauth/token` | token refresh（沿用 Claude Code 公開 `client_id`） |

連線一律使用 TLS 1.2。**除上述 Anthropic 端點外，本工具不會把任何資料送往其他位址。** 沒有遙測、沒有第三方伺服器。

## 非官方 / ToS

上述端點與 `client_id` 為 Anthropic 未公開、未承諾相容的內部介面。使用本工具可能與服務條款有所牴觸，且 Anthropic 隨時可變更使本工具失效。**請自行評估後使用，後果自負。**

## 信任與驗證

- 全部行為集中於單一檔案 [`src/ClaudeUsageWidget.cs`](src/ClaudeUsageWidget.cs)，建議自行檢視。
- Repo **不在版本控制中存放預編譯 exe**；Release 的 exe 由原始碼以 README 指令編譯。不放心請自行編譯比對。

## 回報問題

發現安全疑慮請開 [Issue](../../issues)（請勿在 issue 內貼出任何 token 或憑證內容）。
