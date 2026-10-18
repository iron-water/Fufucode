<#
.SYNOPSIS
  AND-2 真机级端到端联调：本机 RemoteControlHost <- Android 模拟器 FuFucode Mobile。

.DESCRIPTION
  在**不作弊**的前提下验证手机远控的整条链路：
    1. 宿主机跑真实 `RemoteControlHost`（产品同一个类）绑定一个真实 `IChatSession`，
       监听固定端口，输出真实 4 位 PIN；
    2. `adb shell input` 是**输入合成器**（等价于用户敲键盘），不是自动化框架：
       它不认识控件、不能读取界面、不能断言状态。因此所有"是否配对成功 /
       是否收到消息 / 是否弹出审批卡"的判断都来自**宿主机侧的协议事实**与
       **屏幕像素**（截图 + 人工/模型目视核对），不是 UI 测试框架的断言。
    3. 手机不可桥接到某一台特定会话：验证的是「真实桌面端服务 <-> 真实 APK」的联通。

  为什么不用 uiautomator dump 找坐标：Compose 的 `BasicTextField` 不暴露文本节点，
  在 1080x2000/420dpi 上实测 dump 只给出 `ComposeView` 一个节点。脚本改为
  「截图 + 固定坐标」，坐标由 `-Calibrate` 生成并在本文件顶部记录。

.EXAMPLE
  pwsh -File scripts/mobile/verify-mobile-pairing-live.ps1
  pwsh -File scripts/mobile/verify-mobile-pairing-live.ps1 -SkipBuild   # 复用已构建产物
