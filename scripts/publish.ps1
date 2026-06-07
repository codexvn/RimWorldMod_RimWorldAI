# RimWorldAI Steam Workshop 一键推送
# 用法:
#   .\scripts\publish.ps1 -Prepare       重新编译 + 生成 changelog
#   .\scripts\publish.ps1 -Push [-Mcp] [-Agent] [-Ui] [-All] [-Force]
#     -Force: 强制推送（跳过内容校验 + 跳过确认）

param([switch]$Prepare, [switch]$Push, [switch]$Mcp, [switch]$Agent, [switch]$Ui, [switch]$All, [switch]$Force)

if (-not $Prepare -and -not $Push) {
    Write-Host "Usage: .\scripts\publish.ps1 -Prepare   (重新编译+生成changelog)"
    Write-Host "       .\scripts\publish.ps1 -Push [-Mcp] [-Agent] [-Ui] [-All] (推送)"
    exit 1
}

if ($Push -and -not $Mcp -and -not $Agent -and -not $Ui -and -not $All) { $Agent = $true }

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$stateFile = "$PSScriptRoot\publish_state.json"
$changelogFile = "$PSScriptRoot\CHANGELOG.md"

# ===== 工具函数 =====

function Get-PublishHash($dir) {
    if (-not (Test-Path $dir)) { return "" }
    $files = Get-ChildItem $dir -Recurse -File | Sort-Object FullName
    $hash = [string]::Join("", ($files | ForEach-Object { "$($_.FullName.Replace($dir,''))|$($_.Length)" }))
    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($hash))).Replace("-","")
}

function Get-Changelog {
    if (-not (Test-Path $changelogFile)) { return $null }
    $text = Get-Content $changelogFile -Raw
    $m = [regex]::Match($text, '##\s*\[([\d-]+)\]\s*-\s*(.+?)(?=\n##\s*\[|\z)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) {
        $body = $m.Groups[2].Value -replace '\n\s*\n', '\n'  # 保留换行
        $body = $body.Trim()
        return @{ Date = $m.Groups[1].Value; Text = $body }
    }
    return $null
}

$mods = @(
    @{ Name = "RimWorldMCP";    Key = "rimworld_mcp";    Vdf = "workshop_mcp.vdf";    Dir = "$root\publish\RimWorldMCP" }
    @{ Name = "RimWorldAgent";  Key = "rimworld_agent";  Vdf = "workshop_agent.vdf";  Dir = "$root\publish\RimWorldAgent" }
    @{ Name = "RimWorldAgentUI"; Key = "rimworld_agentui"; Vdf = "workshop_agentui.vdf"; Dir = "$root\publish\RimWorldAgentUI" }
)

$state = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { @{} }

# ===== Prepare 模式 =====
if ($Prepare) {
    Write-Host "===== 1. 清理旧输出 =====" -ForegroundColor Cyan
    dotnet clean "$root\RimWorldAI.sln" --configuration Release 2>$null
    Remove-Item "$root\publish\*" -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "===== 2. 重新编译 (Release) =====" -ForegroundColor Cyan
    dotnet build "$root\RimWorldAI.sln" --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }

    Write-Host "===== 3. 生成 Changelog =====" -ForegroundColor Cyan
    Push-Location $root
    $lastSha = $state.rimworld_agent.last_sha
    if (-not $lastSha) { $lastSha = (git rev-list --max-parents=0 HEAD) }
    $changes = git log "$lastSha..HEAD" --format="- %s" 2>$null
    if (-not $changes) { $changes = "- 日常更新" }
    $date = Get-Date -Format "yyyy-MM-dd"
    Pop-Location

    $newSection = @"
## [$date] -

$($changes -join "`n")

"@

    if (Test-Path $changelogFile) {
        $existing = Get-Content $changelogFile -Raw
        if ($existing -match "##\s*\[$date\]") {
            Write-Host "  今天已有 changelog 条目，跳过生成" -ForegroundColor Yellow
        } else {
            $existing = $newSection + $existing
            Set-Content $changelogFile $existing -Encoding UTF8
            Write-Host "  已生成 changelog 条目:" -ForegroundColor Green
            Write-Host $newSection
        }
    } else {
        Set-Content $changelogFile ($newSection) -Encoding UTF8
        Write-Host "  已创建 CHANGELOG.md:" -ForegroundColor Green
        Write-Host $newSection
    }

    Write-Host "===== Prepare 完成 =====" -ForegroundColor Green
    Write-Host "  1. 编辑 scripts\CHANGELOG.md，填写本次更新标题和条目"
    Write-Host "  2. 手动编辑后运行: .\scripts\publish.ps1 -Push -Agent (或其他 mod)"
    exit 0
}

