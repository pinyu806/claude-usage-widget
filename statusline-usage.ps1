# Claude Code statusLine：把 stdin JSON 的 rate_limits 寫進 ~/.claude/.usage_cache.json 給 widget 讀，並輸出一行簡短用量。
# 設定（~/.claude/settings.json）：
#   "statusLine": { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"<path>\\statusline-usage.ps1\"" }
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE '.claude'
try {
    $u8 = New-Object System.Text.UTF8Encoding($false)
    $sr = New-Object IO.StreamReader([Console]::OpenStandardInput(), $u8)
    $raw = $sr.ReadToEnd()
    $j = $raw | ConvertFrom-Json
    # rate_limits 只在訂閱帳號、且該 session 收到第一次 API 回應後才出現
    $rl = $j.rate_limits

    if (-not $rl) { exit 0 }

    $now = [long]([DateTime]::UtcNow - (New-Object DateTime(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))).TotalMilliseconds
    $out = New-Object PSObject -Property @{ updatedAt = $now; rate_limits = $rl }
    $path = Join-Path $dir '.usage_cache.json'
    $tmp = $path + '.tmp'
    [IO.File]::WriteAllText($tmp, ($out | ConvertTo-Json -Depth 5 -Compress), $u8)
    if (Test-Path -LiteralPath $path) { [IO.File]::Replace($tmp, $path, $null) } else { [IO.File]::Move($tmp, $path) }

    $parts = @()
    if ($rl.five_hour) { $parts += ('5H {0}%' -f [Math]::Round([double]$rl.five_hour.used_percentage)) }
    if ($rl.seven_day) { $parts += ('7D {0}%' -f [Math]::Round([double]$rl.seven_day.used_percentage)) }
    Write-Output ($parts -join ' | ')
} catch { exit 0 }
