using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.Controls.Behaviours;

// Enter sends, and what's typed or pasted stops at the rules' limit (a paste goes in up to what fits)
public static class ComposeBox
{
    public static readonly DependencyProperty SendProperty = DependencyProperty.RegisterAttached(
        "Send", typeof(ICommand), typeof(ComposeBox), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RulesProperty = DependencyProperty.RegisterAttached(
        "Rules", typeof(ComposeRules), typeof(ComposeBox), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetSend(DependencyObject d) => (ICommand?)d.GetValue(SendProperty);
    public static void SetSend(DependencyObject d, ICommand? value) => d.SetValue(SendProperty, value);
    public static ComposeRules? GetRules(DependencyObject d) => (ComposeRules?)d.GetValue(RulesProperty);
    public static void SetRules(DependencyObject d, ComposeRules? value) => d.SetValue(RulesProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewKeyDown -= OnKey;
        box.PreviewTextInput -= OnInput;
        DataObject.RemovePastingHandler(box, OnPaste);
        box.PreviewKeyDown += OnKey;
        box.PreviewTextInput += OnInput;
        DataObject.AddPastingHandler(box, OnPaste);
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        if (GetRules(box) is { SingleLine: false } && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (GetSend(box) is { } send && send.CanExecute(null)) send.Execute(null);
    }

    private static string WithoutSelection(TextBox box) => box.Text.Remove(box.SelectionStart, box.SelectionLength);

    private static void OnInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box || GetRules(box) is not { } rules) return;
        var kept = rules.Filtered(e.Text);
        var take = rules.Fit(WithoutSelection(box), kept);
        if (take == e.Text) return;
        e.Handled = true;
        if (take.Length > 0 || kept != e.Text) Put(box, take);
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || GetRules(box) is not { } rules ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var take = rules.Fit(WithoutSelection(box), rules.Filtered(rules.SingleLine ? ComposeRules.Flatten(text) : text));
        if (take == text) return;
        e.CancelCommand();
        Put(box, take);
    }

    private static void Put(TextBox box, string text)
    {
        box.SelectedText = text;
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }
}