#>
[CmdletBinding()]
param(
    [string]$Serial = 'emulator-5554',
    [int]$Port = 54321,
    [string]$Message = 'Hello from WorkBuddy Android',
    [string]$EvidenceDir = 'docs/records/evidence/and2-live-pairing',
    [string]$WorkDir = "$env:TEMP\and2-live",
    [string]$AdbPath,
    [switch]$SkipBuild,
    [switch]$KeepHarness
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package = 'com.fufucode.mobile.debug'

# --- 坐标（420dpi / 1080x2000，逐次截图目视核对得出） -------------------------------
# 注意：配对页进入即自动弹起数字键盘（官方规范），键盘会盖住下半屏。
# 因此填地址前必须先按 ESC 收起键盘，顺序不能颠倒。
$COORD = @{
    # 表单已收缩到「键盘弹起也不遮挡」的高度，页面无需滚动即可完成全部输入，
    # 因此这几个坐标在「键盘收起 / 键盘弹起」两种状态下都成立（实拍核对过）。
    # 逐个坐标都在真机上点过并用输入法上报的 inputType 反证过：
    #   540,740 -> URI（地址框）；186,1230 -> Number（第 1 个配对码格）。
    addressField = @(540, 740)    # 「电脑地址」输入框
    pinField     = @(186, 1230)   # 4 位配对码方格（第 1 格）
    connectBtn   = @(540, 1345)   # 「连接」主按钮
    composer     = @(430, 1670)   # 会话页输入框
    sendBtn      = @(980, 1670)   # 会话页发送键
    approveBtn   = @(700, 1180)   # 审批卡「允许一次」（按需校准）
}

function Info($m) { Write-Host "[and2] $m" }
function Fail($m) { throw "[and2] $m" }

function Resolve-Adb {
    if ($AdbPath) { return $AdbPath }
    # 注意：Join-Path 不接受 $null 根路径，因此只在环境变量确实存在时才拼接。
    $candidates = [Collections.Generic.List[string]]::new()
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')) }
    if ($env:ANDROID_HOME) { $candidates.Add((Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe')) }
    if ($env:ANDROID_SDK_ROOT) { $candidates.Add((Join-Path $env:ANDROID_SDK_ROOT 'platform-tools\adb.exe')) }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    Fail '找不到 adb.exe；请通过 -AdbPath 指定。'
}

$Adb = Resolve-Adb

function ConvertTo-ArgList {
    param([object[]]$Arguments)
    $list = [Collections.Generic.List[string]]::new()
    foreach ($a in $Arguments) { foreach ($v in $a) { $list.Add([string]$v) } }
    return $list.ToArray()
}

function Adb {
    param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)
    $argv = ConvertTo-ArgList $Arguments
    $lines = [Collections.Generic.List[string]]::new()
    & $Adb -s $Serial @argv 2>&1 | ForEach-Object { $lines.Add([string]$_) }
    if ($LASTEXITCODE -ne 0) { throw "adb $($argv -join ' ') 失败：$($lines -join ' | ')" }
    return $lines.ToArray()
}

function AdbQuiet {
    param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)
    $argv = ConvertTo-ArgList $Arguments
    & $Adb -s $Serial @argv 2>&1 | Out-Null
}

function Tap([int[]]$Point) { AdbQuiet shell input tap $Point[0] $Point[1] }
function TypeText([string]$Text) {
    $escaped = $Text -replace '\\', '\\\\' -replace ' ', '%s'
    AdbQuiet shell input text $escaped
}
function Key([string]$Code) { AdbQuiet shell input keyevent $Code }

<# 清空当前聚焦输入框：连按足够次数的退格（Compose BasicTextField 无 clear API）。 #>
function ClearFocusedField([int]$Times = 40) {
    for ($i = 0; $i -lt $Times; $i++) { Key 67 } # KEYCODE_DEL
    Start-Sleep -Milliseconds 400
}

<#
  把配对页恢复到确定状态：无焦点 + 列表滚到顶部。
  配对页是可垂直滚动的 Compose 列，聚焦输入框时会自动滚动，
  因此固定坐标只在「无焦点 + 顶部」这一状态下成立。

  用 BACK(keyevent 4) 收键盘：本机实测 **ESC(keyevent 111) 不收起键盘**，
  反而会被当前输入框当成字符插入（观察到 "g ft g ft" 之类的垃圾进入地址框）。
  配对页没有别的返回层，键盘弹起时 BACK 只收键盘、不退出 Activity。
#>
function ResetPairScroll {
    # 只在键盘确实可见时才按 BACK：键盘已收起时 BACK 会拉下通知栏，
    # 后续点击就全落在系统 UI 上（实测踩到过，整页误判）。
    HideKeyboard
    # 点一下顶部空白（品牌区），确保没有任何输入框保持焦点：
    # 冷启动时地址框会被自动聚焦，此时按键与部分点击会落到键盘上。
    AdbQuiet shell input tap 540 200
    Start-Sleep -Milliseconds 400
    HideKeyboard
    for ($i = 0; $i -lt 4; $i++) {
        # 从页面中部往下拖：让可滚动内容回到顶部。
        AdbQuiet shell input swipe 540 900 540 1500 220
        Start-Sleep -Milliseconds 350
    }
    Start-Sleep -Milliseconds 500
}

<#
  取输入法对**当前编辑框**上报的类型。
  dumpsys input_method 里会同时打印 IME 自身信息与历史 editor 信息，
  不能拿整份 dump 做模糊匹配（实测过 `-match 'Number'` 命中无关文本）。
  唯一权威的是 `inputTypeString = X` 这一行（输入法按当前 editor 上报）。
#>
function Get-EditorInputType {
    $lines = & $Adb -s $Serial shell dumpsys input_method 2>&1 | ForEach-Object { [string]$_ }
    foreach ($line in $lines) {
        if ($line -match 'inputTypeString\s*=\s*(\S+)') { return $Matches[1] }
    }
    return 'unknown'
}

<# 软键盘当前是否可见：ResetPairScroll 只应在键盘可见时才按 BACK。 #>
function Test-KeyboardShown {
    $lines = & $Adb -s $Serial shell dumpsys input_method 2>&1 | ForEach-Object { [string]$_ }
    foreach ($line in $lines) {
        if ($line -match 'mInputShown\s*=\s*(\S+)') { return $Matches[1] -eq 'true' }
    }
    return $false
}

<#
  反复按 BACK 直到软键盘确实收起（或超时）。
  单次 BACK 后键盘有动画，`mInputShown` 可能还没翻成 false；
  只看一次会在键盘仍占屏时继续操作，导致点击落到键盘上（实测踩到过）。
#>
function HideKeyboard([int]$TimeoutSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (!(Test-KeyboardShown)) { Start-Sleep -Milliseconds 400; return }
        Key 4
        Start-Sleep -Milliseconds 800
    }
    Fail '软键盘无法收起：后续点击会落到键盘上。'
}

<#
  等待配对页真正画出来再操作。
  冷启动时应用会先显示 splash（截图里只有绿色图标），此时点击一律落空；
  本机模拟器实测冷启动到首帧可达 3-5 秒，固定 sleep 不可靠。
  判据：窗口已获得焦点，且 Compose 视图已建立（dumpsys 中能查到 AndroidComposeView）。
#>
function Wait-AppReady([int]$TimeoutSeconds = 40) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $probe = Join-Path $WorkDir 'ready.png'
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1200
        & $Adb -s $Serial shell "screencap -p /sdcard/and2-ready.png" 2>&1 | Out-Null
        & $Adb -s $Serial pull /sdcard/and2-ready.png $probe 2>&1 | Out-Null
        if (!(Test-Path $probe)) { continue }
        # 像素级判据：冷启动 splash 上没有任何品牌蓝，只有真正的界面才会出现
        # 品牌蓝主按钮。窗口"有焦点"早于内容就绪，不能用 dumpsys window 判。
        $count = Get-BrandBluePixelCount $probe
        if ($count -gt 200) {
            Remove-Item $probe -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 600
            return
        }
    }
    Fail "等待配对页渲染超时（${TimeoutSeconds}s）：未出现品牌蓝主按钮像素。"
}

