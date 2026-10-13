# sync-external-vocab.ps1 —— 重新生成外部专业词库 TSV（供 LexiconBuilder --eedict-vocab 使用）
#
# 数据来源：
#   1. ADI（亚德诺半导体）官方术语表 Glossary of EE Terms 的社区抓取结果
#      （github.com/WangShuXian6/blog issue #164，数据版权归 Analog Devices 所有，
#        生成结果对应数据文件 ee-adi-vocab.tsv，并保留来源注释）；
#   2. github.com/Phillweston/EE_Vocabulary 《电子类专业英语词汇 A–Z》
#      （生成结果对应 ee-phillweston-vocab.tsv）。
#
# 原始数据缓存于 artifacts\dicts-src（不入库）；生成的 TSV 写入本目录（随仓库提交）。
# 用法：pwsh -File tools\Qwaacty.LexiconBuilder\data\sync-external-vocab.ps1 [-ForceDownload]
param(
    [string]$CacheDir,
    [string]$OutDir,
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not $CacheDir) { $CacheDir = Join-Path $repoRoot 'artifacts\dicts-src' }
if (-not $OutDir) { $OutDir = $PSScriptRoot }
New-Item -ItemType Directory -Force -Path $CacheDir, $OutDir | Out-Null
$utf8 = [Text.Encoding]::UTF8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Ensure-Download([string]$url, [string]$file) {
    if (-not $ForceDownload -and (Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).Length -gt 1000) { return }
    Write-Host "downloading $url"
    & curl.exe -sSL $url -o $file
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $file)) { throw "download failed: $url" }
}

# 乱码（UTF-8 字节被按 CP1252 解码）游程检测：连续 3+ 个 CP1252 字符且其中至少 2 个
# 属于 Latin-1 区间。正常的全角引号/破折号单独出现不会触发。
$mojiChar = '[\u00A0-\u00FF\u0152\u0153\u0160\u0161\u0178\u017D\u017E\u201A\u201E\u2020\u2021\u2022\u2026\u2030\u2039\u203A\u20AC\u2122\u02C6\u02DC\u2018\u2019\u201C\u201D\u2013\u2014]'
$mojiPattern = "$mojiChar{2,}"
$keywordClass = '[\u4e00-\u9fffA-Za-z0-9ΣΔΩ∆°µ±²\-]'

function Clean-Cell([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return '' }
    $t = $text
    $t = [regex]::Replace($t, 'NULL', '')
    $t = [regex]::Replace($t, '(?i)\(?\s*English\s*only\s*\)?', '')
    # 截断到首个乱码游程（游程后通常只剩关键词尾巴）；要求游程含至少一个 C0–FF 字符
    foreach ($m in [regex]::Matches($t, $mojiPattern)) {
        $strong = ([regex]::Matches($m.Value, '[\u00C0-\u00FF]')).Count
        if ($strong -ge 1) { $t = $t.Substring(0, $m.Index); break }
    }
    $t = [regex]::Replace($t, '\s+', ' ').Trim()
    $t = [regex]::Replace($t, '(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])', '')
    $t = [regex]::Replace($t, '[：:]\s*。$', '。')
    # 去掉句末的“另请参考…/摘自…”等悬垂碎片（链接已在抓取时丢失）
    $t2 = [regex]::Replace($t, '。?\s*(另请参考|请参考|详见|参见|更多信息|摘自|原文链接)[^。！？]{0,24}。$', '。')
    if ($t2.Length -ge 8 -and $t2 -ne '。') { $t = $t2 }
    # 去掉句末悬垂的纯代码/关键词列表/重复词/乱码尾巴
    $lastEnd = -1
    for ($i = $t.Length - 1; $i -ge 0; $i--) { if ('。！？!?'.IndexOf($t[$i]) -ge 0) { $lastEnd = $i; break } }
    if ($lastEnd -ge 0 -and $lastEnd -lt $t.Length - 1) {
        $tail = $t.Substring($lastEnd + 1).Trim()
        if ($tail.Length -le 60) {
            $head = $t.Substring(0, $lastEnd + 1)
            $hasMoji = $tail -match $mojiChar
            $isCodeTail = $tail -match "^[A-Za-z0-9\s\+\-/&%().,²°µΩ×·]{1,60}$"
            $isRefTail = $tail -match '参考|详见|参见'
            $isKeywordList = $tail -match "^($keywordClass{1,14}[,，、]){2,}$keywordClass{1,14}$"
            $isRepeat = ($tail.Length -le 16 -and $head.Contains($tail))
            if ($hasMoji -or $isCodeTail -or $isRefTail -or $isKeywordList -or $isRepeat) {
                $t = $head
            }
            else {
                # 尾巴里含“关键词,关键词”后缀时，从后缀处截断
                $km = [regex]::Match($tail, "($keywordClass{1,14}[,，、]){2,}$keywordClass{1,14}$")
                if ($km.Success -and $km.Index -gt 0) { $t = $head + $tail.Substring(0, $km.Index) }
            }
        }
    }
    $t = $t.TrimEnd([char[]]@('、', '，', ',', ';', '；', '：', ':', ' ', '·'))
    return $t.Trim()
}

