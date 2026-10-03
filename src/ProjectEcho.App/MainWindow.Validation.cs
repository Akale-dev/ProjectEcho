using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private readonly Dictionary<FrameworkElement, FieldValidationIssue> fieldValidationIssues = new();
    private readonly Dictionary<FrameworkElement, Func<bool>> fieldValidationCorrections = new();

    private void SetValidationIssue(FrameworkElement field, string message)
    {
        if (!fieldValidationIssues.TryGetValue(field, out var issue))
        {
            issue = new FieldValidationIssue(AutomationProperties.GetHelpText(field));
            fieldValidationIssues.Add(field, issue);
            field.Loaded += ValidationField_Loaded;
            field.Unloaded += ValidationField_Unloaded;
        }
        AutomationProperties.SetHelpText(field, message);
        AttachValidationOutline(field, issue);
    }

    private void ClearValidationIssue(FrameworkElement field)
    {
        if (!fieldValidationIssues.Remove(field, out var issue)) return;
        if (issue.Outline is { } outline) issue.Layer?.Remove(outline);
        field.Loaded -= ValidationField_Loaded;
        field.Unloaded -= ValidationField_Unloaded;
        AutomationProperties.SetHelpText(field, issue.PreviousHelpText);
    }

    private void ClearValidationIssues(params FrameworkElement[] fields)
    {
        foreach (var field in fields) ClearValidationIssue(field);
    }

    private bool HasValidationIssue(FrameworkElement field) => fieldValidationIssues.ContainsKey(field);

    private void FocusFirstValidationIssue(IEnumerable<FrameworkElement> fields)
    {
        var first = fields.FirstOrDefault(HasValidationIssue);
        if (first is null) return;
        // Validate before disabling work controls. Delayed layout also supports a just-opened tab.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!HasValidationIssue(first) || WindowCloseRequested) return;
            first.BringIntoView(); first.Focus();
            if (fieldValidationIssues.TryGetValue(first, out var issue)) AttachValidationOutline(first, issue);
        }));
    }

    private void TrackValidationCorrection(FrameworkElement field, Func<bool> isValid)
    {
        if (fieldValidationCorrections.TryAdd(field, isValid))
        {
            field.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ValidationText_Changed));
            field.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ValidationSelection_Changed));
            field.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(ValidationToggle_Changed));
            field.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(ValidationToggle_Changed));
        }
        else fieldValidationCorrections[field] = isValid;
    }

    private void ValidationText_Changed(object sender, TextChangedEventArgs e) => ClearCorrectedValidation(sender);
    private void ValidationSelection_Changed(object sender, SelectionChangedEventArgs e) => ClearCorrectedValidation(sender);
    private void ValidationToggle_Changed(object sender, RoutedEventArgs e) => ClearCorrectedValidation(sender);
    private void ClearCorrectedValidation(object sender)
    {
        if (sender is FrameworkElement field && HasValidationIssue(field) &&
            fieldValidationCorrections.TryGetValue(field, out var isValid) && isValid()) ClearValidationIssue(field);
    }

    private void ValidationField_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement field && fieldValidationIssues.TryGetValue(field, out var issue)) AttachValidationOutline(field, issue);
    }

    private void ValidationField_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement field && fieldValidationIssues.TryGetValue(field, out var issue))
        {
            if (issue.Outline is { } outline) issue.Layer?.Remove(outline);
            issue.Layer = null; issue.Outline = null;
        }
    }

    private static void AttachValidationOutline(FrameworkElement field, FieldValidationIssue issue)
    {
        if (!field.IsLoaded || issue.Outline is not null || AdornerLayer.GetAdornerLayer(field) is not { } layer) return;
        issue.Layer = layer; issue.Outline = new ValidationOutline(field); layer.Add(issue.Outline);
    }

    private sealed class FieldValidationIssue(string previousHelpText)
    {
        public string PreviousHelpText { get; } = previousHelpText;
        public AdornerLayer? Layer { get; set; }
        public ValidationOutline? Outline { get; set; }
    }

    private sealed class ValidationOutline : Adorner
    {
        private static readonly Pen OutlinePen = CreatePen();
        public ValidationOutline(UIElement field) : base(field) { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!AdornedElement.IsVisible) return;
            var size = AdornedElement.RenderSize;
            if (size.Width <= 3 || size.Height <= 3) return;
            drawingContext.DrawRoundedRectangle(null, OutlinePen, new Rect(1.5, 1.5, size.Width - 3, size.Height - 3), 5, 5);
        }
        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(218, 50, 66)), 2.5); pen.Freeze(); return pen;
        }
    }
}
