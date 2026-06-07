# RimWorldAI Steam Workshop 一键推送
# 用法: .\scripts\publish.ps1 [-Mcp] [-Agent] [-Ui] [-All]
param([switch]$Mcp, [switch]$Agent, [switch]$Ui, [switch]$All)

if (-not $Mcp -and -not $Agent -and -not $Ui -and -not $All) {
    $Agent = $true   # 默认只推送 Agent
}

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "===== 1. 构建全部项目 =====" -ForegroundColor Cyan
dotnet build "$root\RimWorldAI.sln" --configuration Release
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

if ($Mcp -or $All) {
    Write-Host "===== 2a. 推送 RimWorldMCP =====" -ForegroundColor Cyan
    steamcmd +login anonymous +workshop_build_item "$root\scripts\workshop_mcp.vdf" +quit
}

if ($Agent -or $All) {
    Write-Host "===== 2b. 推送 RimWorldAgent =====" -ForegroundColor Cyan
    steamcmd +login anonymous +workshop_build_item "$root\scripts\workshop_agent.vdf" +quit
}

if ($Ui -or $All) {
    Write-Host "===== 2c. 推送 RimWorldAgentUI =====" -ForegroundColor Cyan
    steamcmd +login anonymous +workshop_build_item "$root\scripts\workshop_agentui.vdf" +quit
}

Write-Host "===== 完成 =====" -ForegroundColor Green
