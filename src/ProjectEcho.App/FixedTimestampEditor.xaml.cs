using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Core;

namespace ProjectEcho.App;

/// <summary>
/// An hours/minutes/seconds/centiseconds editor with immutable punctuation.
/// Incomplete drafts remain invalid and recoverable until the user finishes or clears them.
/// </summary>
public partial class FixedTimestampEditor : UserControl
{
    private bool updating;
    private string text = "";
    private TextBox[] Fields => [HoursBox, MinutesBox, SecondsBox, CentisecondsBox];

    public FixedTimestampEditor()
    {
        InitializeComponent();
        foreach (TextBox field in Fields)
        {
            field.TextChanged += Field_TextChanged;
            field.PreviewTextInput += Field_PreviewTextInput;
            field.GotKeyboardFocus += (_, _) => field.SelectAll();
            field.LostKeyboardFocus += (_, _) =>
            {
                // Make the decimal display agree with the integer-centisecond field.
                // Never repair an empty or nonnumeric incomplete draft.
                if (field.Text.Length > 0 && field.Text.All(c => c is >= '0' and <= '9'))
                    field.Text = field.Text.PadLeft(2, '0');
            };
            field.PreviewMouseLeftButtonDown += Field_PreviewMouseLeftButtonDown;
            DataObject.AddPastingHandler(field, Field_Pasting);
        }
        RefreshLanguage();
    }

    public event EventHandler? TextChanged;

    // The string bridge preserves the existing revision-draft API. Valid legacy
    // MM:ss input is converted by the same parser used by draft validation.
    public string Text
    {
        get => text;
        set
        {
            value ??= "";
            if (text == value) return;
            string previous = text;
            updating = true;
            try
            {
                bool valid = TranscriptRevisionDraft.TryParseTimestamp(value, out long? milliseconds);
                string display = valid ? milliseconds is { } ms ? TimelineTimestamp.FromMilliseconds(ms) : "" : value;
                var parts = Regex.Match(display, @"\A(?<h>[0-9]{0,3}):(?<m>[0-9]{0,2}):(?<s>[0-9]{0,2})(?:\.(?<f>[0-9]{0,2}))?\z", RegexOptions.CultureInvariant);
                bool empty = valid && milliseconds is null;
                bool hasFields = empty || parts.Success;
                DigitFields.Visibility = hasFields ? Visibility.Visible : Visibility.Collapsed;
                RawInputText.Visibility = hasFields ? Visibility.Collapsed : Visibility.Visible;
                RawInputText.Text = hasFields ? "" : value;
                RawInputText.ToolTip = hasFields ? null : value;
                HoursBox.Text = empty ? "" : parts.Groups["h"].Value;
                MinutesBox.Text = empty ? "" : parts.Groups["m"].Value;
                SecondsBox.Text = empty ? "" : parts.Groups["s"].Value;
                CentisecondsBox.Text = empty ? "" : parts.Groups["f"].Value;
                // Display two decimals but retain original milliseconds until this editor is changed.
                // Preserve unfinished or otherwise invalid drafts exactly, including
                // a missing fraction separator; selection/navigation must not repair them.
                text = valid && hasFields ? TranscriptRevisionDraft.FormatTimestamp(milliseconds) : value;
            }
            finally { updating = false; }
            if (text != previous) TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear() => Text = "";

    public void RefreshLanguage()
    {
        SetFieldDescription(HoursBox, "小时（0–999）");
        SetFieldDescription(MinutesBox, "分钟（0–59）");
        SetFieldDescription(SecondsBox, "秒（0–59）");
        SetFieldDescription(CentisecondsBox, "百分秒（0–99，1 表示 0.01 秒）");
        string clear = UiText.T("清空此时间（Ctrl+Delete）；开始和结束同时清空表示无时间。");
        ClearButton.ToolTip = clear;
        AutomationProperties.SetName(ClearButton, UiText.T("清空此时间"));
        AutomationProperties.SetHelpText(ClearButton, clear);
        AutomationProperties.SetHelpText(this, UiText.T("依次填写小时、分钟、秒、百分秒，只输入数字。冒号和小数点固定；点击 × 或按 Ctrl+Delete 清空此时间。"));
        AutomationProperties.SetName(RawInputText, UiText.T("未完成的时间草稿，请清空后重新填写各数字栏。"));
    }

    private static void SetFieldDescription(TextBox field, string source)
    {
        string label = UiText.T(source);
        AutomationProperties.SetName(field, label);
        field.ToolTip = label + "\n" + UiText.T("只输入数字；不接受带冒号、小数点或空格的粘贴内容。");
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating) return;
        // A first digit on a genuinely untimed cue initializes the other fields.
        // Clearing one field of an existing timestamp instead leaves an invalid,
        // incomplete draft: it must never silently become zero on navigation.
        if (text.Length == 0 && sender is TextBox edited && edited.Text.Length > 0)
        {
            updating = true;
            try
            {
                foreach (TextBox field in Fields)
                    if (field != edited && field.Text.Length == 0) field.Text = "00";
            }
            finally { updating = false; }
        }
        string previous = text;
        text = Fields.All(field => field.Text.Length == 0) ? "" :
            $"{Component(HoursBox, 2)}:{Component(MinutesBox, 2)}:{Component(SecondsBox, 2)}.{Component(CentisecondsBox, 2)}";
        if (text != previous) TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Component(TextBox field, int digits) =>
        field.Text.Length == 0 ? "" : field.Text.PadLeft(digits, '0');

    private static bool CanInsert(TextBox field, string inserted) => inserted.Length > 0 &&
        inserted.All(character => character is >= '0' and <= '9') &&
        field.Text.Length - field.SelectionLength + inserted.Length <= field.MaxLength;

    private static void Field_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox field && !CanInsert(field, e.Text)) e.Handled = true;
    }

    private static void Field_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox field || !e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string inserted || !CanInsert(field, inserted))
            e.CancelCommand();
    }

    private static void Field_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox field && !field.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            field.Focus();
            field.SelectAll();
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // WPF TextBox can handle Space through KeyDown rather than TextInput.
        if (e.Key == Key.Space && e.OriginalSource is TextBox) { e.Handled = true; return; }
        if (e.Key == Key.Delete && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Clear();
            HoursBox.Focus();
            e.Handled = true;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Clear();
        HoursBox.Focus();
    }
}
