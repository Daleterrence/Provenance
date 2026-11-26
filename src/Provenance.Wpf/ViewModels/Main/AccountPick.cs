using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Main;

// renamed in place, a replaced item would drop the log-in card's selection
public sealed partial class AccountPick(Account account, string name) : ObservableObject
{
    public Account Account { get; } = account;

    [ObservableProperty]
    private string _name = name;
}
