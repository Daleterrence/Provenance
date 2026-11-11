using System.Windows.Media;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Main;

public sealed record AccountRow(Account Account, string Name, ImageSource? Picture);
