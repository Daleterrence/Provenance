using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public sealed record ChatZoneItem(ChatZone Zone)
{
    public string Name => Zone.Name;
    public string Group => Zone.Group;
    public string Counts => $"Rooms: {Zone.Rooms}   Users: {Zone.People}";
}

public sealed record ChatRoomItem(ChatRoomEntry Room, string Name, string Categories)
{
    public string People => $"{Room.People}/{Room.Capacity}";
    public bool Locked => Room.Locked;
    public bool HasCategories => Categories.Length > 0;
}

// Style is say, notice (arrivals, leaving, master changes), status (away and back), whisper or mine
public sealed record ChatLineItem(long Seq, string Text, string Style, DateTimeOffset When)
{
    public string TimeText => When.ToString("HH:mm");
}

public sealed record ChatMemberItem(string Name, bool Me, bool Master, bool Away)
{
    public string Note => Away ? "Away" : Me ? "You" : "";
}

// one chat rooms window: the zone picker and room list, then the room. Everything goes through the hook's files, bar
// the room password, which goes as a one-shot Credential Manager entry
public sealed partial class ChatRoomsViewModel : ObservableObject, IDisposable
{
    public enum Page { Lobby, Room }

    public const string CouldNotGetName = "Could not retrieve chat name.";
    public const string NotNow = "Chat rooms aren't available right now.";
    public const string GameRunningText = "Chat rooms have to wait till FINAL FANTASY XI is closed.";
    public const string LeaveQuestion = "Leave chat room?";
    public const string LeaveAsMaster =
        "Another member will be automatically designated room master if you leave the chat room now. Leave the room?";

    private readonly SocialViewModel _social;
    private readonly Func<string, string, bool> _savePassword;
    private readonly Action _dropPassword;
    private readonly ChatSendGate _gate = new();
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _zonesToken, _roomsToken, _joinToken;
    private string _roomIrc = "", _roomTitle = "";
    private long _seenSeq, _lastSeq, _joinSeq;
    private string? _lastNick;
    private bool _restoringZone, _quiet, _disposed, _joinLocked;
    private readonly Dictionary<int, string> _categories = new();

