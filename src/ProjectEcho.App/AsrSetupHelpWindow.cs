using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ProjectEcho.App;

/// <summary>Local, readable setup instructions. Only an explicit official-link click opens a browser.</summary>
internal sealed class AsrSetupHelpWindow : Window
{
    private const string ReleasesUrl = "https://github.com/ggml-org/whisper.cpp/releases/tag/b5130";
    private const string VulkanUrl = "https://github.com/ggml-org/whisper.cpp/blob/b5130/README.md#vulkan-gpu-support";
    private const string ModelsUrl = "https://huggingface.co/ggerganov/whisper.cpp/tree/main";
    private const string ModelGuideUrl = "https://github.com/ggml-org/whisper.cpp/blob/b5130/models/README.md";
    private readonly string language = UiText.Language;
    private readonly TextBlock linkStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
    internal TabControl TopicTabs { get; } = new();
    internal ScrollViewer HelpScroller => (ScrollViewer)((TabItem)TopicTabs.SelectedItem).Content;
    internal Button CloseButton { get; } = new();
    internal Action<string> OpenLink { get; set; } = static url =>
    {
        using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    };

    internal AsrSetupHelpWindow(int selectedTopic = 0)
    {
        Title = T("本地识别 · 配置教学", "本機辨識 · 設定教學", "Local transcription · Setup guide");
        double availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 40);
        double availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(820, availableWidth); Height = Math.Min(780, availableHeight);
        MinWidth = Math.Min(640, availableWidth); MinHeight = Math.Min(560, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new TextBlock
        {
            Text = T("需要准备两样东西：识别程序负责运行，模型文件提供语言识别所需的知识。先配好 CPU 路径，再按需要使用显卡。",
                "需要準備兩樣東西：辨識程式負責執行，模型檔案提供語言辨識所需的知識。先設定好 CPU 路徑，再按需要使用顯示卡。",
                "You need two files: an engine that runs transcription and a model that contains its learned speech knowledge. Start with CPU, then set up GPU acceleration if needed."),
            TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(intro);
        TopicTabs.Items.Add(new TabItem { Header = T("1. 识别程序", "1. 辨識程式", "1. Engine"), Content = CreatePage(BuildEnginePage()) });
        TopicTabs.Items.Add(new TabItem { Header = T("2. 模型文件", "2. 模型檔案", "2. Model"), Content = CreatePage(BuildModelPage()) });
        TopicTabs.SelectedIndex = selectedTopic == 1 ? 1 : 0;
        AutomationProperties.SetName(TopicTabs, T("配置教学主题", "設定教學主題", "Setup guide topics"));
        Grid.SetRow(TopicTabs, 1); root.Children.Add(TopicTabs);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linkStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        linkStatus.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(linkStatus);
        CloseButton.Content = T("关闭", "關閉", "Close"); CloseButton.IsCancel = true;
        CloseButton.HorizontalAlignment = HorizontalAlignment.Right; CloseButton.Margin = new Thickness(0);
        CloseButton.Click += (_, _) => Close();
        Grid.SetColumn(CloseButton, 1); footer.Children.Add(CloseButton);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
    }

    private static ScrollViewer CreatePage(StackPanel panel) => new()
    {
        Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4, 12, 14, 8),
        CanContentScroll = false
    };