<# 统计 PNG 中「品牌蓝」(#0052D9) 像素数，作为"界面已绘制"的像素级判据。 #>
function Get-BrandBluePixelCount([string]$Path) {
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    try { $bmp = [System.Drawing.Bitmap]::FromFile($Path) } catch { return 0 }
    try {
        $count = 0
        # 每 4 像素抽样一次即可，够判「有没有大面积品牌蓝」。
        for ($y = 0; $y -lt $bmp.Height; $y += 4) {
            for ($x = 0; $x -lt $bmp.Width; $x += 4) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.B -gt 170 -and ($c.B - [int]$c.R) -gt 90 -and ($c.B - [int]$c.G) -gt 55) { $count++ }
            }
        }
        return $count
    } finally { $bmp.Dispose() }
}

<#
  实测映射（本机 Android 16 + Gboard）：
    - 电脑地址框 KeyboardType.Uri            -> inputTypeString = URI
    - 配对码格   KeyboardType.NumberPassword -> inputTypeString = Number
  NumberPassword 只在真正做密码掩码时才上报 NumberPassword；Compose 的
  隐藏输入框不做掩码，因此上报 Number。断言按**实测值**写，不按设想值写。
#>
function Test-AddressFieldFocused { (Get-EditorInputType) -eq 'URI' }
function Test-PinFieldFocused { (Get-EditorInputType) -eq 'Number' }

<# 断言当前编辑框就是电脑地址框；否则报错并给出实际类型。 #>
function Assert-AddressFieldFocused([bool]$ExpectFocused = $true) {
    $actual = Get-EditorInputType
    if ($ExpectFocused -and $actual -ne 'URI') {
        Fail "焦点不在电脑地址框：输入法上报 inputTypeString=$actual（预期 URI）。"
    }
}

<#
  取当前界面的 uiautomator 层级。
  Compose 的可编辑框会暴露成 android.widget.EditText 并带准确 bounds，
  所以坐标不必猜——按节点真实位置点。
#>
function Get-UiHierarchy {
    & $Adb -s $Serial shell "uiautomator dump /sdcard/and2-ui.xml" 2>&1 | Out-Null
    $local = Join-Path $WorkDir 'ui.xml'
    & $Adb -s $Serial pull /sdcard/and2-ui.xml $local 2>&1 | Out-Null
    if (!(Test-Path $local)) { Fail 'uiautomator dump 未产出层级文件。' }
    return [xml](Get-Content $local -Raw)
}

