#requires -RunAsAdministrator
<#
.SYNOPSIS
  setup-share.ps1 で作った、撮影専用の共有（kasanevm）と、専用ユーザーを消す。
.DESCRIPTION
  既定では、共有とユーザーだけを消し、フォルダ（撮った画像が入っている）は残す。
  フォルダも消すときは、-RemoveFolder を付ける。撮った画像を、リポジトリへ写したことを確かめてから使う。
.PARAMETER Root          共有していたフォルダ。既定は C:\kasanevm。
.PARAMETER UserName      専用ユーザーの名前。既定は kasanevm。
.PARAMETER RemoveFolder  フォルダも消す。
#>
param(
    [string]$Root = 'C:\kasanevm',
    [string]$UserName = 'kasanevm',
    [switch]$RemoveFolder
)
$ErrorActionPreference = 'Stop'
$shareName = 'kasanevm'
if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $shareName -Force; "共有 $shareName を消しました" } else { "共有 $shareName はありません" }
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $UserName; "ユーザー $UserName を消しました" } else { "ユーザー $UserName はありません" }
if ($RemoveFolder) {
    if (Test-Path $Root) {
        $imgs = @(Get-ChildItem (Join-Path $Root 'out\img') -Recurse -File -ErrorAction SilentlyContinue).Count
        Write-Host "フォルダ $Root を消します（撮った画像: $imgs 枚）" -ForegroundColor Yellow
        Remove-Item $Root -Recurse -Force; "フォルダ $Root を消しました"
    } else { "フォルダ $Root はありません" }
} else { "フォルダ $Root は残しました（消すときは -RemoveFolder）" }
