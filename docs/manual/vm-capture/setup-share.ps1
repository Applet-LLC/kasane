#requires -RunAsAdministrator
<#
.SYNOPSIS
  VM から撮影するための、撮影専用の SMB 共有（kasanevm）と、専用ユーザーを、ホストに作る。
.DESCRIPTION
  ホストを変更するので、管理者が自分で実行する（昇格した PowerShell）。何を作るかは、次のとおり。
    - フォルダ <Root>（既定 C:\kasanevm）の下に、out\{lib,jobs,jobs2,img,keys,results}、dist
    - 専用のローカルユーザー（既定 kasanevm。パスワードは乱数。最後に 1 回だけ表示する。どこにも保存しない）
    - 共有 kasanevm（<Root>）。アクセスできるのは、専用ユーザーと Administrators だけ。ドライブ全体は公開しない。
    - out\lib に vmlib.ps1・UiaLib.ps1・Capture-Manual.ps1、out に agent.ps1・agent2.ps1、out\jobs-1.02 に撮影ジョブを置く
      （ジョブと agent の中の、ホストのアドレスは、-HostAddress に置き換えて写す。リポジトリのファイルは変えない）。
  撮影が終わったら、teardown-share.ps1 で消す。
.PARAMETER HostAddress  VM から見た、ホストのアドレス。省略すると、100.64.* のアドレスを探す（無ければ、一覧を出して止まる）。
.PARAMETER Root         共有するフォルダ。既定は C:\kasanevm。
.PARAMETER UserName     専用ユーザーの名前。既定は kasanevm。
.PARAMETER DistFolder   1.02 の配布物（Install-kasane.exe と KasaneSetup-*.msi）のフォルダ。指定すると、<Root>\dist に写す。
.EXAMPLE
  .\setup-share.ps1 -DistFolder C:\Users\applet\Documents\GitHub\kasane\setup\dist\kasane-subscription
#>
param(
    [string]$HostAddress,
    [string]$Root = 'C:\kasanevm',
    [string]$UserName = 'kasanevm',
    [string]$DistFolder
)
$ErrorActionPreference = 'Stop'
$shareName = 'kasanevm'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path

# --- ホストのアドレス ---
if (-not $HostAddress) {
    $cand = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '100.64.*' })
    if ($cand.Count -eq 1) { $HostAddress = $cand[0].IPAddress }
    else {
        Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' } |
            Select-Object InterfaceAlias, IPAddress | Format-Table -AutoSize | Out-String | Write-Host
        throw '-HostAddress に、VM から見たホストのアドレスを指定してください（上の一覧から選ぶ）。'
    }
}
Write-Host "ホストのアドレス: $HostAddress"

# --- すでにあるものは、上書きしない ---
if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { throw "共有 $shareName がすでにあります。先に teardown-share.ps1 で消してください。" }
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { throw "ユーザー $UserName がすでにあります。先に teardown-share.ps1 で消してください。" }

# --- フォルダ ---
foreach ($d in 'out\lib', 'out\jobs', 'out\jobs2', 'out\img', 'out\keys', 'out\results', 'dist') { New-Item -ItemType Directory -Force (Join-Path $Root $d) | Out-Null }

# --- 専用ユーザー（パスワードは乱数。保存しない） ---
$bytes = New-Object byte[] 18; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$plain = ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x') + 'aA1!'
$secure = ConvertTo-SecureString $plain -AsPlainText -Force
New-LocalUser -Name $UserName -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -Description 'kasane manual capture (temporary)' | Out-Null

# --- フォルダの権限: 専用ユーザーに、変更を許す（ほかのアカウントには、広げない） ---
$acl = Get-Acl $Root
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($UserName, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule); Set-Acl $Root $acl

# --- 共有: 専用ユーザーと Administrators だけ。一覧にも、見えないようにする ---
New-SmbShare -Name $shareName -Path $Root -FullAccess $UserName, 'BUILTIN\Administrators' -FolderEnumerationMode AccessBased -CachingMode None `
    -Description 'kasane manual capture (temporary)' | Out-Null

# --- ファイルを写す（ホストのアドレスは、置き換える） ---
function Copy-WithAddress([string]$src, [string]$dst) {
    $text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8) -replace '100\.64\.1\.22', $HostAddress
    [System.IO.File]::WriteAllText($dst, $text, (New-Object System.Text.UTF8Encoding($true)))   # BOM 付き（VM の Windows PowerShell 5.1 が、日本語を読むため）
}
foreach ($f in 'agent.ps1', 'agent2.ps1') { Copy-WithAddress (Join-Path $here $f) (Join-Path $Root "out\$f") }
Copy-WithAddress (Join-Path $here 'vmlib.ps1') (Join-Path $Root 'out\lib\vmlib.ps1')
foreach ($f in 'UiaLib.ps1', 'Capture-Manual.ps1') { Copy-Item (Join-Path $repo "kasane.Tests\UiAutomation\$f") (Join-Path $Root "out\lib\$f") -Force }
New-Item -ItemType Directory -Force (Join-Path $Root 'out\jobs-1.02') | Out-Null
Get-ChildItem (Join-Path $here 'jobs-1.02') -Filter *.ps1 | ForEach-Object { Copy-WithAddress $_.FullName (Join-Path $Root "out\jobs-1.02\$($_.Name)") }
if ($DistFolder) { robocopy $DistFolder (Join-Path $Root 'dist') /E /NFL /NDL /NJH /NP | Select-Object -Last 3 }
Set-Content (Join-Path $Root 'out\lang.txt') 'ja' -Encoding ASCII

# --- 結果 ---
Write-Host ''
Write-Host '共有を作りました。' -ForegroundColor Green
Write-Host "  フォルダ: $Root    共有: \\$HostAddress\$shareName    ユーザー: $env:COMPUTERNAME\$UserName"
Write-Host '  撮影ジョブは、out\jobs-1.02\ にあります。README の順に、out\jobs2\（昇格）と out\jobs\ へ 1 つずつ移して実行します。'
Write-Host '  撮影用のキーは、out\keys\ に invalid.txt、trial.txt、subscription.txt を置きます（1 行に、キーだけ）。'
Write-Host '  配布物は、dist\ に置きます（-DistFolder で写せます）。'
Write-Host ''
Write-Host 'VM のデスクトップで、次を実行します（パスワードは、この 1 回だけ表示します。どこにも保存していません）:' -ForegroundColor Yellow
Write-Host "  net use \\$HostAddress\$shareName /user:$env:COMPUTERNAME\$UserName $plain"
Write-Host "  powershell -ExecutionPolicy Bypass -File \\$HostAddress\$shareName\out\agent.ps1       # 昇格しないエージェント"
Write-Host "  （昇格版は、管理者として起動した PowerShell で agent2.ps1。net use は、昇格した側でも、もう一度必要です）"