function Split-MdRow([string]$line) {
    $inner = $line.Trim()
    if (-not $inner.StartsWith('|')) { return @() }
    $inner = $inner.Substring(1)
    if ($inner.EndsWith('|')) { $inner = $inner.Substring(0, $inner.Length - 1) }
    return @($inner -split '\|' | ForEach-Object { $_.Trim() })
}

# ---------- 1) ADI 术语表 ----------
Ensure-Download 'https://api.github.com/repos/WangShuXian6/blog/issues/164' (Join-Path $CacheDir 'adi-issue-164.json')
Ensure-Download 'https://api.github.com/repos/WangShuXian6/blog/issues/164/comments?per_page=100' (Join-Path $CacheDir 'adi-issue-164-comments.json')

$issue = [IO.File]::ReadAllText((Join-Path $CacheDir 'adi-issue-164.json'), $utf8) | ConvertFrom-Json
$comments = [IO.File]::ReadAllText((Join-Path $CacheDir 'adi-issue-164-comments.json'), $utf8) | ConvertFrom-Json
$chunks = @($issue.body) + @($comments | Where-Object { (([regex]::Matches($_.body, '(?m)^\|')).Count) -gt 50 } | ForEach-Object { $_.body })

$specialTerms = @{
    'a_d_converter'             = 'A/D Converter'
    'd_a_converter'             = 'D/A Converter'
    'nand-gate'                 = 'NAND Gate'
    'xor-gate'                  = 'XOR Gate'
    'low-pass-filter'           = 'Low-Pass Filter'
    'notch-filter'              = 'Notch Filter'
    'half-wave-rectifier'       = 'Half-Wave Rectifier'
    'full-bridge-rectifier'     = 'Full-Bridge Rectifier'
    'impedance-matching'        = 'Impedance Matching'
    'inverting-op-amp'          = 'Inverting Op-Amp'
    'non-inverting-op-amp'      = 'Non-Inverting Op-Amp'
    'difference-amplifier'      = 'Difference Amplifier'
    'voltage-divider'           = 'Voltage Divider'
    'd-flip-flop'               = 'D Flip-Flop'
    'thermal-resistance'        = 'Thermal Resistance'
    'time-of-flight'            = 'Time-of-Flight'
    'switching-controller'      = 'Switching Controller'
    'mixed-signal'              = 'Mixed-Signal'
    'light-sensor'              = 'Light Sensor'
    'circuit-simulator'         = 'Circuit Simulator'
    'clock-timing'              = 'Clock Timing'
    'sigma-delta'               = 'Sigma-Delta'
    'h-bridge'                  = 'H-Bridge'
    'soc-system-on-a-chip'      = 'System on a Chip (SoC)'
    '1_wire'                    = '1-Wire'
    '1-wire_master'             = '1-Wire Master'
    'i2c'                       = 'I²C'
    'i2s'                       = 'I²S'
    '10gbe'                     = '10GbE'
    '80211'                     = '802.11'
    '80211a'                    = '802.11a'
    '80211b'                    = '802.11b'
    '80211g'                    = '802.11g'
    'v_f'                       = 'V/F'
    'v_s'                       = 'V-s'
    't_h'                       = 'T/H'
    't_r'                       = 'T/R'
    'p_p'                       = 'P-P'
    'wr_rd'                     = 'WR-RD'
    's_s'                       = 'S/S'
    's_umts'                    = 'S-UMTS'
    'local_temperature'         = 'Local Temperature'
    'local_temperature_sensor'  = 'Local Temperature Sensor'
}

function Format-Slug([string]$slug) {
    if ($specialTerms.ContainsKey($slug)) { return $specialTerms[$slug] }
    $s = ($slug -replace '_', ' ' -replace '-', ' ').Trim()
    return (($s -split '\s+' | ForEach-Object {
                if ($_ -match '^[a-z]') { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) } else { $_ }
            }) -join ' ')
}