    private StackPanel BuildEnginePage()
    {
        var page = new StackPanel();
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "bundled-whisper.json")))
        {
            Section(page, T("完整包已准备好，可先直接使用", "完整包已準備好，可先直接使用", "The complete package is ready to use"));
            Text(page, T("这个完整包已经附带 CPU 引擎、Vulkan 引擎和 base 多语言模型，默认用 CPU，无需另外下载。使用本包引擎时，硬件选 Vulkan 会自动切到 Tools → whisper-vulkan 内的 whisper-cli.exe，切回 CPU 也会选对应程序；界面显示实际路径。设备编号先用 0；需要供新会议沿用时，可点“保存为默认配置”。提示词自动保存在当前会议，不进入默认配置。需要电脑已具备支持 Vulkan 的显卡驱动。自行选择的包外或自定义程序不会自动替换，必须自行匹配硬件。不要把引擎从原文件夹移走。",
                "這個完整包已附帶 CPU 引擎、Vulkan 引擎和 base 多語言模型，預設使用 CPU，無需另行下載。使用本包引擎時，硬體選 Vulkan 會自動切換至 Tools → whisper-vulkan 內的 whisper-cli.exe，切回 CPU 也會選擇對應程式；介面顯示實際路徑。裝置編號先用 0；需要供新會議沿用時，可點「儲存為預設設定」。提示詞自動儲存在目前會議，不進入預設設定。電腦需要已具備支援 Vulkan 的顯示卡驅動程式。自行選擇的包外或自訂程式不會自動替換，必須自行配對硬體。不要移動引擎檔案。",
                "This complete package includes separate CPU and Vulkan engines plus the multilingual base model. CPU is selected by default. When using these bundled engines, choosing Vulkan selects Tools → whisper-vulkan → whisper-cli.exe, and choosing CPU selects the CPU engine. The actual path is shown. Start with device 0. Choose Save as default only to apply these settings to new meetings; prompts save automatically with the current meeting. A compatible Vulkan graphics driver is still required. External or custom executables are kept as selected and must match the requested backend. Keep the engine files in their folders."));
        }
        Section(page, T("第一步：确认自己用哪种硬件", "第一步：確認自己使用哪種硬體", "Step 1: Find out which hardware you have"));
        Text(page, T("按 Ctrl + Shift + Esc 打开 Windows 任务管理器，进入“性能”，查看 GPU 0 / GPU 1 页右上方的显卡名称。AMD Radeon 通常选择 Vulkan 路径；NVIDIA 也可选择 Vulkan 路径。不确定型号、只有集成显卡或第一次配置时，先选择 CPU。这里的 GPU 0 / GPU 1 不等于识别程序里的设备编号。",
            "按 Ctrl + Shift + Esc 開啟 Windows 工作管理員，進入「效能」，查看 GPU 0 / GPU 1 頁右上方的顯示卡名稱。AMD Radeon 通常選擇 Vulkan 路徑；NVIDIA 也可選擇 Vulkan 路徑。不確定型號、只有整合式顯示卡或第一次設定時，先選擇 CPU。這裡的 GPU 0 / GPU 1 不等於辨識程式裡的裝置編號。",
            "Press Ctrl + Shift + Esc to open Windows Task Manager. Under Performance, open GPU 0 / GPU 1 and read the device name. AMD Radeon normally uses the Vulkan route; NVIDIA can also use Vulkan. If you are unsure, have only integrated graphics, or are setting this up for the first time, start with CPU. Task Manager's GPU numbers are not the engine's device numbers."));

        Section(page, T("第二步：下载适合 Windows x64 的识别程序", "第二步：下載適合 Windows x64 的辨識程式", "Step 2: Get an engine for Windows x64"));
        Text(page, T("点击下面的官方发行页，向下找到 Assets（发布文件），必要时点击 Show all assets。ProjectEcho 使用 Windows x64；不要选 ARM64、Win32 或 Source code（源代码）。",
            "點擊下方官方發行頁，向下找到 Assets（發行檔案），必要時點擊 Show all assets。ProjectEcho 使用 Windows x64；不要選 ARM64、Win32 或 Source code（原始碼）。",
            "Open the official release page below. Scroll to Assets and select Show all assets if needed. ProjectEcho uses Windows x64. Do not choose ARM64, Win32, or Source code archives."));
        Link(page, T("打开官方 b5130 发行页（GitHub）", "開啟官方 b5130 發行頁（GitHub）", "Open the official b5130 release (GitHub)"), OfficialPage.Releases);
        Text(page, T("CPU：选择 whisper-bin-x64.zip。它适合先完成安装和短音频测试，无需专用显卡。之后在本地识别页面把硬件选为 CPU。",
            "CPU：選擇 whisper-bin-x64.zip。它適合先完成安裝和短音訊測試，無需專用顯示卡。之後在本機辨識頁面將硬體選為 CPU。",
            "CPU: choose whisper-bin-x64.zip. This is the easiest starting point for a short test and does not require a dedicated GPU. Select CPU in ProjectEcho's local transcription page."));
        Text(page, T("AMD / Vulkan：b5130 官方发行页目前没有 Windows x64 Vulkan 下载包。CPU 包不能通过切换选项变成 Vulkan 包。你可以先使用 CPU；如需 Vulkan，请让熟悉安装的人按下方官方说明编译同版本程序，或使用来源可信且明确包含 Vulkan 后端的同版本构建，再选择其中的 whisper-cli.exe。ProjectEcho 不会自动下载、编译或更改显卡驱动。",
            "AMD / Vulkan：b5130 官方發行頁目前沒有 Windows x64 Vulkan 下載包。CPU 包不能透過切換選項變成 Vulkan 包。你可以先使用 CPU；如需 Vulkan，請讓熟悉安裝的人按下方官方說明編譯同版本程式，或使用來源可信且明確包含 Vulkan 後端的同版本建置，再選擇其中的 whisper-cli.exe。ProjectEcho 不會自動下載、編譯或更改顯示卡驅動程式。",
            "AMD / Vulkan: the official b5130 release currently has no Windows x64 Vulkan archive. A CPU build cannot become a Vulkan build by changing a setting. Start with CPU, or ask someone with build experience to follow the official Vulkan instructions. You can also use a trusted same-version build that explicitly includes Vulkan, then select its whisper-cli.exe. ProjectEcho does not download, compile, or change graphics drivers automatically."));
        Link(page, T("查看官方 Vulkan 编译说明（GitHub）", "查看官方 Vulkan 編譯說明（GitHub）", "Read the official Vulkan build instructions (GitHub)"), OfficialPage.Vulkan);

        Section(page, T("第三步：完整解压，再选择 exe", "第三步：完整解壓縮，再選擇 exe", "Step 3: Extract the whole archive and select the exe"));
        Text(page, T("在资源管理器中右键 ZIP →“全部解压缩”，保存到一个以后不移动的本地文件夹，例如你自己的“文档”下的 Whisper 文件夹。保留整个解压目录，尤其是与程序在一起的 DLL；不要只复制 exe，也不要直接从 ZIP 里面运行。",
            "在檔案總管中右鍵 ZIP →「全部解壓縮」，儲存到一個以後不移動的本機資料夾，例如你自己的「文件」下的 Whisper 資料夾。保留整個解壓縮目錄，尤其是與程式放在一起的 DLL；不要只複製 exe，也不要直接從 ZIP 裡執行。",
            "In File Explorer, right-click the ZIP and choose Extract All. Use a stable local folder, such as a Whisper folder under your Documents. Keep the entire extracted folder, including the DLL files beside the program. Do not copy only the exe or run it from inside the ZIP."));
        Text(page, T("回到 ProjectEcho → 本地识别 →“选择 whisper-cli.exe”，找到解压目录中的 Release 文件夹并选 whisper-cli.exe。不要选 whisper-server.exe、bench.exe 或 main.exe。接着切换本教学的“模型文件”页，准备一个模型。",
            "回到 ProjectEcho → 本機辨識 →「選擇 whisper-cli.exe」，找到解壓縮目錄中的 Release 資料夾並選 whisper-cli.exe。不要選 whisper-server.exe、bench.exe 或 main.exe。接著切換本教學的「模型檔案」頁，準備一個模型。",
            "Return to ProjectEcho → Local transcription → Choose whisper-cli.exe. Open the extracted Release folder and select whisper-cli.exe, not whisper-server.exe, bench.exe, or main.exe. Next, use this guide's Model tab to prepare a model."));

        Section(page, T("第四步：选硬件，做一次短音频测试", "第四步：選硬體，做一次短音訊測試", "Step 4: Select hardware and test a short recording"));
        Text(page, T("CPU 会明确关闭 GPU；自动模式会尝试所选程序可用的 GPU，也允许回退 CPU。Vulkan 模式要求实际引擎报告对应后端，不能仅凭选中按钮判断已加速。GPU 设备编号先用 0（引擎列出的第一个设备）；多显卡需要按该引擎的设备顺序确认，不能照抄任务管理器编号。CPU 线程先保留 2，再结合录音和识别时的负载调整。",
            "CPU 會明確關閉 GPU；自動模式會嘗試所選程式可用的 GPU，也允許退回 CPU。Vulkan 模式要求實際引擎回報對應後端，不能僅憑選中按鈕判斷已加速。GPU 裝置編號先用 0（引擎列出的第一個裝置）；多顯示卡需要按該引擎的裝置順序確認，不能照抄工作管理員編號。CPU 執行緒先保留 2，再結合錄音和辨識時的負載調整。",
            "CPU explicitly disables GPU use. Auto tries a GPU available to the selected engine and permits CPU fallback. Vulkan require the engine to report the matching backend; selecting an option alone does not prove acceleration. Start with GPU device 0, the first device in the engine's own list. For multiple GPUs, confirm that list instead of copying Task Manager numbers. Keep CPU threads at 2 initially, then adjust for your recording and transcription workload."));

        Section(page, T("如果打不开或识别失败", "如果無法開啟或辨識失敗", "If the engine does not start or transcription fails"));
        Text(page, T(@"• 提示缺少 DLL：重新完整解压正确的 x64 包，并保留程序旁全部文件；如缺 Microsoft Visual C++ 运行库，请向 IT 或微软官网求助，不要随意找单个 DLL。
• 提示 Vulkan 后端不匹配或没有 GPU：确认选的程序确实包含该后端、设备编号有效、驱动兼容；可主动改回 CPU 完成识别。
• 提示内存或显存不足：先换较小模型；显存不足时可尝试 CPU。不要假定同一显卡能运行所有模型。
• 程序路径失效：如果移动过文件夹，重新选择其中的 whisper-cli.exe。",
            @"• 提示缺少 DLL：重新完整解壓縮正確的 x64 包，並保留程式旁全部檔案；如缺 Microsoft Visual C++ 執行階段程式庫，請向 IT 或微軟官網求助，不要隨意找單個 DLL。
• 提示 Vulkan 後端不符或沒有 GPU：確認選的程式確實包含該後端、裝置編號有效、驅動程式相容；可主動改回 CPU 完成辨識。
• 提示記憶體或顯示記憶體不足：先換較小模型；顯示記憶體不足時可嘗試 CPU。不要假定同一顯示卡能執行所有模型。
• 程式路徑失效：如果移動過資料夾，重新選擇其中的 whisper-cli.exe。",
            @"• Missing DLL: extract the correct x64 archive again and keep all files together. If Microsoft Visual C++ runtime is missing, use Microsoft’s official site or ask IT, rather than fetching individual DLLs.
• Missing or mismatched Vulkan backend: check the engine build, device number, and driver compatibility. You can explicitly switch to CPU to continue.
• Not enough memory or VRAM: use a smaller model. For a VRAM limit, CPU may be an alternative. A GPU is not guaranteed to fit every model.
• Missing program path: if you moved the folder, select whisper-cli.exe again from its new location."));
        return page;
    }

    private StackPanel BuildModelPage()
    {
        var page = new StackPanel();
        Section(page, T("第一步：先选多语言 base 模型", "第一步：先選多語言 base 模型", "Step 1: Start with the multilingual base model"));
        Text(page, T("模型是一个 .bin 文件，不是需要双击安装的程序。第一次建议用 ggml-base.bin 跑通流程；它包含中文等多语言支持。名字里的 .en 表示仅英文，所以中文或中英混合会议不要选 ggml-base.en.bin。",
            "模型是一個 .bin 檔案，不是需要按兩下安裝的程式。第一次建議用 ggml-base.bin 跑通流程；它包含中文等多語言支援。名稱裡的 .en 表示僅英文，所以中文或中英混合會議不要選 ggml-base.en.bin。",
            "A model is a .bin file, not an installer to double-click. Start with ggml-base.bin to check your setup. It supports multiple languages, including Chinese. The .en suffix means English-only, so do not select ggml-base.en.bin for Chinese or mixed Chinese/English meetings."));
        Text(page, T("跑通以后，可以分别试 ggml-small.bin、ggml-large-v3-turbo.bin 或 ggml-large-v3.bin。它们需要不同的磁盘、内存和运算资源；大模型不保证每段录音都更准确，速度也取决于硬件。用同一段短录音比较公司名、数字、否定句和中英切换，再决定日常使用哪一个。",
            "跑通以後，可以分別試 ggml-small.bin、ggml-large-v3-turbo.bin 或 ggml-large-v3.bin。它們需要不同的磁碟、記憶體和運算資源；大模型不保證每段錄音都更準確，速度也取決於硬體。用同一段短錄音比較公司名稱、數字、否定句和中英切換，再決定日常使用哪一個。",
            "After a successful test, you can compare ggml-small.bin, ggml-large-v3-turbo.bin, and ggml-large-v3.bin. They have different storage, memory, and processing needs. A larger model does not guarantee better results on every recording; speed also depends on hardware. Compare company names, numbers, negations, and language switches on the same short recording before choosing your everyday model."));

        Section(page, T("第二步：手动下载真正的模型文件", "第二步：手動下載真正的模型檔案", "Step 2: Download the actual model file"));
        Text(page, T("打开下方官方模型库，在 Files / Files and versions 文件列表找到 ggml-base.bin，点击文件名，再点下载图标或 Download。等浏览器显示下载完成，把文件放在固定的本地文件夹，例如 Whisper\\models。",
            "開啟下方官方模型庫，在 Files / Files and versions 檔案列表找到 ggml-base.bin，點擊檔名，再點下載圖示或 Download。等瀏覽器顯示下載完成，將檔案放在固定的本機資料夾，例如 Whisper\\models。",
            "Open the official model repository below. Under Files / Files and versions, find ggml-base.bin, open it, and choose the download icon or Download. Wait until the browser says the download is complete, then keep the file in a stable local folder, such as Whisper\\models."));
        Link(page, T("打开官方模型文件列表（Hugging Face）", "開啟官方模型檔案列表（Hugging Face）", "Open the official model files (Hugging Face)"), OfficialPage.Models);
        Link(page, T("查看官方模型格式与列表说明（GitHub）", "查看官方模型格式與列表說明（GitHub）", "Read the official model format guide (GitHub)"), OfficialPage.ModelGuide);
        Text(page, T("需要的是 whisper.cpp 使用的 ggml-….bin 文件。不要用“网页另存为”保存出来的 HTML，不要选还未下载完成的 .crdownload / .part 文件，也不要选 ZIP、OpenAI 的 .pt 或 GGUF 模型。更改文件后缀不会转换模型格式。不要下载以 for-tests- 开头的测试文件，它们没有真正的模型权重。",
            "需要的是 whisper.cpp 使用的 ggml-….bin 檔案。不要用「網頁另存新檔」儲存出來的 HTML，不要選尚未下載完成的 .crdownload / .part 檔案，也不要選 ZIP、OpenAI 的 .pt 或 GGUF 模型。更改副檔名不會轉換模型格式。不要下載以 for-tests- 開頭的測試檔案，它們沒有真正的模型權重。",
            "You need a ggml-….bin file for whisper.cpp. Do not use an HTML page saved with Save Page As, an unfinished .crdownload / .part download, a ZIP archive, an OpenAI .pt file, or a GGUF model. Renaming the extension does not convert a model. Avoid files starting with for-tests-; those do not contain real model weights."));

        Section(page, T("第三步：在 ProjectEcho 中选择文件", "第三步：在 ProjectEcho 中選擇檔案", "Step 3: Select the file in ProjectEcho"));
        Text(page, T("回到本地识别页面，点击“选择模型”，选刚才下载完成的 .bin 文件。确认程序路径和模型路径都显示了真实文件，再阅读并勾选模型来源与许可确认；本场选项在开始识别时保留，“保存为默认配置”只用于新会议。以后如果移动或删除模型，需要重新选择文件；不用填写 SHA-256 比对值。",
            "回到本機辨識頁面，點擊「選擇模型」，選剛才下載完成的 .bin 檔案。確認程式路徑和模型路徑都顯示了真實檔案，再閱讀並勾選模型來源與授權確認；本場選項在開始辨識時保留，「儲存為預設設定」只用於新會議。以後如果移動或刪除模型，需要重新選擇檔案；不用填寫 SHA-256 比對值。",
            "Return to Local transcription and choose the downloaded .bin model. Check that both the engine and model paths point to real files, review and acknowledge the model's source and license. Starting recognition retains the settings for this meeting; Save as default applies them only to new meetings. If you later move or delete the model, select it again. You do not need to enter a SHA-256 comparison value."));

        Section(page, T("第四步：语言与电话会提示词", "第四步：語言與電話會提示詞", "Step 4: Language and earnings-call hints"));
        Text(page, T("auto = 自动判断语言；zh = 中文；en = 英文。中文或中英混合内容需要多语言模型。自动检测也可能判断错误，识别后应核对原音频。",
            "auto = 自動判斷語言；zh = 中文；en = 英文。中文或中英混合內容需要多語言模型。自動偵測也可能判斷錯誤，辨識後應核對原音訊。",
            "auto detects the language; zh selects Chinese; en selects English. Chinese and mixed-language recordings require a multilingual model. Automatic detection can still be wrong, so check the transcript against the audio."));
        Text(page, T("初始提示词可留空，也可以写少量公司名、人名、ticker 和行业术语。例如：\n腾讯 Tencent，0700.HK，TCEHY；王先生；EBITDA，自由现金流，毛利率。\n用真实相关的短词即可，不必写长篇会议背景或要求模型编造内容。模型有自己的 token 上限，提示词不是强制词典，也不保证数字、人名或术语准确。",
            "初始提示詞可留空，也可以寫少量公司名稱、人名、ticker 和產業術語。例如：\n騰訊 Tencent，0700.HK，TCEHY；王先生；EBITDA，自由現金流，毛利率。\n使用真實相關的短詞即可，不必寫長篇會議背景或要求模型編造內容。模型有自己的 token 上限，提示詞不是強制詞典，也不保證數字、人名或術語準確。",
            "Initial prompt is optional. Add a few relevant company names, people's names, tickers, or industry terms. For example:\nTencent, 0700.HK, TCEHY; Mr. Wang; EBITDA, free cash flow, gross margin.\nKeep it short and relevant. You do not need a long meeting brief or instructions to invent content. The model has its own token limit. A prompt is not a mandatory dictionary and does not guarantee accurate names, numbers, or terminology."));

        Section(page, T("第五步：开始识别并比较版本", "第五步：開始辨識並比較版本", "Step 5: Transcribe and compare versions"));
        Text(page, T("先选一场已保存的短录音，主动点击开始本地识别。首次加载模型可能需要等待。更换模型或提示词后再次识别，会产生新版本，旧识别和人工校订仍然保留。回听或字幕校订时，用版本选择框切换比较。识别成功只表示结果已保存，金融数字、否定句与人名仍需结合录音核对。",
            "先選一場已儲存的短錄音，主動點擊開始本機辨識。首次載入模型可能需要等待。更換模型或提示詞後再次辨識，會產生新版本，舊辨識和人工校訂仍然保留。回聽或字幕校訂時，使用版本選擇框切換比較。辨識成功只表示結果已儲存，財務數字、否定句與人名仍需結合錄音核對。",
            "Choose a short saved recording and start local transcription yourself. The first model load may take time. Running another model or prompt creates a new version and preserves earlier results and manual corrections. Use the version selector during playback or subtitle review to compare them. Success means the result was saved; check financial figures, negations, and names against the recording."));

        Section(page, T("常见模型问题", "常見模型問題", "Common model problems"));
        Text(page, T(@"• 找不到模型：检查文件是否移动、仍在下载，或者选成了快捷方式；重新选择实际 .bin 文件。
• 模型无法加载：确认是完整下载的 whisper.cpp ggml 模型，而不是网页、压缩包或其他格式。
• 大模型运行很慢或内存不足：先换 base / small 并用短录音测试；显卡是否可用仍取决于识别程序的后端。
• 中文结果异常：确认模型名不含 .en，再检查语言设置、录音质量及提示词，保留旧版本便于比较。",
            @"• 找不到模型：檢查檔案是否移動、仍在下載，或是否選成了捷徑；重新選擇實際 .bin 檔案。
• 模型無法載入：確認是完整下載的 whisper.cpp ggml 模型，而不是網頁、壓縮包或其他格式。
• 大模型執行很慢或記憶體不足：先換 base / small 並用短錄音測試；顯示卡是否可用仍取決於辨識程式的後端。
• 中文結果異常：確認模型名稱不含 .en，再檢查語言設定、錄音品質及提示詞，保留舊版本以便比較。",
            @"• Model not found: check whether the file was moved, is still downloading, or is only a shortcut. Select the actual .bin file again.
• Model will not load: make sure the download is complete and uses the whisper.cpp ggml format, not a web page, archive, or another model format.
• Large model is slow or exceeds memory: try base / small on a short recording first. GPU use still depends on the selected engine's backend.
• Poor Chinese results: check that the model name has no .en suffix, then review the language, recording quality, and prompt. Keep earlier versions for comparison."));
        return page;
    }

    private void Section(Panel page, string text)
    {
        var heading = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, page.Children.Count == 0 ? 0 : 14, 0, 8)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); page.Children.Add(heading);
    }

    private static void Text(Panel page, string text)
    {
        var paragraph = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0, 0, 0, 10) };
        paragraph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); page.Children.Add(paragraph);
    }

    private void Link(Panel page, string label, OfficialPage destination)
    {
        // Destination is a closed enum, not a user-entered URL or filesystem path. The click
        // captures this constant; changing a control's Tag cannot alter where it navigates.
        string url = destination switch
        {
            OfficialPage.Releases => ReleasesUrl, OfficialPage.Vulkan => VulkanUrl,
            OfficialPage.Models => ModelsUrl, OfficialPage.ModelGuide => ModelGuideUrl,
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Tag = url,
            ToolTip = url, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            try
            {
                OpenLink(url);
                linkStatus.Text = T("已请求在默认浏览器打开官方页面。下载需要你在浏览器中操作。",
                    "已要求在預設瀏覽器開啟官方頁面。下載需要你在瀏覽器中操作。",
                    "Requested the official page in your default browser. Download files yourself in the browser.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                linkStatus.Text = T("无法打开浏览器：", "無法開啟瀏覽器：", "Could not open the browser: ") + ex.Message;
            }
        };
        page.Children.Add(button);
    }

    private string T(string simplified, string traditional, string english) => language switch
    { "en" => english, "zh-Hant" => traditional, _ => simplified };

    private enum OfficialPage { Releases, Vulkan, Models, ModelGuide }
}
