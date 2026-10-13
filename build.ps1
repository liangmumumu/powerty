param([switch]$SelfContained, [switch]$Portable, [string]$LexiconSource, [string]$EcdictSource)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = "dotnet"
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
}
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$publishPath = Join-Path $root "artifacts\publish"
$installerPath = Join-Path $root "artifacts\installer"
$dictsPath = Join-Path $root "artifacts\dicts"
$testProject = Join-Path $root "tests\Qwaacty.Core.Tests\Qwaacty.Core.Tests.csproj"
$lexiconProject = Join-Path $root "tools\Qwaacty.LexiconBuilder\Qwaacty.LexiconBuilder.csproj"
$lexiconDataDir = Join-Path $root "tools\Qwaacty.LexiconBuilder\data"

if (-not $LexiconSource) {
    $defaultLexiconSource = Join-Path (Split-Path -Parent $root) "qwerty_typing_translating\qwerty-learner-master"
    if (Test-Path -LiteralPath $defaultLexiconSource) {
        $LexiconSource = $defaultLexiconSource
    }
}

if (-not $EcdictSource) {
    $defaultEcdictSource = Join-Path (Split-Path -Parent $root) "ecdict\ecdict.csv"
    if (Test-Path -LiteralPath $defaultEcdictSource) {
        $EcdictSource = $defaultEcdictSource
    }
}

$curatedPath = Join-Path $root "tools\Qwaacty.LexiconBuilder\curated.tsv"

& $dotnet run --project $testProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Core checks failed."
}

# The publish directory is rebuilt from scratch every run: `dotnet publish` never
# deletes stale files, so leftovers (old dlls, an old lexicon) would otherwise
# silently end up inside the installer and the green build.
if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishPath | Out-Null

if ($SelfContained) {
    & $dotnet publish (Join-Path $root "src\Qwaacty.App\Qwaacty.App.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained publish failed. It needs the win-x64 runtime packs, which require a one-time download approval."
    }
}
else {
    & $dotnet publish (Join-Path $root "src\Qwaacty.App\Qwaacty.App.csproj") `
        --configuration Release `
        --output $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed."
    }
}

if ($LexiconSource) {
    if (Test-Path -LiteralPath $LexiconSource) {
        New-Item -ItemType Directory -Force -Path $dictsPath | Out-Null
        # Dictionaries land in artifacts\dicts (uploaded to the "dicts-v1" release);
        # they are deliberately NOT bundled into the installer or green build any more.
        $lexiconOutput = Join-Path $dictsPath "lexicon.ttlx"
        $lexiconArguments = @("--source", $LexiconSource, "--output", $lexiconOutput)
        if ($EcdictSource -and (Test-Path -LiteralPath $EcdictSource)) {
            $lexiconArguments += @("--ecdict", $EcdictSource, "--ecdict-phrase-words", "6")
            Write-Host "Merging ECDICT entries from $EcdictSource"
        }
        elseif ($PSBoundParameters.ContainsKey('EcdictSource')) {
            throw "ECDICT source not found: $EcdictSource"
        }

        if (Test-Path -LiteralPath $curatedPath) {
            $lexiconArguments += @("--curated", $curatedPath)
        }

        & $dotnet run --project $lexiconProject --configuration Release -- @lexiconArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Lexicon build failed."
        }

        & $dotnet run --project $testProject --configuration Release -- --lexicon $lexiconOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Real lexicon verification failed."
        }

        # Professional (electronics) dictionary: course wordlist + curated terms,
        # explanations from the local ECDICT csv.
        $eedictOutput = Join-Path $dictsPath "eedict.json"
        $eedictArguments = @("--eedict-output", $eedictOutput,
            "--eedict-vocab", (Join-Path $lexiconDataDir "ee-course-vocab.tsv"),
            "--eedict-vocab", (Join-Path $lexiconDataDir "ee-extra-vocab.tsv"))
        if ($EcdictSource -and (Test-Path -LiteralPath $EcdictSource)) {
            $eedictArguments += @("--eedict-ecdict", $EcdictSource)
        }

        & $dotnet run --project $lexiconProject --configuration Release -- @eedictArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Professional dictionary build failed."
        }
    }
    else {
        if ($PSBoundParameters.ContainsKey('LexiconSource')) {
            throw "Lexicon source not found: $LexiconSource"
        }

        Write-Host "Lexicon source not found at $LexiconSource; the app will use built-in demo entries. Pass -LexiconSource to override."
    }
}