<# 所有可编辑框（按文档顺序 = 视觉从上到下）。 #>
function Get-EditFields {
    # 页面切换/动画期间 dump 可能还没有目标输入框；重试几次再判失败，
    # 避免把「过渡帧」当成「页面没有输入框」。
    for ($i = 0; $i -lt 5; $i++) {
        $xml = Get-UiHierarchy
        $nodes = @($xml.SelectNodes('//node[contains(@class,"EditText")]'))
        if ($nodes.Count -gt 0) { return $nodes }
        Start-Sleep -Milliseconds 800
    }
    return @()
}

<# 解析 bounds="[l,t][r,b]" 的中心点。 #>
function Get-BoundsCenter([string]$Bounds) {
    if ($Bounds -notmatch '\[(\d+),(\d+)\]\[(\d+),(\d+)\]') { Fail "无法解析 bounds：$Bounds" }
    $l = [int]$Matches[1]; $t = [int]$Matches[2]; $r = [int]$Matches[3]; $b = [int]$Matches[4]
    return @([int](($l + $r) / 2), [int](($t + $b) / 2))
}

<#
  含指定文字或无障碍描述（content-desc）的最近可点击祖先。
  用于按钮定位，不靠固定坐标。发送图标按钮只有 content-desc，没有 text。
#>
function Find-ClickableByText([string]$Text) {
    $xml = Get-UiHierarchy
    foreach ($n in $xml.SelectNodes('//node')) {
        $hasText = $n.GetAttribute('text') -eq $Text
        $hasDesc = $n.GetAttribute('content-desc') -eq $Text
        if (!$hasText -and !$hasDesc) { continue }
        $current = $n
        while ($current -and $current.Name -eq 'node') {
            if ($current.GetAttribute('clickable') -eq 'true') {
                return Get-BoundsCenter $current.GetAttribute('bounds')
            }
            $current = $current.ParentNode
        }
    }
    Fail "未找到文字/描述为「$Text」的可点击按钮。"
}

<#
  点按某个输入区并等待焦点真的落上去。
  输入法类型切换不是瞬时的（实测点按后需要约 0.5-1.5s 才上报新类型），
  单次读取会误判为“没切过去”。这里轮询到预期类型或超时。
#>
function Tap-UntilFocus {
    param(
        [int[]]$Point,
        [string]$ExpectType,
        [int]$TimeoutSeconds = 8
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Tap $Point
        Start-Sleep -Milliseconds 700
        $actual = Get-EditorInputType
        if ($actual -eq $ExpectType) { return }
    }
    Fail "点按 ($($Point -join ',')) 后焦点未切到 $ExpectType：inputTypeString=$(Get-EditorInputType)。"
}

function Screenshot([string]$Name) {
    $remote = "/sdcard/and2-$Name.png"
    $local = Join-Path $EvidenceDir "$Name.png"
    AdbQuiet shell "rm -f $remote"
    AdbQuiet shell "screencap -p $remote"
    # exec-out 在 PowerShell 下会被字符串化，必须走「设备内落盘 + pull」。
    # 不直接看 $LASTEXITCODE：该值可能被前面的管道命令污染，以文件是否落盘为准。
    & $Adb -s $Serial pull $remote $local 2>&1 | Out-Null
    if (!(Test-Path $local) -or (Get-Item $local).Length -le 0) { Fail "截图下拉失败：$remote -> $local" }
    AdbQuiet shell "rm -f $remote" # 清理本任务设备端临时文件
    Info "证据截图 -> $Name.png"
}

function Start-Harness {
    Info "启动宿主真实 RemoteControlHost（端口 $Port）..."
    $exe = Join-Path $repo 'tests/Fufucode.Mobile.LanHarness/bin/Debug/net10.0/Fufucode.Mobile.LanHarness.exe'
    if (!(Test-Path $exe)) { Fail "宿主未构建：$exe" }
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.Arguments = "--port $Port"
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    [void]$proc.Start()
    return $proc
}

