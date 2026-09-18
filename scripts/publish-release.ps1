# 发布 GitHub Release（并可选上传附件）。
#
# 认证方式：复用 git 已经配好的凭据（Git Credential Manager）。
#   —— 不要求你把 token 贴进聊天，脚本也不会把它打印出来。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 `
#       -Tag preview-v0.1b -Name "preview v0.1b" -NotesFile documents\发布说明_preview-v0.1b.md `
#       -AssetPath "C:\Users\PC\Desktop\AI\_备份\WhiteBoard_xxx.zip"
#
# 退出码：0 = 成功

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$NotesFile = '',
    [string]$AssetPath = '',
    [string]$Repo = 'SeriosKore/WhiteBoard',
    [switch]$NotPrerelease
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "仓库：$Repo　标签：$Tag　标题：$Name"

# ── 取凭据（不打印） ──────────────────────────────────────────────────────
$credLines = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
$token = ($credLines | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
$user = ($credLines | Where-Object { $_ -like 'username=*' }) -replace '^username=', ''

if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Error '没能从 git 凭据管理器取到 GitHub 凭据（请先执行一次 git push 完成登录）'
    exit 2
}
Write-Host "已取得凭据（用户：$user；token 长度 $($token.Length)，不回显）"

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'WhiteBoard-Release-Script'
}
$api = "https://api.github.com/repos/$Repo"

# ── 准备发布说明 ──────────────────────────────────────────────────────────
$body = ''
if ($NotesFile -ne '' -and (Test-Path $NotesFile)) {
    $body = [System.IO.File]::ReadAllText((Resolve-Path $NotesFile).Path, [System.Text.UTF8Encoding]::new($true))
    Write-Host "发布说明：$NotesFile（$($body.Length) 字符）"
} else {
    Write-Host '未提供发布说明文件，将使用空说明'
}

# ── 已存在就复用，不重复创建 ──────────────────────────────────────────────
$release = $null
try {
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $headers -Method Get
    Write-Host "该标签已存在 Release（id=$($release.id)），将复用它"
} catch {
    Write-Host '尚不存在 Release，开始创建…'
}

if ($null -eq $release) {
    $payload = @{
        tag_name   = $Tag
        name       = $Name
        body       = $body
        draft      = $false
        prerelease = (-not $NotPrerelease)
    } | ConvertTo-Json -Depth 4

    $release = Invoke-RestMethod -Uri "$api/releases" -Headers $headers -Method Post `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8'

    Write-Host "已创建 Release：$($release.html_url)"
} else {
    Write-Host "Release 地址：$($release.html_url)"
}

# ── 上传附件（同名已存在则跳过） ──────────────────────────────────────────
if ($AssetPath -ne '') {
    if (-not (Test-Path $AssetPath)) {
        Write-Warning "附件不存在，跳过：$AssetPath"
    } else {
        $asset = Get-Item $AssetPath
        $existing = @($release.assets | Where-Object { $_.name -eq $asset.Name })
        if ($existing.Count -gt 0) {
            Write-Host "附件已存在，跳过上传：$($asset.Name)"
        } else {
            $sizeMB = [math]::Round($asset.Length / 1MB, 1)
            Write-Host "正在上传附件：$($asset.Name)（$sizeMB MB）…"
            $uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($asset.Name))"
            $uploaded = Invoke-RestMethod -Uri $uploadUrl -Headers $headers -Method Post `
                -InFile $asset.FullName -ContentType 'application/zip'
            Write-Host "附件已上传：$($uploaded.browser_download_url)"
        }
    }
}

Write-Host ''
Write-Host '──────── 发布完成 ────────'
Write-Host "Release：$($release.html_url)"
$final = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $headers -Method Get
Write-Host "状态：$(if ($final.prerelease) { '预发布（prerelease）' } else { '正式发布' })　附件数：$(($final.assets | Measure-Object).Count)"
exit 0