if ($Portable) {
    $portablePath = Join-Path $root "artifacts\portable"
    if (Test-Path -LiteralPath $portablePath) {
        Remove-Item -LiteralPath $portablePath -Recurse -Force
    }

    # Compression is deliberately OFF: EnableCompressionInSingleFile shrinks the file
    # on disk (~74 MB vs ~179 MB) but makes the runtime decompress every managed
    # assembly into private memory, adding roughly 150 MB of resident memory to an
    # app that already keeps a large lexicon in RAM. Resident memory wins.
    & $dotnet publish (Join-Path $root "src\Qwaacty.App\Qwaacty.App.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $portablePath
    if ($LASTEXITCODE -ne 0) {
        throw "Portable self-contained publish failed. It needs the win-x64 runtime packs (one-time download from the official NuGet feed)."
    }

    Get-ChildItem -Path $portablePath -Filter *.pdb | Remove-Item -Force
    New-Item -ItemType File -Force -Path (Join-Path $portablePath "portable.txt") | Out-Null

    # Dictionaries are NOT bundled any more: the app ships a 词库 page whose download
    # buttons pull lexicon.ttlx / eedict.json from the "dicts-v1" GitHub release.
    Write-Host "Dictionaries are not bundled; users download them from the in-app 词库 page."

    $usageText = @'
Qwaacty — 绿色版（免安装）
==================================

启动方式：双击 Qwaacty.App.exe

特点：
1. 自包含版本：无需安装 .NET 运行时或任何依赖，整个文件夹可拷贝到任意位置（含 U 盘）使用。
2. 便携设置：快捷键、在线补查、开机启动、浮窗外观等设置保存在本文件夹的 settings.json，不写入系统其他位置。

使用方法：
- 键入实时翻译：勾选主窗口「键入实时翻译」或按 Ctrl+Alt+Shift+T 开关。打字时自动显示当前单词/中文片段（支持变形词：running→run、studied→study 等自动还原）。离线词库（约 43 万词条）与专业词库（电子信息：中英术语+解释）请在主窗口「词库」页点击下载（约 34MB / 200KB，下载后立即生效）。
- 专业词库：勾选主页「专业词库（电子信息）」或按 Ctrl+Alt+Shift+P 开关；开启后划词/截图命中专业词条时，将以中英对照+术语解释展示，顶部可按需点击「AI 增强翻译」，并附百科链接。
- 划词翻译：勾选「划词翻译」或按 Ctrl+Alt+Shift+S 开关；选中文本后按 Ctrl+Alt+Shift+D 翻译（按下后立即显示"正在读取"反馈）。
- AI 翻译（可选）：在「AI 翻译」区域选择服务商（推荐 DeepSeek）→ 到服务商官网注册并创建 API Key → 将 Key 粘贴到"API Key"框 → 点「测试连接」确认成功。启用后，本地词库未收录的单词、短语与划词句子会优先由 AI 翻译（流式逐字显示，结果自动缓存）。
- 弹窗自动关闭规则：自动查询的弹窗在你点击任意处（取消划词）时自动关闭；快捷键弹窗可选"松开按键后自动关闭"（至少显示 1.5 秒便于阅读）或"保持显示、手动关闭"，在设置中自由切换。
- 快捷键默认使用三修饰键偏门组合，避免与其他软件冲突；三个键均可在主窗口修改。
- 关闭主窗口后程序驻留系统托盘（快捷键继续有效）；双击托盘图标重新打开，右键托盘菜单可开关模块或退出。
- 兼容模式（使用微信/QQ 打字查词必须开启）：这些应用无法被直接读取，需改用临时键入缓冲（仅内存，切换焦点/回车/点击即清空）。
- 浮窗交互：仅显示词性、音标与释义；多条结果时第一条完整显示，其余以横向词条折叠，点击即可展开；可按住顶部拖动位置、拖右下角调节大小、鼠标滚轮滚动；字体、字号与不透明度在主窗口「浮窗外观」调整（含实时预览框）。
- 诊断日志（默认开启，仅记录事件元数据：应用名/成功失败/错误，不含任何文本内容）：出现问题时可在「%LOCALAPPDATA%\Qwaacty\diagnostics.log」中查看。
- 程序只允许运行一个实例；重复启动会提示到系统托盘查找图标。
- 主界面参考 VS Code 深色风格。

注意事项：
- 首次运行若出现「Windows 已保护你的电脑」（未签名），点击「更多信息」→「仍要运行」。
- 密码输入框等无法安全读取的区域会自动跳过；程序不保存键入内容。
- 「登录 Windows 时自动启动」会写入当前用户注册表（仅在你勾选时）；若移动了本文件夹，请在设置中重新勾选以修复路径。
- 兼容模式在中文输入法组合状态下无法工作（拼音字母会被缓冲）；在微信中查中文词建议使用划词查询。
- 若快捷键被其他软件占用，启动时会弹托盘通知提示，换一个组合即可。

卸载：直接删除本文件夹即可（若勾选过开机启动，请先在设置中取消勾选）。
'@
    Set-Content -LiteralPath (Join-Path $portablePath "使用说明.txt") -Value $usageText -Encoding UTF8

    $zipPath = Join-Path $root "artifacts\Qwaacty-Green-x64.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $portablePath,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Host "Portable folder: $portablePath"
    Write-Host "Portable zip:    $zipPath"
}

$compiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
$compilerPath = $null
if ($null -ne $compiler) {
    $compilerPath = $compiler.Source
}
else {
    $localIscc = Join-Path $root "tools\InnoSetup6\ISCC.exe"
    if (Test-Path -LiteralPath $localIscc) {
        $compilerPath = $localIscc
    }
}

if ($null -eq $compilerPath) {
    Write-Host "Published app to $publishPath. Install Inno Setup 6 and run installer\Qwaacty.iss to create the installer."
    exit 0
}

New-Item -ItemType Directory -Force -Path $installerPath | Out-Null
& $compilerPath "/O$installerPath" (Join-Path $root "installer\Qwaacty.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed."
}
