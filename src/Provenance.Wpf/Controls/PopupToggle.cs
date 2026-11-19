using System.Windows;
using System.Windows.Controls.Primitives;

namespace Provenance.Wpf.Controls;

public class PopupToggle : ToggleButton
{
    protected override void OnClick() => OnToggle();

    protected override void OnToggle() => RaiseEvent(new RoutedEventArgs(ClickEvent, this));
}
