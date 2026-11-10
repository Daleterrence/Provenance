using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

// only a full pick counts, and an unknown code's kept until something's picked
public sealed partial class CascadePick : ObservableObject
{
    private readonly IReadOnlyList<MyProfileChoice> _rows;
    private readonly int _levels;
    private readonly string _notSet;
    private long _initial;
    private bool _touched, _setting;

    public CascadePick(IReadOnlyList<MyProfileChoice> rows, int levels, string notSet)
    {
        _rows = rows;
        _levels = levels;
        _notSet = notSet;
        Tops = [notSet, .. rows.Select(r => Part(r, 0)).Where(p => p.Length > 0).Distinct()];
    }

    public event Action? Changed;

    public IReadOnlyList<string> Tops { get; }
    public ObservableCollection<string> Mids { get; } = new();
    public ObservableCollection<Choice<long>> Leaves { get; } = new();
    public bool HasMid => _levels >= 3;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSet))] private string? _top;
    [ObservableProperty] private string? _mid;
    [ObservableProperty] private Choice<long>? _leaf;

    public bool IsSet => Top is { } t && t != _notSet;

    public long? Code => !_touched ? _initial : !IsSet ? 0 : Leaf?.Code;

    public void Set(long code)
    {
        _initial = code;
        _touched = false;
        _setting = true;
        try
        {
            var row = code == 0 ? null : _rows.FirstOrDefault(r => r.Code == code);
            Top = row is null ? _notSet : Part(row, 0);
            if (row != null && HasMid) Mid = Part(row, 1);
            Leaf = row is null ? null : Leaves.FirstOrDefault(l => l.Code == code);
        }
        finally { _setting = false; }
    }

    private static string Part(MyProfileChoice r, int i) => i < r.Parts.Count ? r.Parts[i].Trim() : "";

    partial void OnTopChanged(string? value)
    {
        Mids.Clear();
        Leaves.Clear();
        Mid = null;
        Leaf = null;
        if (IsSet && HasMid)
            foreach (var m in _rows.Where(r => Part(r, 0) == value).Select(r => Part(r, 1)).Distinct()) Mids.Add(m);
        else if (IsSet)
            FillLeaves(_rows.Where(r => Part(r, 0) == value), 1);
        Touch();
    }

    partial void OnMidChanged(string? value)
    {
        if (!HasMid) return;
        Leaves.Clear();
        Leaf = null;
        if (value != null) FillLeaves(_rows.Where(r => Part(r, 0) == Top && Part(r, 1) == value), 2);
        Touch();
    }

    partial void OnLeafChanged(Choice<long>? value) => Touch();

    private void FillLeaves(IEnumerable<MyProfileChoice> rows, int part)
    {
        foreach (var r in rows) Leaves.Add(new Choice<long>(r.Code, Part(r, part)));
        if (Leaves.Count == 1 && !_setting) Leaf = Leaves[0];   // nothing to choose between
    }

    private void Touch()
    {
        if (_setting) return;
        _touched = true;
        Changed?.Invoke();
    }
}
