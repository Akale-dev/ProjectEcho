using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private string PreferencesPath => Path.Combine(projectRoot,
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.json")) ? "Data" : ".local", "settings", "interface.json");

    private string ReadInterfaceLanguage()
    {
        try
        {
            string path = PreferencesPath; RejectReparseAncestors(path);
            if (File.Exists(path) && new FileInfo(path).Length <= 4096)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("language", out var language)) return language.GetString() ?? "zh-CN";
            }
        }
        catch { /* A damaged preference must not prevent recording. */ }
        return "zh-CN";
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LanguageButton, Placement = PlacementMode.Top };
        foreach (var (code, label) in new[] { ("zh-CN", "简体中文"), ("zh-Hant", "繁體中文"), ("en", "English") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = UiText.Language == code };
            item.Click += (_, _) => ChangeLanguage(code, persist: true);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void ChangeLanguage(string code, bool persist)
    {
        UiText.SetLanguage(code);
        UpdateLanguageLabel();
        UpdateNavigation(); UpdateControls();
        MeetingCountText.Text = libraryRead ? UiText.F($"{RecoveryBox.Items.Count} 场会议") + (LibraryFilterActive ? UiText.T(" · 已筛选") : "")
            : UiText.T("点击会议库读取历史");
        LibraryFilterButton.ToolTip = UiText.T(LibraryFilterActive ? "筛选和排序会议（已筛选）" : "筛选和排序会议");
        EnvironmentText.Text = UiText.F($"Windows build {Environment.OSVersion.Version.Build} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · NAudio 3.1.0 · 不采集的预检");
        // Reevaluate display-only row labels. User-authored titles/text are never converted.
        RecoveryBox.Items.Refresh(); ReplayRevisionBox.Items.Refresh();
        transcriptionView?.RevisionBox.Items.Refresh();
        if (!persist) return;
        try
        {
            string path = PreferencesPath; RejectReparseAncestors(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { schemaVersion = 1, language = UiText.Language });
                stream.Flush(true);
            }
            RejectReparseAncestors(path); File.Move(pending, path, true);
        }
        catch (Exception ex) { SetStatus(UiText.T("语言已切换，但偏好未能保存：") + ex.Message); }
    }

    private void UpdateLanguageLabel() => LanguageButton.Content = UiText.Language switch
    { "en" => "Language · English ▾", "zh-Hant" => "語言 · 繁體中文 ▾", _ => "语言 · 简体中文 ▾" };
}
