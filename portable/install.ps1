$ErrorActionPreference = 'Stop'
$scriptPath = $MyInvocation.MyCommand.Path

# 任何未預期的錯誤：顯示原因並停住，絕不閃退
trap {
    Write-Host ''
    Write-Host ('發生錯誤：' + $_.Exception.Message) -ForegroundColor Red
    Write-Host '安裝未完成。可截圖此視窗到回報串尋求協助。' -ForegroundColor Yellow
    Write-Host ''
    Read-Host '按 Enter 關閉'
    exit 1
}

try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}

Write-Host ''
Write-Host '================================================' -ForegroundColor Cyan
Write-Host '  SpiritVale 紫星販賣保護 - 安裝' -ForegroundColor Cyan
Write-Host '================================================' -ForegroundColor Cyan
Write-Host ''

$root = Split-Path -Parent $scriptPath
$dll  = Join-Path $root 'SpiritValeSellFavorite.dll'

if (-not (Test-Path $dll)) {
    Write-Host '找不到 SpiritValeSellFavorite.dll。' -ForegroundColor Red
    Write-Host '請確認壓縮檔已「完整解壓縮」，且所有檔案放在同一個資料夾內。' -ForegroundColor Yellow
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}

function Find-Game {
    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch {}
    if (-not $steam) { try { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\Wow6432Node\Valve\Steam' -ErrorAction Stop).InstallPath } catch {} }
    $libs = New-Object System.Collections.ArrayList
    if ($steam) {
        [void]$libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"(.+?)"')) {
                [void]$libs.Add($m.Groups[1].Value.Replace('\\', '\'))
            }
        }
    }
    foreach ($l in $libs) {
        $p = Join-Path $l 'steamapps\common\SpiritVale'
        if (Test-Path (Join-Path $p 'SpiritVale.exe')) { return $p }
    }
    return $null
}

$game = Find-Game
if ($game) {
    Write-Host ('自動偵測到遊戲位置：' + $game) -ForegroundColor Green
} else {
    Write-Host '找不到 SpiritVale 安裝位置。' -ForegroundColor Yellow
    Write-Host '請把遊戲資料夾路徑貼上（Steam 右鍵遊戲 > 管理 > 瀏覽本機檔案）：'
    $game = (Read-Host '路徑').Trim('"').Trim()
}

if (-not (Test-Path (Join-Path $game 'SpiritVale.exe'))) {
    Write-Host '這個路徑裡沒有 SpiritVale.exe，安裝取消。' -ForegroundColor Red
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}
if (Get-Process 'SpiritVale' -ErrorAction SilentlyContinue) {
    Write-Host '偵測到遊戲正在執行中！請「完全關閉遊戲」後再執行一次。' -ForegroundColor Red
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}

# ---- 權限檢查：遊戲裝在 Program Files 等位置時需要管理員權限，自動提權重跑 ----
$canWrite = $true
$testFile = Join-Path $game ('.__writetest_' + [IO.Path]::GetRandomFileName())
try { Set-Content -Path $testFile -Value 'x' -ErrorAction Stop; Remove-Item $testFile -Force -ErrorAction SilentlyContinue } catch { $canWrite = $false }
if (-not $canWrite) {
    Write-Host ''
    Write-Host '遊戲目錄需要系統管理員權限，正在以管理員身分重新啟動安裝程式...' -ForegroundColor Yellow
    Write-Host '（等一下跳出的授權視窗請按「是」）' -ForegroundColor Yellow
    try {
        Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'))
        return
    } catch {
        Write-Host '未取得管理員授權，安裝取消。' -ForegroundColor Red
        Write-Host '請對「一鍵安裝.bat」按右鍵 →「以系統管理員身分執行」再試一次。' -ForegroundColor Yellow
        Write-Host ''; Read-Host '按 Enter 關閉'; return
    }
}

# ---- 步驟 1：BepInEx（沒裝就自動下載安裝）----
$bepCore = Join-Path $game 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
if (Test-Path $bepCore) {
    Write-Host '偵測到 BepInEx 已安裝，略過此步驟。' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host '本 Mod 需要 BepInEx 6（IL2CPP 版）框架，現在自動下載安裝（約 33 MB）...' -ForegroundColor Cyan
    $bepUrl = 'https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip'
    $bepZip = Join-Path $env:TEMP 'BepInEx-be785.zip'
    try {
        Invoke-WebRequest -Uri $bepUrl -OutFile $bepZip -UseBasicParsing
    } catch {
        Write-Host '下載 BepInEx 失敗，請檢查網路連線後重試。' -ForegroundColor Red
        Write-Host ('  ' + $_.Exception.Message) -ForegroundColor Gray
        Write-Host ''; Read-Host '按 Enter 關閉'; return
    }
    Expand-Archive -Path $bepZip -DestinationPath $game -Force
    Remove-Item $bepZip -Force -ErrorAction SilentlyContinue

    $cfgDir = Join-Path $game 'BepInEx\config'
    $cfg = Join-Path $cfgDir 'BepInEx.cfg'
    if (-not (Test-Path $cfg)) {
        New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
        @"
[Logging.Console]
Enabled = false
"@ | Set-Content -Path $cfg -Encoding UTF8
    }
    Write-Host 'BepInEx 安裝完成。' -ForegroundColor Green
}

# ---- 步驟 2：安裝本 Mod ----
$dst = Join-Path $game 'BepInEx\plugins\SpiritValeSellFavorite'
if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Force -Path $dst | Out-Null }
Copy-Item $dll $dst -Force

# ---- 步驟 3：驗證 ----
$installed = Join-Path $dst 'SpiritValeSellFavorite.dll'
if (-not (Test-Path $installed)) {
    Write-Host '安裝驗證失敗：檔案未成功複製。' -ForegroundColor Red
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}
$ver = (Get-Item $installed).VersionInfo.ProductVersion

Write-Host ''
Write-Host '================================================' -ForegroundColor Green
Write-Host ('  [OK] 安裝成功！（版本 v' + $ver + '，已驗證檔案落地）') -ForegroundColor Green
Write-Host '================================================' -ForegroundColor Green
Write-Host ('  位置：' + $dst) -ForegroundColor Gray
Write-Host ''
Write-Host '【重要】第一次啟動遊戲會多花 1~3 分鐘（框架初始化），' -ForegroundColor Yellow
Write-Host '        畫面全黑或停住是正常的，請耐心等待，之後就恢復正常速度。' -ForegroundColor Yellow
Write-Host ''
Write-Host '使用方式（都在商人販賣介面內，滑鼠停在物品上按 F）：' -ForegroundColor Cyan
Write-Host '  ・左側自己的背包按 F ＝ 標上／解除紫星 ★' -ForegroundColor Gray
Write-Host '    有紫星的物品無法加入販賣清單（點擊、全部出售都會被擋下）' -ForegroundColor Gray
Write-Host '  ・右側販賣清單按 F ＝ 標上紫星並立刻退回背包' -ForegroundColor Gray
Write-Host '  ・一般背包畫面的 F 維持遊戲原本的收藏功能，不受影響' -ForegroundColor Gray
Write-Host ''
Write-Host '設定檔（第一次啟動遊戲後自動生成）：' -ForegroundColor Cyan
Write-Host ('  ' + (Join-Path $game 'BepInEx\config\local.spiritvale.sellfavorite.cfg')) -ForegroundColor Gray
Write-Host ''
Write-Host '若要移除：雙擊「一鍵移除.bat」。' -ForegroundColor Gray
Write-Host ''
Read-Host '按 Enter 關閉'