    // savePassword hands a room password to the hook for one enter (token, password), dropPassword takes back one it
    // never read
    public ChatRoomsViewModel(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword)
    {
        _social = social;
        _savePassword = savePassword;
        _dropPassword = dropPassword;
        _social.SnapshotApplied += OnSnapshot;
        _social.PropertyChanged += OnSocialChanged;
        ZonesView = CollectionViewSource.GetDefaultView(Zones);
        ZonesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatZoneItem.Group)));
        _tick.Tick += (_, _) => SendTick();
    }

    // Send's button only greys and ungreys when asked, so this keeps asking till a line can go again
    internal void SendTick()
    {
        SendCommand.NotifyCanExecuteChanged();
        if (_gate.Token is null && _gate.CanSend(DateTime.UtcNow)) _tick.Stop();
    }

    internal bool SendTicking => _tick.IsEnabled;

    public ObservableCollection<ChatZoneItem> Zones { get; } = new();
    public ICollectionView ZonesView { get; }
    public ObservableCollection<ChatRoomItem> Rooms { get; } = new();
    public ObservableCollection<ChatLineItem> Lines { get; } = new();
    public ObservableCollection<ChatMemberItem> Members { get; } = new();

    // the view's hooks for the questions only it can ask
    public Func<string, string?>? AskChatName { get; set; }
    public Func<string?>? AskPassword { get; set; }
    public Func<string, bool>? ConfirmLeave { get; set; }
    public Action<string>? ShowError { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(InLobby), nameof(InRoom))] private Page _current = Page.Lobby;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Available), nameof(CanRetry))] private string? _unavailable;
    [ObservableProperty] private string _zonesNote = "";
    [ObservableProperty] private string _roomsNote = "";
    [ObservableProperty] private string _pageText = "";
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _roomName = "";
    [ObservableProperty] private string _roomCount = "";
    [ObservableProperty] private string _sendNote = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _compose = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(EnterCommand))]
    private bool _searching;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(EnterCommand))]
    private ChatZoneItem? _selectedZone;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EnterCommand))] private ChatRoomItem? _selectedRoom;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))] private int _offset;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextPageCommand))] private int _total;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(EnterCommand), nameof(ExitCommand), nameof(CancelEnterCommand))]
    private bool _entering;

    public bool Available => Unavailable is null;
    public bool CanRetry => Unavailable is not null && Unavailable != GameRunningText;
    public bool InLobby => Current == Page.Lobby;
    public bool InRoom => Current == Page.Room;
    public bool Busy => BusyText.Length > 0;

    partial void OnBusyTextChanged(string value) => OnPropertyChanged(nameof(Busy));

    // a pick is a click, putting the same zone back after a reload isn't
    partial void OnSelectedZoneChanged(ChatZoneItem? value)
    {
        if (value != null && Available && !_restoringZone) RequestRooms(0);
    }

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    private int? Pid => _social.SessionPid;

    // the zone list, the viewer's first search when chat opens and TRY AGAIN's. Nothing else starts one
    [RelayCommand]
    public void Start()
    {
        if (_social.GameRunning) { Unavailable = GameRunningText; return; }
        if (Pid is not { } pid) { Unavailable = NotNow; return; }
        Unavailable = null;
        _zonesToken = NewToken();
        ZonesNote = "Searching zones...";
        if (!ChatRoomFiles.Zones(pid, _zonesToken)) { _zonesToken = null; Unavailable = NotNow; }
    }

    private void RequestRooms(int offset)
    {
        if (SelectedZone is not { } z || Pid is not { } pid) return;
        _roomsToken = NewToken();
        Searching = true;
        RoomsNote = "Searching chat rooms...";
        Rooms.Clear();
        PageText = "";
        if (!ChatRoomFiles.Rooms(pid, _roomsToken, z.Zone.Code, offset)) { Searching = false; RoomsNote = NotNow; }
    }

    private bool CanList => SelectedZone != null && !Searching && !Entering;

    [RelayCommand(CanExecute = nameof(CanList))]
    private void UpdateList() => RequestRooms(Offset);

    private bool CanNext => CanList && Offset + ChatRoomFiles.PageSize < Total;
    private bool CanPrevious => CanList && Offset > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() => RequestRooms(Offset + ChatRoomFiles.PageSize);

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() => RequestRooms(Math.Max(0, Offset - ChatRoomFiles.PageSize));

    private bool CanEnter => SelectedRoom != null && !Entering && !Searching && Current == Page.Lobby;

    [RelayCommand(CanExecute = nameof(CanEnter))]
    private void Enter()
    {
        if (SelectedRoom is not { } room || Pid is not { } pid) return;
        var nick = AskChatName?.Invoke(_lastNick ?? DefaultNick());
        if (nick is null) return;
        var token = NewToken();
        if (room.Locked)
        {
            if (AskPassword?.Invoke() is not { } pw) return;
            if (!_savePassword(token, pw))
            {
                ShowError?.Invoke("Chat communication error.");
                return;
            }
        }
        _lastNick = nick;
        _joinToken = token;
        _joinLocked = room.Locked;
        _joinSeq = _lastSeq;
        _roomIrc = room.Room.Irc;
        _roomTitle = room.Name;
        Entering = true;
        BusyText = "Entering chat room...";
        if (!ChatRoomFiles.Join(pid, token, room.Room.Irc, room.Locked, nick))
        {
            if (room.Locked) _dropPassword();
            BackToLobby();
            ShowError?.Invoke("Chat communication error.");
        }
    }

    // the handle's name, cut to what a chat name can hold
    private string DefaultNick()
    {
        var n = _social.AccountName.Trim();
        return PolMessage.FitBytes(n, ChatRoomFiles.MaxNameBytes);
    }

    private bool CanExit => Current == Page.Room && !Entering;

    [RelayCommand(CanExecute = nameof(CanExit))]
    private void Exit()
    {
        bool othersHere = Members.Count(m => !m.Me) > 0, master = Members.Any(m => m.Me && m.Master);
        if (ConfirmLeave?.Invoke(master && othersHere ? LeaveAsMaster : LeaveQuestion) == false) return;
        Leave();
    }

    [RelayCommand(CanExecute = nameof(Entering))]
    private void CancelEnter()
    {
        Leave();
        RoomsNote = "Room entry cancelled.";
    }

    // Exit, Cancel, the game starting and closing the window. A send not taken yet never goes, and an enter not taken
    // yet is withdrawn, or called off by the leave when it couldn't be
    public void Leave()
    {
        if (Pid is { } pid)
        {
            SocialSnapshot.WithdrawRequest(pid, "chatsend");
            if (Current == Page.Room || Entering)
            {
                bool withdrawn = Entering && SocialSnapshot.WithdrawRequest(pid, "chatjoin") == SocialSnapshot.Withdrawal.Withdrawn;
                if (Entering) _dropPassword();
                if (!withdrawn && !_quiet) ChatRoomFiles.Leave(pid, NewToken(), Entering ? _joinToken ?? "" : "");
            }
        }
        BackToLobby();
    }

    // the session's ending anyway (log out, the game closing), which drops the room without a PART
    public void CloseQuietly()
    {
        _quiet = true;
        Dispose();
    }

    private void BackToLobby()
    {
        Current = Page.Lobby;
        Entering = false;
        BusyText = "";
        _joinToken = null;
        _gate.Reset();
        _tick.Stop();
        SendNote = "";
        Members.Clear();
        Lines.Clear();
        _roomIrc = "";
        SendCommand.NotifyCanExecuteChanged();
        ExitCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend => Current == Page.Room && Compose.Trim().Length > 0 && _gate.CanSend(DateTime.UtcNow)
                            && !_social.GameRunning;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Compose.Trim().Replace("\r", "").Replace('\n', ' ');
        if (text.Length == 0 || Pid is not { } pid) return;
        if (PolMessage.TextBytes(text) > ChatRoomFiles.MaxTextBytes)
        {
            SendNote = "That's too long for one line.";
            return;
        }
        var token = NewToken();
        if (!ChatRoomFiles.Send(pid, token, text)) { SendNote = "Chat communication error."; return; }
        _gate.Sent(token, DateTime.UtcNow);
        Compose = "";
        SendNote = "";
        _tick.Start();
        SendCommand.NotifyCanExecuteChanged();
    }

    // the game starting leaves the room straight away, the hook might never see the Play. TRY AGAIN once it's closed
    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SocialViewModel.GameRunning)) return;
        if (_social.GameRunning)
        {
            Leave();
            Unavailable = GameRunningText;
        }
        else Unavailable = NotNow;
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        if (snap.Chat is not { } chat) return;
        ApplyZones(chat);
        ApplyRooms(chat);
        ApplyRoom(chat);
        ApplyLines(chat);
        ApplySend(chat);
        _lastSeq = Math.Max(_lastSeq, chat.LineSeq);
    }

    private void ApplyZones(ChatRoomsSnapshot chat)
    {
        if (_zonesToken is null || chat.ZonesToken != _zonesToken) return;
        if (chat.ZonesState == "searching") return;
        _zonesToken = null;
        ZonesNote = "";
        if (chat.ZonesState != "done")
        {
            Unavailable = NotNow;
            return;
        }
        _categories.Clear();
        foreach (var c in chat.Categories) _categories[c.Code] = c.Name;
        var keep = SelectedZone?.Zone.Code;
        _restoringZone = true;
        try
        {
            Zones.Clear();
            foreach (var z in chat.Zones) Zones.Add(new ChatZoneItem(z with { Name = _social.Filtered(z.Name) }));
            if (Zones.Count == 0) { Unavailable = NotNow; return; }
            ZonesNote = "Select zone.";
            SelectedZone = keep is { } k ? Zones.FirstOrDefault(x => x.Zone.Code == k) : null;
        }
        finally { _restoringZone = false; }
    }

    private void ApplyRooms(ChatRoomsSnapshot chat)
    {
        if (_roomsToken is null || chat.RoomsToken != _roomsToken || chat.RoomsState == "searching") return;
        _roomsToken = null;
        Searching = false;
        Rooms.Clear();
        if (chat.RoomsState == "failed:no chat") { Unavailable = NotNow; return; }
        if (chat.RoomsState != "done")
        {
            RoomsNote = $"The room list didn't load ({SocialViewModel.FailReason(chat.RoomsState)}).";
            return;
        }
        Offset = chat.Offset;
        Total = chat.Total;
        foreach (var r in chat.Rooms) Rooms.Add(new ChatRoomItem(r, _social.Filtered(r.Name), CategoryText(r.Categories)));
        RoomsNote = Total == 0 ? "No chat rooms found in this zone." : $"Rooms: {Total}";
        int pages = (Total + ChatRoomFiles.PageSize - 1) / ChatRoomFiles.PageSize;
        PageText = pages > 1 ? $"Page {Offset / ChatRoomFiles.PageSize + 1}/{pages}" : "";
    }

    internal string CategoryText(IReadOnlyList<int> codes) =>
        string.Join(", ", codes.Where(c => c != 0).Select(c => _categories.TryGetValue(c, out var n) ? n : "").Where(n => n.Length > 0));

    private void ApplyRoom(ChatRoomsSnapshot chat)
    {
        if (_joinToken is null || chat.RoomToken != _joinToken) return;
        var title = _roomTitle;
        switch (chat.RoomState)
        {
            case "entering":
                return;
            case "in":
                if (Current != Page.Room)
                {
                    Current = Page.Room;
                    Entering = false;
                    BusyText = "";
                    Lines.Clear();
                    // the hook keeps the room's lines from the JOIN's answer, before it says "in"
                    _seenSeq = _joinSeq;
                    AddLine(0, $"Entered\n{title}.", "notice");
                    ExitCommand.NotifyCanExecuteChanged();
                    SendCommand.NotifyCanExecuteChanged();
                }
                RoomName = title;
                var now = chat.Members.OrderByDescending(x => x.Master).ThenByDescending(x => x.Me)
                    .Select(m => new ChatMemberItem(m.Name.Length > 0 ? (m.Me ? m.Name : _social.Filtered(m.Name)) : CouldNotGetName,
                                                    m.Me, m.Master, m.Away)).ToList();
                if (!Members.SequenceEqual(now))
                {
                    Members.Clear();
                    foreach (var m in now) Members.Add(m);
                }
                RoomCount = chat.Limit > 0 ? $"{Members.Count}/{chat.Limit}" : $"{Members.Count}";
                return;
            // out of the room before the dialog, which runs its own loop where the next snapshot lands
            case "kicked":
                BackToLobby();
                ShowError?.Invoke($"You have been kicked\nfrom {title}.");
                return;
            case "gone":
                BackToLobby();
                ShowError?.Invoke($"You're no longer in {title}.");
                return;
            case "lost":
                BackToLobby();
                Unavailable = NotNow;
                ShowError?.Invoke("Communication error occurred during chat. Quitting PlayOnline Chat.");
                return;
            case "left":
                BackToLobby();
                return;
        }
        if (!chat.RoomState.StartsWith("failed", StringComparison.Ordinal)) return;
        // the hook deletes the password entry once it knows there's one, this is in case it never got that far
        if (_joinLocked) _dropPassword();
        var why = chat.RoomState switch
        {
            "failed:refused" => "Room entry refused.",
            "failed:password" => "Wrong password.",
            "failed:full" => "Selected chat room is currently full and cannot be entered.",
            "failed:closed" => "This room is already closed and cannot be entered.",
            "failed:no chat" => NotNow,
            _ => "Chat communication error.",
        };
        BackToLobby();
        if (chat.RoomState == "failed:no chat") Unavailable = NotNow;
        ShowError?.Invoke(why);
    }

    private void ApplyLines(ChatRoomsSnapshot chat)
    {
        if (Current != Page.Room || chat.LineSeq <= _seenSeq || Pid is not { } pid) return;
        if (ChatRoomFiles.TryReadLines(pid) is not { } file || !string.Equals(file.Room, _roomIrc, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var l in file.Lines.Where(x => x.Seq > _seenSeq).OrderBy(x => x.Seq))
        {
            var (text, style) = LineText(l);
            AddLine(l.Seq, text, style, l.When);
            _seenSeq = l.Seq;
        }
        while (Lines.Count > 500) Lines.RemoveAt(0);
    }

    // the viewer's own words for every line, and its one stand-in when a name never came through
    internal (string Text, string Style) LineText(ChatLine l)
    {
        string name = l.Me ? l.Name : _social.Filtered(l.Name), text = l.Me ? l.Text : _social.Filtered(l.Text);
        if (name.Length == 0) return (CouldNotGetName, l.Kind == "say" ? "say" : "notice");
        return l.Kind switch
        {
            "say" => ($"{name}> {text}", l.Me ? "mine" : "say"),
            "entered" => ($"{name} has entered the room!", "notice"),
            "left" => ($"{name} has left the room.", "notice"),
            "away" => (text.Length == 0 ? $"{name} is away.\n\"Cannot respond immediately.\"" : $"{name} is away.\n\"{text}\"", "status"),
            "back" => ($"{name} is back.", "status"),
            "master" => ($"{name} has been designated room master.", "notice"),
            "kicked" => ($"{name} was kicked by the room master.", "notice"),
            "whisper" => ($"Whisper from {name}.\n\"{text}\"", "whisper"),
            _ => ($"{name}> {text}", "say"),
        };
    }

    private void AddLine(long seq, string text, string style, DateTimeOffset? when = null) =>
        Lines.Add(new ChatLineItem(seq, text, style, when ?? DateTimeOffset.Now));

    private void ApplySend(ChatRoomsSnapshot chat)
    {
        var done = _gate.Settle(chat.SendToken, chat.SendState, DateTime.UtcNow);
        if (done is null) return;
        if (done != "sent")
            SendNote = done switch
            {
                "failed:too long" => "That's too long for one line.",
                "failed:timeout" => "PlayOnline didn't answer, so that line may not have gone.",
                _ => "Chat communication error.",
            };
        SendCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _social.SnapshotApplied -= OnSnapshot;
        _social.PropertyChanged -= OnSocialChanged;
        _tick.Stop();
        Leave();
    }
}
