namespace Provenance.Wpf.ViewModels;

// how a session ends for its lines in flight: Play and log out (or exit) let the hook finish them, a drop doesn't
public enum SessionEnd { Play, LogOut, Dropped }