$stats = [ordered]@{ rows = 0; skippedNoContent = 0; skippedNoZh = 0; mojibakeCut = 0; junkCleared = 0; zhExtracted = 0; abbr = 0 }
$adiLines = New-Object System.Collections.Generic.List[string]
$skippedList = New-Object System.Collections.Generic.List[string]
$seenSlugs = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($chunk in $chunks) {
    foreach ($line in ($chunk -split "\r?\n")) {
        if (-not $line.StartsWith('|')) { continue }
        $cells = Split-MdRow $line
        if ($cells.Count -lt 2) { continue }
        if ($cells[0] -match '^-+$' -or $cells[0] -match '英文术语|English') { continue }
        if ($cells[1] -match '^-+$') { continue }
        $slug = $cells[0]
        $zhRaw = $cells[1]
        $exRaw = ''
        if ($cells.Count -ge 3) { $exRaw = $cells[2] }
        if (-not $slug -or -not $seenSlugs.Add($slug)) { continue }
        $stats.rows++

        if ($exRaw -match '[åæçèéïð]') { $stats.mojibakeCut++ }
        $zh = Clean-Cell $zhRaw
        $ex = Clean-Cell $exRaw

        # 纯字母、无空格的“解释”多为爬取残缺（如 AnalogtoDigital），弃用
        if ($ex -match '^[A-Za-z]{4,60}$') { $ex = ''; $stats.junkCleared++ }

        # 内容为空的“概念页”行（无解释且中文名为空/英文粘连）交给手工词表 ee-logic-vocab.tsv
        if (-not $ex -and $zhRaw -notmatch '[\u4e00-\u9fff]') { $stats.skippedNoContent++; $skippedList.Add($slug); continue }

        # 中文名为空或非中文时，尝试从解释开头提取“术语：解释”的术语部分
        if ($zh -notmatch '[\u4e00-\u9fff]') {
            $mm = [regex]::Match($ex, '^([\u4e00-\u9fff]{2,16})[：:]')
            if (-not $mm.Success) { $mm = [regex]::Match($ex, '^([0-9]*[\u4e00-\u9fff][\u4e00-\u9fff0-9]{1,14})[。，；,;：:!！?？]') }
            if ($mm.Success) { $zh = $mm.Groups[1].Value.Trim(); $stats.zhExtracted++ }
        }
        if (-not $zh) { $stats.skippedNoZh++; continue }

        # 英文显示名：优先采用第二列的英文/缩写形式，否则由 slug 生成
        $display = ''
        if ($zhRaw -match '^[A-Za-z0-9][A-Za-z0-9/².\-+ ]{0,48}$') { $display = $zhRaw } else { $display = Format-Slug $slug }
        $isAbbr = ($display -cmatch '^[A-Z0-9²][A-Z0-9²/\-+.]{1,11}$')
        if ($isAbbr) { $stats.abbr++ }
        $type = 'word'
        if ($isAbbr) { $type = 'abbr' }

        $adiLines.Add((@($display, $zh, $type, '', $ex) -join "`t"))
    }
}

$adiPath = Join-Path $OutDir 'ee-adi-vocab.tsv'
[IO.File]::WriteAllLines($adiPath, $adiLines, $utf8NoBom)
Write-Host ("ADI rows={0} kept={1} skipped-no-content={2} skipped-no-zh={3} mojibake-cut={4} junk-cleared={5} zh-extracted={6} abbr={7}" -f $stats.rows, $adiLines.Count, $stats.skippedNoContent, $stats.skippedNoZh, $stats.mojibakeCut, $stats.junkCleared, $stats.zhExtracted, $stats.abbr)
Write-Host ('skipped slugs: ' + ($skippedList -join ' '))
Write-Host "wrote $adiPath"

# ---------- 2) Phillweston/EE_Vocabulary ----------
Ensure-Download 'https://raw.githubusercontent.com/Phillweston/EE_Vocabulary/master/EE_Vocabulary.md' (Join-Path $CacheDir 'phillweston-ee-vocab.md')

$philLines = New-Object System.Collections.Generic.List[string]
$philSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$philRows = 0
foreach ($line in [IO.File]::ReadAllLines((Join-Path $CacheDir 'phillweston-ee-vocab.md'), $utf8)) {
    if (-not $line.TrimStart().StartsWith('|')) { continue }
    $cells = Split-MdRow $line
    if ($cells.Count -lt 2) { continue }
    $en = Clean-Cell $cells[0]
    $zh = Clean-Cell $cells[1]
    if ($en -notmatch '[A-Za-z]' -or -not $zh) { continue }
    if ($en -match '^-+$' -or $en -match '^(English|英文)$') { continue }
    $philRows++
    if (-not $philSeen.Add($en)) { continue }
    $philLines.Add((@($en, $zh, 'word') -join "`t"))
}

$philPath = Join-Path $OutDir 'ee-phillweston-vocab.tsv'
[IO.File]::WriteAllLines($philPath, $philLines, $utf8NoBom)
Write-Host ("Phillweston rows={0} kept={1}" -f $philRows, $philLines.Count)
Write-Host "wrote $philPath"
