using System.Windows;
using System.Windows.Controls.Primitives;

namespace Provenance.Wpf.Controls;

// a button that reads as on while its popup's open. IsChecked is bound to the popup, so a toggle is only ever a click
public class PopupToggle : ToggleButton
{
    protected override void OnClick() => OnToggle();

    // a screen reader's own press lands here, with no click before it
    protected override void OnToggle() => RaiseEvent(new RoutedEventArgs(ClickEvent, this));
}
