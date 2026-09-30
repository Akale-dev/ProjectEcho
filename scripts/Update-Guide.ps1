#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$guidePath = Join-Path $root 'START_HERE.html'
$oldGuide = [IO.File]::ReadAllText($guidePath)
$styleMatch = [regex]::Match($oldGuide, '(?s)<style>(.*?)</style>')
if (-not $styleMatch.Success) { throw 'Existing guide stylesheet missing; not overwriting.' }
$style = $styleMatch.Groups[1].Value
$prompt = [Net.WebUtility]::HtmlEncode([IO.File]::ReadAllText((Join-Path $root 'FIRST_PROMPT.txt')))
$docHtml = foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File | Sort-Object Name)) {
    $markdown = [IO.File]::ReadAllText($file.FullName)
    $title = [Net.WebUtility]::HtmlEncode(($markdown -split '\r?\n')[0].TrimStart('#', ' '))
    $body = (ConvertFrom-Markdown -InputObject $markdown).Html
    # Document-local links should also work from the generated root-level guide.
    $body = [regex]::Replace($body, 'href="((?![a-zA-Z]+:|/|#)[^"#]+\.md)(#[^"]*)?"', 'href="docs/$1$2"')
    '<details class="doc"><summary>' + $title + '</summary><div class="docbody">' + $body + '</div></details>'
}
$html = @"
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>ProjectEcho｜录音与导出验证版</title><style>$style</style></head><body>
<header><div class="eyebrow">PROJECTECHO / WINDOWS DEVELOPMENT</div><h1>先把会议完整录下来。</h1><p>Zoom + Webex · 桌面与Chrome专用实例 · 独立麦克风开关 · 完整混音 · 手动导出</p><span class="badge">2026-09-30 M3 实施</span><span class="badge">录音、MP3/M4A导出、文字工作流与新版界面已实现；现场及模型验收待完成</span></header>
<main class="shell"><nav class="nav"><a href="#start">如何开始</a><a href="#prompt">历史M0提示词</a><a href="#specs">最新规划</a><a href="STATUS.md">开发状态</a></nav>
<section id="start"><h2>录音、导出与文字工作流验证版。</h2><p>默认未分类、日期命名，录后归档；每场确认来源与麦克风，试录由用户主动选择。停止后生成完整混音，提供回听和按大小分段导出。</p><div class="callout">保留原始分轨、真实时间戳、尾音与恢复；M2至少支持WAV及一种经过验证的压缩格式，并交付无需预装SDK/.NET的完整便携包。本地识别可选；不加入SQLite，复杂剪辑延后。</div><p>已实现Windows录音、WAV/M4A/MP3导出、文字导入/修订/搜索和可选本地识别适配；105组默认自动测试通过。便携包包含App、CaptureHost与运行时，不需要模型。双设备同步、干净Windows和未测平台仍须各自验收；应用不自动入会、录音、启mic或上传。</p><p>当前按STATUS继续，下面M0提示词仅作历史。最新情况见 <a href="docs/21_ENGINEERING_SETUP.md">工程环境记录</a>；开发命令使用 <code>scripts/Dotnet.ps1</code> 和 <code>scripts/Dev.ps1</code>。</p></section>
<section id="prompt"><h2>历史M0提示词（当前请按STATUS继续）</h2><textarea id="firstPrompt" readonly>$prompt</textarea><button id="copy">复制提示词</button><span id="copyState" class="small"></span></section>
<section id="specs"><h2>当前规划与历史审查</h2><p class="small">由docs和FIRST_PROMPT生成；更改规划后运行 scripts/Update-Guide.ps1。20为历史审查，当前执行以修订规格和STATUS为准。</p><input class="search" id="filter" aria-label="搜索规划" placeholder="搜索来源、麦克风、M2、验收…"><button id="expand">展开全部</button><button id="collapse" class="secondary">折叠全部</button>$($docHtml -join "`n")</section>
<footer>ProjectEcho · M3实现与未测项分别记录 · 2026-09-30<br>静态校验、工具链验证和真实音频验收分别记录。</footer></main>
<script>
const docs = [...document.querySelectorAll('.doc')];
document.getElementById('filter').addEventListener('input', e => {
 const q=e.target.value.toLocaleLowerCase().trim();
 for (const d of docs) d.hidden=!!q && !d.textContent.toLocaleLowerCase().includes(q);
});
document.getElementById('expand').addEventListener('click',()=>docs.forEach(d=>{if(!d.hidden)d.open=true;}));
document.getElementById('collapse').addEventListener('click',()=>docs.forEach(d=>d.open=false));
document.getElementById('copy').addEventListener('click',async()=>{
 const box=document.getElementById('firstPrompt'), state=document.getElementById('copyState');
 try {await navigator.clipboard.writeText(box.value); state.textContent='已复制。';}
 catch (_) {box.focus();box.select();state.textContent='文本已选中，请按 Ctrl+C 复制。';}
});
</script></body></html>
"@
[IO.File]::WriteAllText($guidePath, $html, [Text.UTF8Encoding]::new($false))
Write-Output ('Updated offline guide from {0} Markdown files.' -f @($docHtml).Count)