# ===== Push 模式 =====
Write-Host "===== 1. 编译 (Release) =====" -ForegroundColor Cyan
dotnet build "$root\RimWorldAI.sln" --configuration Release
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$cl = Get-Changelog
if (-not $cl) {
    Write-Host "===== 错误: CHANGELOG.md 无有效条目 =====" -ForegroundColor Red
    Write-Host "  先运行: .\scripts\publish.ps1 -Prepare 生成 changelog" -ForegroundColor Red
    exit 1
}

Write-Host "===== 2. 待推送检查 =====" -ForegroundColor Cyan
$changelogText = $cl.Text

# 筛选本次推送的 mod
$targetMods = @()
foreach ($m in $mods) {
    if ($m.Name -eq "RimWorldMCP"    -and ($Mcp -or $All)) { $targetMods += $m }
    if ($m.Name -eq "RimWorldAgent"  -and ($Agent -or $All)) { $targetMods += $m }
    if ($m.Name -eq "RimWorldAgentUI" -and ($Ui -or $All)) { $targetMods += $m }
}

# 构建确认信息
$confirmLines = @()
$pendingCount = 0
$skipCount = 0

foreach ($m in $targetMods) {
    $hash = Get-PublishHash $m.Dir
    $lastHash = $state.($m.Key).last_sha
    $ver = [int]$state.($m.Key).version
    if (-not $Force -and $lastHash -and $hash -eq $lastHash) {
        $confirmLines += "  [跳过] $($m.Name) v$ver — 内容未变化"
        $skipCount++
    } else {
        $confirmLines += "  [推送] $($m.Name) v$ver → v$($ver+1)"
        $pendingCount++
    }
}

Write-Host ""
Write-Host "========================================"
Write-Host "  Changelog: [$($cl.Date)]"
$clLines = $changelogText -split '[\r\n]+'
foreach ($line in $clLines) {
    Write-Host "    $line"
}
Write-Host "========================================"
Write-Host "  待推送: $($targetMods.Count) 个 mod (其中 $pendingCount 个有变更, $skipCount 个跳过)"
foreach ($line in $confirmLines) { Write-Host $line }
Write-Host "========================================"

if ($pendingCount -eq 0) {
    Write-Host "`n  没有需要推送的 mod" -ForegroundColor Yellow
    exit 0
}

# 用户确认
if (-not $Force) {
    $confirm = Read-Host "`n  输入 'yes' 确认推送"
    if ($confirm -ne "yes") {
        Write-Host "  已取消" -ForegroundColor Yellow
        exit 0
    }
}

# ===== 推送 =====
foreach ($m in $targetMods) {
    $hash = Get-PublishHash $m.Dir
    $lastHash = $state.($m.Key).last_sha
    if (-not $Force -and $lastHash -and $hash -eq $lastHash) {
        Write-Host "===== 跳过 $($m.Name) =====" -ForegroundColor Yellow
        continue
    }

    Write-Host "===== 推送 $($m.Name) =====" -ForegroundColor Cyan
    $vdf = Get-Content "$PSScriptRoot\$($m.Vdf)" -Raw
    $clSafe = $changelogText -replace '"', '`"'
    $vdf = $vdf -replace '"visibility" "0"', ('"visibility" "0"`n    "changenote" "' + $clSafe + '"')
    $tmpVdf = "$env:TEMP\workshop_$($m.Key).vdf"
    Set-Content $tmpVdf $vdf

    steamcmd +login anonymous +workshop_build_item $tmpVdf +quit
    if ($LASTEXITCODE -eq 0) {
        if (-not $state.($m.Key)) { $state | Add-Member -NotePropertyName $($m.Key) -NotePropertyValue (@{}) -Force }
        $state.($m.Key).last_sha = $hash
        $state.($m.Key).version = ([int]$state.($m.Key).version) + 1
        $state | ConvertTo-Json | Set-Content $stateFile
        Write-Host "  已推送 v$($state.($m.Key).version)" -ForegroundColor Green
    } else {
        Write-Host "  推送失败! 重试或检查 SteamCMD" -ForegroundColor Red
    }
}

Write-Host "===== 完成 =====" -ForegroundColor Green
