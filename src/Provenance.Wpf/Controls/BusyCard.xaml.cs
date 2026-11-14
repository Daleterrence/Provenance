using System.Windows;
using System.Windows.Controls;

namespace Provenance.Wpf.Controls;

public partial class BusyCard : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BusyCard), new PropertyMetadata(""));

    public BusyCard() => InitializeComponent();

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
