using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Provenance.Wpf.Controls.Behaviours;

// MaterialDesign pads the top and bottom of a combo box's list with empty space
public static class TightPopup
{
    public static readonly DependencyProperty TemplateProperty = DependencyProperty.RegisterAttached(
        "Template", typeof(ControlTemplate), typeof(TightPopup), new PropertyMetadata(null, OnChanged));

    public static ControlTemplate? GetTemplate(DependencyObject d) => (ControlTemplate?)d.GetValue(TemplateProperty);
    public static void SetTemplate(DependencyObject d, ControlTemplate? value) => d.SetValue(TemplateProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.Loaded -= OnLoaded;
        if (e.NewValue is null) return;
        combo.Loaded += OnLoaded;
        if (combo.IsLoaded) Apply(combo);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((ComboBox)sender);

    private static void Apply(ComboBox combo)
    {
        if (GetTemplate(combo) is not { } template) return;
        combo.ApplyTemplate();
        if (combo.Template?.FindName("PART_Popup", combo) is not ComboBoxPopup popup) return;
        popup.ClassicContentTemplate = template;
        if (popup.Child is ContentControl content) content.Template = template;
    }
}