function HarnessSend($proc, [string]$Line) {
    $proc.StandardInput.WriteLine($Line)
    $proc.StandardInput.Flush()
}

function HarnessRead-Timeout($proc, [int]$Seconds, [string]$Type) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        Info "  host< $line"
        $obj = $null
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.type -eq $Type) { return $obj }
    }
    return $null
}

function HarnessRead-All($proc, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $events = @()
    while ((Get-Date) -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        try { $events += ($line | ConvertFrom-Json) } catch { }
    }
    return $events
}

# =====================================================================================
Info "===== AND-2 真机级端到端联调 ====="
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$results = [ordered]@{}
$harness = $null
$portOwner = $null

try {
    # --- 0. 前置：设备与构建 --------------------------------------------------------
    $devices = (& $Adb devices 2>&1 | ForEach-Object { [string]$_ }) -join "`n"
    if ($devices -notmatch [regex]::Escape($Serial)) { Fail "设备 $Serial 不在 adb devices 列表中。" }
    $boot = (Adb shell getprop sys.boot_completed) -join ''
    if ($boot.Trim() -ne '1') { Fail "设备 $Serial 尚未完成启动。" }
    $pkg = (Adb shell pm list packages) -join "`n"
    if ($pkg -notmatch [regex]::Escape($package)) { Fail "未安装 $package，请先安装 APK。" }

    # 占用检查：端口必须是空闲的，避免验证打到别人的进程上。
    $busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($busy) {
        $portOwner = (Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Fail "端口 $Port 已被占用（PID $($busy[0].OwningProcess) / $portOwner）：先停止占用者再跑，避免误判。"
    }

    if (!$SkipBuild) {
        Info '构建宿主联调程序...'
        Push-Location $repo
        try { & dotnet build tests/Fufucode.Mobile.LanHarness -c Debug | Out-Null } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { Fail '宿主构建失败。' }
    }
    $results.build = 'ok'

    # --- 1. 启动真实宿主 ------------------------------------------------------------
    $harness = Start-Harness
    $ready = HarnessRead-Timeout $harness 30 'ready'
    if (!$ready) { Fail '宿主未在 30s 内就绪。' }
    if ($ready.port -ne $Port) { Fail "宿主实际端口 $($ready.port) 与请求的 $Port 不一致。" }
    $pin = $ready.pin
    $results.host = [ordered]@{ address = $ready.address; port = $ready.port; pin = $pin; pairingUrl = $ready.pairingUrl }
    Info "宿主 PIN=$pin  地址=$($ready.address):$($ready.port)"
    if ($pin -notmatch '^\d{4}$') { Fail "PIN 不是 4 位数字：$pin" }

    # --- 2. 手机侧：冷启动 + 输入地址/配对码 ---------------------------------------
    AdbQuiet shell am force-stop $package
    Start-Sleep -Milliseconds 800
    AdbQuiet shell am start -n "$package/com.fufucode.mobile.MainActivity"
    Wait-AppReady
    Start-Sleep -Seconds 1
    Screenshot '01-pair-screen'

    $connectHost = "10.0.2.2:$Port"

    # 坐标不写死：配对码格与电脑地址框的像素位置会随键盘/滚动变化，
    # 固定坐标实测反复点错（把地址打进配对码、或点按钮变成给配对码打字）。
    # 改为每次从 uiautomator 层级里读两个 EditText 的真实 bounds 再点。
    # 文档顺序 = 视觉顺序：第 1 个是「电脑地址」，第 2 个是「配对码」。
    # 注意必须用 @() 包裹：PowerShell 会把空数组/单元素数组展开，
    # 不包裹时 $fields 可能是 $null，后续 .Count / [0] 直接报 Null 方法错误。
    $fields = @(Get-EditFields)
    if ($fields.Count -lt 2) { Fail "配对页只暴露了 $($fields.Count) 个输入框，预期 2 个（地址 + 配对码）。" }
    $addressPoint = Get-BoundsCenter $fields[0].GetAttribute('bounds')
    $pinPoint = Get-BoundsCenter $fields[1].GetAttribute('bounds')
    Info "地址框中心 $($addressPoint -join ',')；配对码格中心 $($pinPoint -join ',')"

    # 先配对码（该格为当前焦点，键盘自然弹起），再电脑地址。
    Tap-UntilFocus -Point $pinPoint -ExpectType 'Number'
    ClearFocusedField
    TypeText $pin
    Start-Sleep -Milliseconds 800
    Screenshot '02a-pin-entered'

    HideKeyboard
    Tap-UntilFocus -Point $addressPoint -ExpectType 'URI'
    ClearFocusedField
    TypeText $connectHost
    Start-Sleep -Milliseconds 800
    Screenshot '02-pair-filled'

    Info "点击「连接」 -> http://$connectHost"
    # 收键盘后再点「连接」：地址框聚焦会把地址键盘重新弹起，
    # 而键盘高度足够把「连接」按钮压到屏幕之外（实测按钮被挡）。
    # 收键盘动作本身不会再插入字符（BACK 而非 ESC，见 ResetPairScroll 注释）。
    HideKeyboard
    # 连接成功以**宿主侧连接计数 > 0** 为准；连不上就再收一次键盘重试，
    # 不靠"点过了"当成功（点击可能被动画中的键盘或滚动吞掉）。
    $connected = $false
    for ($attempt = 1; $attempt -le 3 -and !$connected; $attempt++) {
        # 必须先清焦点：配对码格保持焦点时按 BACK 只收键盘，
        # 下一次点击会先把焦点交回该格并重新弹起键盘（实测点按钮变成给配对码打字）。
        # 点品牌区空白处可稳妥清掉焦点。
        AdbQuiet shell input tap 540 200
        Start-Sleep -Milliseconds 500
        HideKeyboard
        # 按钮同样按层级里的真实 bounds 定位。
        $connectPoint = Find-ClickableByText '连接'
        Info "「连接」按钮中心 $($connectPoint -join ',')"
        Tap $connectPoint
        Start-Sleep -Milliseconds 1500
        HarnessSend $harness 'status'
        $probe = HarnessRead-Timeout $harness 5 'status'
        if ($probe -and $probe.connections -ge 1) {
            $connected = $true
            $results.connections = $probe.connections
            break
        }
        HideKeyboard
    }
    # 连接结果用**宿主侧连接计数**判定：只有真正的 WebSocket 客户端接入才会 >0。
    for ($i = 0; $i -lt 12 -and !$connected; $i++) {
        Start-Sleep -Milliseconds 700
        HarnessSend $harness 'status'
        $st = HarnessRead-Timeout $harness 5 'status'
        if ($st -and $st.connections -ge 1) { $connected = $true; $results.connections = $st.connections; break }
    }
    if (!$connected) { Fail '配对后宿主未观察到任何已认证连接：配对失败或手机未建立事件流。' }
    Start-Sleep -Seconds 2
    Screenshot '03-chat-entered'
    $results.pairing = 'ok'

    # --- 3. 消息收发 -----------------------------------------------------------------
    Info "在手机上输入并发送：$Message"
    # 会话页同样按层级定位：输入框是唯一 EditText，发送键靠 content-desc「发送」找。
    $chatFields = @(Get-EditFields)
    if ($chatFields.Count -lt 1) { Fail '会话页未找到输入框（等待后仍无 EditText 节点）。' }
    $composerPoint = Get-BoundsCenter $chatFields[0].GetAttribute('bounds')
    Info "输入框中心 $($composerPoint -join ',')"
    Tap $composerPoint
    Start-Sleep -Milliseconds 700
    TypeText $Message
    Start-Sleep -Milliseconds 700
    Screenshot '04-message-typed'

    $sendPoint = Find-ClickableByText '发送'
    Info "发送键中心 $($sendPoint -join ',')"
    Tap $sendPoint

    $sent = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Milliseconds 700
        HarnessSend $harness 'status'
        $st = HarnessRead-Timeout $harness 5 'status'
        if ($st -and ($st.activityLog -contains "send:$Message")) { $sent = $true; break }
    }
    if (!$sent) { Fail "宿主未收到消息：$Message" }
    $results.messageReceived = $Message
    Info '宿主已收到并注入会话：send:' + $Message

    # 宿主投递一轮脚本化助手回复（思考 + 流式文本 + 待审批工具），手机应实时渲染。
    HarnessSend $harness 'script'
    [void](HarnessRead-Timeout $harness 10 'scripted')
    Start-Sleep -Seconds 3
    Screenshot '05-timeline-streaming'

    # --- 4. 真实审批闸门 -------------------------------------------------------------
    Info '宿主登记一条测试审批 RequestApproval("tool_1","read_file","{}")...'
    HarnessSend $harness 'approval'
    $req = HarnessRead-Timeout $harness 10 'approvalRequested'
    if (!$req -or !$req.approvalId) { Fail '宿主未能登记审批请求。' }
    $results.approval = [ordered]@{ approvalId = $req.approvalId; toolName = $req.toolName }
    Start-Sleep -Seconds 2
    Screenshot '06-approval-card'

    # 手机上点「批准」，宿主侧 WaitForApprovalAsync 必须真的收到决策。
    HarnessSend $harness 'await-approval'
    Start-Sleep -Milliseconds 500
    Info '点击手机审批卡「允许一次」...'
    # 审批卡在时间线**顶部**，而时间线会自动跟到最新消息（底部），
    # 因此卡片通常在可视区之上；先把时间线滑回顶部再找按钮。
    # 收键盘让整屏都归时间线用，滑动空间更大。
    HideKeyboard
    for ($i = 0; $i -lt 6; $i++) {
        AdbQuiet shell input swipe 540 500 540 1500 200
        Start-Sleep -Milliseconds 250
    }
    Start-Sleep -Milliseconds 500
    $approvePoint = Find-ClickableByText '批准执行'
    Info "「批准执行」按钮中心 $($approvePoint -join ',')"
    Screenshot '06a-approval-card-visible'
    Tap $approvePoint
    $res = HarnessRead-Timeout $harness 35 'awaitApproval'
    if (!$res) { Fail '宿主 WaitForApprovalAsync 超时：手机端未回传审批决策。' }
    if (!$res.accepted) { Fail "宿主收到的是拒绝（decision=$($res.decision)），预期 Accepted。" }
    $results.approvalAccepted = $res.decision
    Start-Sleep -Seconds 2
    Screenshot '07-approval-resolved'
    Info "宿主 WaitForApprovalAsync 收到 decision=$($res.decision) accepted=$($res.accepted)"

    # --- 5. 停止（补充链路） ---------------------------------------------------------
    HarnessSend $harness 'status'
    $final = HarnessRead-Timeout $harness 5 'status'
    $results.finalStatus = [ordered]@{
        service          = $final.service
        connections      = $final.connections
        approvalsPending = $final.approvalsPending
        stopCount        = $final.stopCount
        sentMessages     = $final.sent
    }
    $results.outcome = 'pass'
}
catch {
    $results.outcome = 'fail'
    $results.error = $_.Exception.Message
    Write-Host "[and2] FAIL: $($_.Exception.Message)" -ForegroundColor Red
    try { Screenshot '99-failure-state' } catch { }
}
finally {
    if ($harness -and !$harness.HasExited -and !$KeepHarness) {
        try {
            HarnessSend $harness 'quit'
            if (!$harness.WaitForExit(5000)) { $harness.Kill() }
        } catch { }
    }
    if ($harness -and !$KeepHarness) { $harness.Dispose() }
}

$results | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $EvidenceDir 'and2-live-results.json') -Encoding UTF8
Info '===== 结果 ====='
$results | ConvertTo-Json -Depth 6
if ($results.outcome -ne 'pass') { exit 1 }
