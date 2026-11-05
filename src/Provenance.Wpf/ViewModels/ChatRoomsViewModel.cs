using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels;

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
    public const string MasterOnly = "This command is for the room master only.";
    public const string UnlockedText = "The old password was removed, but the new one couldn't be set. The room has no password now.";

    private readonly SocialViewModel _social;
    private readonly Func<string, string, bool> _savePassword;
    private readonly Action _dropPassword;
    // a little past the hook's 6 minute wait for a line's answer
    internal static readonly TimeSpan SendGiveUp = TimeSpan.FromSeconds(365);
    private readonly ChatSendGate _gate = new(SendGiveUp);
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _zonesToken, _roomsToken, _joinToken;
    private string _roomIrc = "", _roomTitle = "";
    private long _seenSeq, _lastSeq, _joinSeq;
    private string? _lastNick;
    private bool _restoringZone, _quiet, _disposed, _joinLocked;
    private readonly Dictionary<int, string> _categories = new();
    private readonly Func<int, ImageSource?>? _emote;
    private string? _masterToken, _masterName;
    private MasterJob _masterKind;
    private enum MasterJob { Kick, Master, Password }

    // savePassword hands a room password to the hook for one enter (token, password), dropPassword takes back one it
    // never read
    public ChatRoomsViewModel(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                              Func<int, ImageSource?>? emote = null)
    {
        _social = social;
        _savePassword = savePassword;
        _dropPassword = dropPassword;
        _emote = emote;
        for (int i = 0; i < ChatLook.Colours; i++) ColourChoices.Add(new ChatColourItem(i, LineBrush(ChatLook.PickerArgb(i))));
        AppTheme.Changed += OnThemeChanged;
        for (int i = 1; i <= ChatLook.Emotes; i++)
            if (emote?.Invoke(i) is { } img) EmoteChoices.Add(new ChatEmoteItem(i, img));
        _social.SnapshotApplied += OnSnapshot;
        _social.PropertyChanged += OnSocialChanged;
        ZonesView = CollectionViewSource.GetDefaultView(Zones);
        ZonesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatZoneItem.Group)));
        _tick.Tick += (_, _) => SendTick(DateTime.UtcNow);
    }

    // Send's button only greys and ungreys when asked, so this keeps asking till a line can go again
    internal void SendTick(DateTime now)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (_gate.Expire(now) is { } done) Settled(done.State, done.Text);
        MasterTick(now);
        if (_gate.CanSend && _masterToken is null) _tick.Stop();
    }

    internal bool SendTicking => _tick.IsEnabled;

    public ObservableCollection<ChatZoneItem> Zones { get; } = new();
    public ICollectionView ZonesView { get; }
    public ObservableCollection<ChatRoomItem> Rooms { get; } = new();
    public ObservableCollection<ChatLineItem> Lines { get; } = new();
    public ObservableCollection<ChatMemberItem> Members { get; } = new();
    public ObservableCollection<ChatColourItem> ColourChoices { get; } = new();
    public ObservableCollection<ChatEmoteItem> EmoteChoices { get; } = new();
    public bool HasEmotes => EmoteChoices.Count > 0;

    // the next line's colour (-1 none) and emoticon (0 none), back to none once it's sent like the viewer's
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PickedColour))] private int _colour = -1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PickedEmote))] private int _emoticon;
    public Brush? PickedColour => Colour < 0 ? null : ColourChoices[Colour].Swatch;
    public ImageSource? PickedEmote => Emoticon > 0 ? _emote?.Invoke(Emoticon) : null;

    [RelayCommand] private void PickColour(object? pick) => Colour = pick is int i ? i : -1;
    [RelayCommand] private void PickEmote(object? pick) => Emoticon = pick is int i ? i : 0;

    // the view's hooks for the questions only it can ask
    public Func<string, string?>? AskChatName { get; set; }
    public Func<string?>? AskPassword { get; set; }
    public Func<string, bool>? ConfirmLeave { get; set; }
    public Action<string>? ShowError { get; set; }
    public Action<string>? ShowInfo { get; set; }
    // null when the form's cancelled
    public Func<IReadOnlyList<ChatZone>, int, IReadOnlyList<ChatCategory>, ChatRoomForm?>? AskCreate { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Func<string?>? AskNewPassword { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(InLobby), nameof(InRoom), nameof(CanMaster)), NotifyCanExecuteChangedFor(nameof(EnterCommand), nameof(ExitCommand), nameof(CreateRoomCommand))]
    private Page _current = Page.Lobby;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Available), nameof(CanRetry))] private string? _unavailable;
    [ObservableProperty] private string _zonesNote = "";
    [ObservableProperty] private string _roomsNote = "";
    [ObservableProperty] private string _pageText = "";
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _roomName = "";
    [ObservableProperty] private string _roomCount = "";
    [ObservableProperty] private string _sendNote = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _compose = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(EnterCommand), nameof(CreateRoomCommand))]
    private bool _searching;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(EnterCommand))]
    private ChatZoneItem? _selectedZone;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EnterCommand))] private ChatRoomItem? _selectedRoom;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))] private int _offset;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextPageCommand))] private int _total;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateListCommand), nameof(EnterCommand), nameof(ExitCommand), nameof(CancelEnterCommand), nameof(CreateRoomCommand))]
    private bool _entering;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMaster))] private bool _isMaster;
    [ObservableProperty] private bool _roomLocked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMaster))] private bool _masterBusy;

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

    // the zone list, the viewer's first search when chat opens and Try again's. Nothing else starts one
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

    private bool CanEnter => SelectedRoom != null && !Entering && !Searching && Current == Page.Lobby && !_social.HandleSwitching;

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

    private bool CanCreate => Zones.Count > 0 && !Entering && !Searching && Current == Page.Lobby && !_social.HandleSwitching;

    // the viewer's Create Room: its form, the chat name, then the hook makes the room and I'm its master
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateRoom()
    {
        if (Pid is not { } pid) return;
        var cats = _categories.Select(c => new ChatCategory(c.Key, "", c.Value)).ToList();
        if (AskCreate?.Invoke(Zones.Select(z => z.Zone).ToList(), SelectedZone?.Zone.Code ?? 0, cats) is not { } form) return;
        var nick = AskChatName?.Invoke(_lastNick ?? DefaultNick());
        if (nick is null) return;
        var token = NewToken();
        bool locked = form.Password is { Length: > 0 };
        if (locked && !_savePassword(token, form.Password!))
        {
            ShowError?.Invoke("Chat communication error.");
            return;
        }
        _lastNick = nick;
        _joinToken = token;
        _joinLocked = locked;
        _joinSeq = _lastSeq;
        _roomIrc = "";
        _roomTitle = form.Name.Trim();
        Entering = true;
        BusyText = "Creating chat room...";
        if (!ChatRoomFiles.Create(pid, token, locked, form.Zone, form.Occupancy, form.Members, form.Purpose, form.Language, nick,
                                  form.Name.Trim()))
        {
            if (locked) _dropPassword();
            BackToLobby();
            ShowError?.Invoke("Chat communication error.");
        }
    }

    // the create form's question, in the viewer's words (0x4eef/0x4ef0)
    public static string CreateQuestion(string zone, int occupancy, bool password, string members, string purpose, string language) =>
        $"Create chat room with these settings?\n\nZone: {zone}\nOccupancy: {occupancy}\nPassword: {(password ? "ON" : "Off")}\n" +
        $"Category 1: {members}\nCategory 2: {purpose}\nCategory 3: {language}";

    public bool CanMaster => Current == Page.Room && IsMaster && !MasterBusy && !_social.HandleSwitching && !_social.GameRunning;

    public bool CanKick(ChatMemberItem? m) => m is { Me: false } && CanMaster;

    // the viewer's Kick and Designate, each with its own question first
    public void Kick(ChatMemberItem m)
    {
        if (!CanKick(m)) { ShowError?.Invoke(MasterOnly); return; }
        if (Confirm?.Invoke($"Kick {m.Name}\nfrom this chat room?") != true) return;
        SendMaster(ChatRoomFiles.MasterOp.Kick, MasterJob.Kick, m.Key, m.Name, null);
    }

    public void Designate(ChatMemberItem m)
    {
        if (!CanKick(m)) { ShowError?.Invoke(MasterOnly); return; }
        if (Confirm?.Invoke($"Designate {m.Name} the new room master?") != true) return;
        SendMaster(ChatRoomFiles.MasterOp.Master, MasterJob.Master, m.Key, m.Name, null);
    }

    // a new password, or none to take it off
    public void ChangePassword(bool remove)
    {
        if (!CanMaster) { ShowError?.Invoke(_social.HandleSwitching ? SocialViewModel.SwitchingNote : MasterOnly); return; }
        string? pw = null;
        if (!remove && (pw = AskNewPassword?.Invoke()) is null) return;
        if (Confirm?.Invoke("Update chat room settings?") != true) return;
        SendMaster(ChatRoomFiles.MasterOp.Password, MasterJob.Password, 0, "", pw);
    }

    private void SendMaster(ChatRoomFiles.MasterOp op, MasterJob kind, int key, string name, string? password)
    {
        if (Pid is not { } pid) return;
        var token = NewToken();
        if (password is { Length: > 0 } && !_savePassword(token, password))
        {
            ShowError?.Invoke("Chat communication error.");
            return;
        }
        if (!ChatRoomFiles.Master(pid, token, op, key, password is { Length: > 0 }))
        {
            if (password is { Length: > 0 }) _dropPassword();
            ShowError?.Invoke("Chat communication error.");
            return;
        }
        _masterToken = token;
        _masterKind = kind;
        _masterName = name;
        _masterSentAt = DateTime.UtcNow;
        MasterBusy = true;
        _tick.Start();
    }

    private DateTime _masterSentAt;
    // a little past the hook's own 6 minutes, for a job it never took or never answered
    internal static readonly TimeSpan MasterGiveUp = TimeSpan.FromSeconds(370);

    internal void MasterTick(DateTime now)
    {
        if (_masterToken is null || now - _masterSentAt < MasterGiveUp || Pid is not { } pid) return;
        SocialRequests.WithdrawRequest(pid, "chatmaster");
        if (_masterKind == MasterJob.Password) _dropPassword();
        _masterToken = null;
        MasterBusy = false;
        ShowError?.Invoke("Chat communication error.");
    }

    private void ApplyMaster(ChatRoomsSnapshot chat)
    {
        if (_masterToken is null || chat.MasterToken != _masterToken || chat.MasterState is "" or "working") return;
        _masterToken = null;
        MasterBusy = false;
        var name = _masterName ?? "";
        if (chat.MasterState == "done")
        {
            if (_masterKind == MasterJob.Kick) ShowInfo?.Invoke("Kicked.");
            else if (_masterKind == MasterJob.Master) ShowInfo?.Invoke("New room master designated.");
            return;
        }
        if (_masterKind == MasterJob.Password) _dropPassword();
        ShowError?.Invoke(chat.MasterState switch
        {
            "failed:left" => $"{name} has already left.",
            "failed:not master" => MasterOnly,
            "failed:unlocked" => UnlockedText,
            "failed:handle changing" => "Your handle was changing, so nothing was changed.",
            _ => "Chat communication error.",
        });
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
            SocialRequests.WithdrawRequest(pid, "chatsend");
            if (Current == Page.Room || Entering)
            {
                bool withdrawn = Entering && ((SocialRequests.WithdrawRequest(pid, "chatjoin") == Withdrawal.Withdrawn) |
                                              (SocialRequests.WithdrawRequest(pid, "chatcreate") == Withdrawal.Withdrawn));
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
        _masterToken = null;
        MasterBusy = false;
        IsMaster = false;
        OnPropertyChanged(nameof(CanMaster));
        Colour = -1;
        Emoticon = 0;
        Members.Clear();
        Lines.Clear();
        _roomIrc = "";
        SendCommand.NotifyCanExecuteChanged();
        ExitCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend => Current == Page.Room && Compose.Trim().Length > 0 && _gate.CanSend
                            && !_social.GameRunning && !_social.HandleSwitching;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Compose.Trim().Replace("\r", "").Replace('\n', ' ');
        if (text.Length == 0 || Pid is not { } pid) return;
        var token = NewToken();
        if (!ChatRoomFiles.Send(pid, token, text, Colour, Emoticon)) { SendNote = "Chat communication error."; return; }
        _gate.Sent(token, Compose, DateTime.UtcNow);
        Compose = "";
        Colour = -1;
        Emoticon = 0;
        SendNote = "";
        _tick.Start();
        SendCommand.NotifyCanExecuteChanged();
    }

    // the game starting leaves the room straight away, the hook might never see the Play. Try again once it's closed
    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SocialViewModel.HandleSwitching) or nameof(SocialViewModel.GameRunning))
            OnPropertyChanged(nameof(CanMaster));
        if (e.PropertyName == nameof(SocialViewModel.HandleSwitching))
        {
            EnterCommand.NotifyCanExecuteChanged();
            CreateRoomCommand.NotifyCanExecuteChanged();
            SendCommand.NotifyCanExecuteChanged();
            SendNote = _social.HandleSwitching ? SocialViewModel.SwitchingNote : SendNote == SocialViewModel.SwitchingNote ? "" : SendNote;
            return;
        }
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
        ApplyCategories(chat);
        ApplyZones(chat);
        ApplyRooms(chat);
        ApplyRoom(chat);
        ApplyLines(chat);
        ApplySend(chat);
        ApplyMaster(chat);
        _lastSeq = Math.Max(_lastSeq, chat.LineSeq);
    }

    // taken whenever they come, c_chan.pfb can fail to read with the zones and work with a room list
    private void ApplyCategories(ChatRoomsSnapshot chat)
    {
        if (chat.Categories.Count == 0) return;
        foreach (var c in chat.Categories) _categories[c.Code] = c.Name;
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
        var keep = SelectedZone?.Zone.Code;
        _restoringZone = true;
        try
        {
            Zones.Clear();
            foreach (var z in chat.Zones) Zones.Add(new ChatZoneItem(z with { Name = _social.Filtered(z.Name) }));
            if (Zones.Count == 0) { Unavailable = NotNow; return; }
            ZonesNote = "Select zone.";
            CreateRoomCommand.NotifyCanExecuteChanged();
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
            RoomsNote = $"The room list didn't load ({SocialRequests.FailReason(chat.RoomsState)}).";
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
                    if (_roomIrc.Length == 0) _roomIrc = chat.RoomIrc;
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
                // like the viewer, only members whose hello's given a name are listed or counted
                var now = chat.Members.Where(m => m.Me || m.Name.Length > 0)
                    .OrderByDescending(x => x.Master).ThenByDescending(x => x.Me)
                    .Select(m => new ChatMemberItem(m.Me ? m.Name : _social.Filtered(m.Name), m.Me, m.Master, m.Away, m.Key)).ToList();
                if (!Members.SequenceEqual(now))
                {
                    Members.Clear();
                    foreach (var m in now) Members.Add(m);
                }
                // an official room's master is the server's, never gives a name and has a place past the capacity
                // (app.dll 0x10088ecc)
                int limit = chat.Limit - (ChatLook.Official(chat.RoomIrc) ? 1 : 0);
                RoomCount = limit > 0 ? $"{Members.Count}/{limit}" : $"{Members.Count}";
                IsMaster = Members.Any(m => m.Me && m.Master);
                RoomLocked = chat.Locked;
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
            "failed:handle changing" => "Your handle was changing, so the room wasn't entered.",
            "failed:bad name" => "The entered room name cannot be used. Please enter a different name.",
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
            var line = new ChatLineItem(l.Seq, text, style, l.When);
            if (l.Kind == "say")
                line = line with
                {
                    Argb = ChatLook.Argb(l.Colour),
                    Colour = ChatLook.Argb(l.Colour) is { } c ? LineBrush(c) : null,
                    Emote = ChatLook.Emote(l.Emote) is > 0 and var e ? _emote?.Invoke(e) : null,
                };
            Lines.Add(line);
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

    private void OnThemeChanged()
    {
        for (int i = 0; i < ColourChoices.Count; i++)
            ColourChoices[i] = ColourChoices[i] with { Swatch = LineBrush(ChatLook.PickerArgb(ColourChoices[i].Pick)) };
        for (int i = 0; i < Lines.Count; i++)
            if (Lines[i].Argb is { } argb) Lines[i] = Lines[i] with { Colour = LineBrush(argb) };
        OnPropertyChanged(nameof(PickedColour));
    }

    internal static SolidColorBrush LineBrush(uint argb) => LineBrush(argb, ChatGrounds());

    // what the lines and the colour picker sit on, in whichever theme's showing
    internal static Color[] ChatGrounds() =>
        [.. new[] { "Bg.Base", "Surface.1", "Surface.2" }
            .Select(k => Application.Current?.TryFindResource(k) as SolidColorBrush).OfType<SolidColorBrush>().Select(b => b.Color)];

    // the viewer's colours are for its light window. On a dark one they're lifted to the same hue at a lightness that
    // reads, on a light one only the few too pale get darkened
    internal static SolidColorBrush LineBrush(uint argb, IReadOnlyList<Color> grounds)
    {
        bool light = grounds.Count > 0 && grounds.Average(c => (c.R + c.G + c.B) / 765.0) > 0.5;
        double r = ((argb >> 16) & 0xff) / 255.0, g = ((argb >> 8) & 0xff) / 255.0, b = (argb & 0xff) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max > min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        if (!light)
        {
            l = Math.Max(l, 0.68);
            s = Math.Min(1, s * 1.1);
        }
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        Color Rgb(double l)
        {
            byte C(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            if (s == 0) return Color.FromRgb(C(l), C(l), C(l));
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return Color.FromRgb(C(Hue(p, q, h + 1 / 3.0)), C(Hue(p, q, h)), C(Hue(p, q, h - 1 / 3.0)));
        }
        // HSL's lightness isn't what the eye sees (a yellow at 0.4 is still pale, a blue at 0.7 still dark), so it
        // carries on till it's AA on everything it sits on
        var colour = Rgb(l);
        double Worst(Color c) => grounds.Count == 0 ? 21 : grounds.Min(gr => Contrast(c, gr));
        if (light) while (Worst(colour) < 4.5 && l > 0) colour = Rgb(l -= 0.02);
        else while (Worst(colour) < 4.5 && l < 1) colour = Rgb(l += 0.02);
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    internal static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private void AddLine(long seq, string text, string style, DateTimeOffset? when = null) =>
        Lines.Add(new ChatLineItem(seq, text, style, when ?? DateTimeOffset.Now));

    private void ApplySend(ChatRoomsSnapshot chat)
    {
        if (_gate.Settle(chat.SendToken, chat.SendState, DateTime.UtcNow) is { } done) Settled(done.State, done.Text);
    }

    private void Settled(string done, string text)
    {
        if (done != "sent")
            SendNote = done switch
            {
                "failed:too long" => "That's too long for one line.",
                "failed:timeout" => "PlayOnline didn't answer, so that line may not have gone.",
                "failed:handle changing" => "Not sent: your handle was changing.",
                _ => "Chat communication error.",
            };
        // only the hook can tell whether it fits polcore's line, so one that doesn't goes back to be cut down
        if (done == "failed:too long" && Compose.Length == 0) Compose = text;
        SendCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _social.SnapshotApplied -= OnSnapshot;
        _social.PropertyChanged -= OnSocialChanged;
        AppTheme.Changed -= OnThemeChanged;
        _tick.Stop();
        Leave();
    }
}
