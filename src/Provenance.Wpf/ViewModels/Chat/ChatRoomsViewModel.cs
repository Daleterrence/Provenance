using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.ViewModels.Chat;

// one chat rooms window: the zone picker and room list, then the room. Everything goes through the hook's files, bar
// the room password, which goes as a one-shot Credential Manager entry
public sealed partial class ChatRoomsViewModel : ObservableObject, IDisposable
{
    public enum Page { Lobby, Room }

    public const string CouldNotGetName = "Could not retrieve chat name.";
    public const string GameRunningText = "Chat rooms have to wait till FINAL FANTASY XI is closed.";
    public const string LeaveQuestion = "Leave chat room?";
    public const string LeaveAsMaster =
        "Another member will be automatically designated room master if you leave the chat room now. Leave the room?";

    private readonly ISocialSession _social;
    private readonly IDialogs _dialogs;
    private readonly Func<string, string, bool> _savePassword;
    private readonly Action _dropPassword;
    private readonly ChatSendGate _gate = new(HookLimits.ChatLine);
    private DateTime Now => _social.Time.GetUtcNow().UtcDateTime;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _zonesToken, _roomsToken, _joinToken;
    private string _roomIrc = "", _roomTitle = "";
    private long _seenSeq, _lastSeq, _joinSeq;
    private string? _lastNick;
    private bool _restoringZone, _quiet, _disposed, _joinLocked;
    private readonly Dictionary<int, string> _categories = new();
    private readonly Func<int, ImageSource?>? _emote;
    private readonly HookJobWatch _master = new();
    private string? _masterName;
    private MasterJob _masterKind;
    private enum MasterJob { Kick, Master, Password }

    // savePassword hands a room password to the hook for one enter (token, password), dropPassword takes back one it
    // never read
    internal ChatRoomsViewModel(ISocialSession social, IDialogs dialogs, Func<string, string, bool> savePassword, Action dropPassword,
                              Func<int, ImageSource?>? emote = null)
    {
        _social = social;
        _dialogs = dialogs;
        _savePassword = savePassword;
        _dropPassword = dropPassword;
        _emote = emote;
        var grounds = ChatColours.Grounds();
        for (int i = 0; i < ChatLook.Colours; i++) ColourChoices.Add(new ChatColourItem(i, ChatColours.LineBrush(ChatLook.PickerArgb(i), grounds)));
        AppTheme.Changed += OnThemeChanged;
        for (int i = 1; i <= ChatLook.Emotes; i++)
            if (emote?.Invoke(i) is { } img) EmoteChoices.Add(new ChatEmoteItem(i, img));
        _social.SnapshotApplied += OnSnapshot;
        _social.PropertyChanged += OnSocialChanged;
        ZonesView = CollectionViewSource.GetDefaultView(Zones);
        ZonesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatZoneItem.Group)));
        _tick.Tick += (_, _) => SendTick(Now);
    }

    // Send's button only greys and ungreys when asked, so this keeps asking till a line can go again
    internal void SendTick(DateTime now)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (_gate.Expire(now) is { } done) FinishSend(done.State, done.Text);
        MasterTick(now);
        if (_gate.CanSend && !_master.Running) _tick.Stop();
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

    private const string DialogTitle = "PlayOnline Chat";

    private string? AskChatName(string initial) => _dialogs.Ask(DialogTitle, "Enter chat name.", initial,
                                                                ChatRoomFiles.MaxNameBytes, validate: ChatRoomFiles.NameProblem);

    private void ShowError(string text) => _dialogs.Error(DialogTitle, text);

    private bool Confirm(string question) => _dialogs.YesNo(DialogTitle, question);

    // the viewer asks for the new one and then again to confirm it
    private string? AskNewPassword()
    {
        var pw = _dialogs.AskSecret("Set password", "Enter password.", ChatRoomFiles.MaxNewPassword, ChatRoomFiles.NewPasswordProblem);
        if (pw is null) return null;
        var again = _dialogs.AskSecret("Confirm password", "Enter password.", ChatRoomFiles.MaxNewPassword);
        if (again is null) return null;
        if (again == pw) return pw;
        ShowError("The password entered in Room Password(Confirm) does not match.");
        return null;
    }

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

    private int? Pid => _social.SessionPid;

    // the zone list, the viewer's first search when chat opens and Try again's. Nothing else starts one
    [RelayCommand]
    public void Start()
    {
        if (_social.GameRunning) { Unavailable = GameRunningText; return; }
        if (Pid is not { } pid) { Unavailable = HookFailures.ChatRooms.NotNow; return; }
        Unavailable = null;
        _zonesToken = HookFiles.NewToken();
        ZonesNote = "Searching zones...";
        if (!ChatRoomFiles.Zones(pid, _zonesToken)) { _zonesToken = null; Unavailable = HookFailures.ChatRooms.NotNow; }
    }

    private void RequestRooms(int offset)
    {
        if (SelectedZone is not { } z || Pid is not { } pid) return;
        _roomsToken = HookFiles.NewToken();
        Searching = true;
        RoomsNote = "Searching chat rooms...";
        Rooms.Clear();
        PageText = "";
        if (!ChatRoomFiles.Rooms(pid, _roomsToken, z.Zone.Code, offset)) { Searching = false; RoomsNote = HookFailures.ChatRooms.NotNow; }
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
        var nick = AskChatName(_lastNick ?? DefaultNick());
        if (nick is null) return;
        var token = HookFiles.NewToken();
        if (room.Locked)
        {
            if (_dialogs.AskSecret("Enter room password", "Enter password.", ChatRoomFiles.MaxPasswordLength, ChatRoomFiles.PasswordProblem) is not { } pw) return;
            if (!_savePassword(token, pw))
            {
                ShowError(HookFailures.ChatCommError);
                return;
            }
        }
        BeginJoin(token, nick, room.Locked, room.Room.Irc, room.Name, "Entering chat room...",
                  () => ChatRoomFiles.Join(pid, token, room.Room.Irc, room.Locked, nick));
    }

    private void BeginJoin(string token, string nick, bool locked, string irc, string title, string busyText, Func<bool> write)
    {
        _lastNick = nick;
        _joinToken = token;
        _joinLocked = locked;
        _joinSeq = _lastSeq;
        _roomIrc = irc;
        _roomTitle = title;
        Entering = true;
        BusyText = busyText;
        if (write()) return;
        if (locked) _dropPassword();
        BackToLobby();
        ShowError(HookFailures.ChatCommError);
    }

    private bool CanCreate => Zones.Count > 0 && !Entering && !Searching && Current == Page.Lobby && !_social.HandleSwitching;

    // the viewer's Create Room: its form, the chat name, then the hook makes the room and I'm its master
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateRoom()
    {
        if (Pid is not { } pid) return;
        var cats = _categories.Select(c => new ChatCategory(c.Key, "", c.Value)).ToList();
        if (_dialogs.AskRoom(Zones.Select(z => z.Zone).ToList(), SelectedZone?.Zone.Code ?? 0, cats,
                             q => _dialogs.YesNo("Create Room", q)) is not { } form) return;
        var nick = AskChatName(_lastNick ?? DefaultNick());
        if (nick is null) return;
        var token = HookFiles.NewToken();
        bool locked = form.Password is { Length: > 0 };
        if (locked && !_savePassword(token, form.Password!))
        {
            ShowError(HookFailures.ChatCommError);
            return;
        }
        BeginJoin(token, nick, locked, "", form.Name.Trim(), "Creating chat room...",
                  () => ChatRoomFiles.Create(pid, token, locked, form.Zone, form.Occupancy, form.Members, form.Purpose, form.Language,
                                             nick, form.Name.Trim()));
    }

    // the create form's question, in the viewer's words (0x4eef/0x4ef0)
    public static string CreateQuestion(string zone, int occupancy, bool password, string members, string purpose, string language) =>
        $"Create chat room with these settings?\n\nZone: {zone}\nOccupancy: {occupancy}\nPassword: {(password ? "ON" : "Off")}\n" +
        $"Category 1: {members}\nCategory 2: {purpose}\nCategory 3: {language}";

    public bool CanMaster => Current == Page.Room && IsMaster && !MasterBusy && _social.WriteBlockedNote is null;

    public bool CanKick(ChatMemberItem? m) => m is { Me: false } && CanMaster;

    // a member's Kick and Designate, only for the room master and never on my own row
    public IReadOnlyList<MenuAction> MemberMenu(ChatMemberItem m) =>
        [new("Kick...", true, () => Kick(m)), new("Designate...", true, () => Designate(m))];

    public IReadOnlyList<MenuAction> PasswordMenu()
    {
        var menu = new List<MenuAction>
        {
            new(RoomLocked ? "Change password..." : "Set password...", true, () => ChangePassword(false)),
        };
        if (RoomLocked) menu.Add(new("Remove password", true, () => ChangePassword(true)));
        return menu;
    }

    // the viewer's Kick and Designate, each with its own question first
    public void Kick(ChatMemberItem m) =>
        MasterAction(m, $"Kick {m.Name}\nfrom this chat room?", ChatRoomFiles.MasterOp.Kick, MasterJob.Kick);

    public void Designate(ChatMemberItem m) =>
        MasterAction(m, $"Designate {m.Name} the new room master?", ChatRoomFiles.MasterOp.Master, MasterJob.Master);

    private void MasterAction(ChatMemberItem m, string question, ChatRoomFiles.MasterOp op, MasterJob job)
    {
        if (!CanKick(m)) { ShowError(HookFailures.ChatRooms.MasterOnly); return; }
        if (!Confirm(question)) return;
        SendMaster(op, job, m.Key, m.Name, null);
    }

    // a new password, or none to take it off
    public void ChangePassword(bool remove)
    {
        if (!CanMaster) { ShowError(_social.HandleSwitching ? SocialViewModel.SwitchingNote : HookFailures.ChatRooms.MasterOnly); return; }
        string? pw = null;
        if (!remove && (pw = AskNewPassword()) is null) return;
        if (!Confirm("Update chat room settings?")) return;
        SendMaster(ChatRoomFiles.MasterOp.Password, MasterJob.Password, 0, "", pw);
    }

    private void SendMaster(ChatRoomFiles.MasterOp op, MasterJob kind, int key, string name, string? password)
    {
        if (Pid is not { } pid) return;
        var token = HookFiles.NewToken();
        if (password is { Length: > 0 } && !_savePassword(token, password))
        {
            ShowError(HookFailures.ChatCommError);
            return;
        }
        if (!ChatRoomFiles.Master(pid, token, op, key, password is { Length: > 0 }))
        {
            if (password is { Length: > 0 }) _dropPassword();
            ShowError(HookFailures.ChatCommError);
            return;
        }
        _master.Begin(token, HookLimits.ChatMaster, Now);
        _masterKind = kind;
        _masterName = name;
        MasterBusy = true;
        _tick.Start();
    }

    internal void MasterTick(DateTime now)
    {
        if (!_master.Running || _master.Elapsed(now) < HookLimits.ChatMaster || Pid is not { } pid) return;
        HookFiles.Withdraw(pid, HookContract.Requests.ChatMaster);
        if (_masterKind == MasterJob.Password) _dropPassword();
        _master.Forget();
        MasterBusy = false;
        ShowError(HookFailures.ChatCommError);
    }

    private void ApplyMaster(ChatRoomsSnapshot chat)
    {
        if (!_master.Running || chat.MasterJob.Token != _master.Token || chat.MasterJob.State is "" or "working") return;
        _master.Forget();
        MasterBusy = false;
        var name = _masterName ?? "";
        if (chat.MasterJob.State == "done")
        {
            if (_masterKind == MasterJob.Kick) _dialogs.Info(DialogTitle, "Kicked.");
            else if (_masterKind == MasterJob.Master) _dialogs.Info(DialogTitle, "New room master designated.");
            return;
        }
        if (_masterKind == MasterJob.Password) _dropPassword();
        ShowError(chat.MasterJob.State switch
        {
            "failed:left" => HookFailures.ChatRooms.AlreadyLeft(name),
            "failed:not master" => HookFailures.ChatRooms.MasterOnly,
            "failed:unlocked" => HookFailures.ChatRooms.Unlocked,
            "failed:handle changing" => HookFailures.ChatRooms.MasterHandleChanging,
            "failed:busy" => HookFailures.Busy,
            _ => HookFailures.ChatCommError,
        });
    }

    // the handle's name, cut to what a chat name can hold
    private string DefaultNick()
    {
        var n = _social.AccountName.Trim();
        return ShiftJis.Fit(n, ChatRoomFiles.MaxNameBytes);
    }

    private bool CanExit => Current == Page.Room && !Entering;

    [RelayCommand(CanExecute = nameof(CanExit))]
    private void Exit()
    {
        bool othersHere = Members.Count(m => !m.Me) > 0, master = Members.Any(m => m.Me && m.Master);
        if (!_dialogs.Confirm(DialogTitle, master && othersHere ? LeaveAsMaster : LeaveQuestion, "LEAVE")) return;
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
            HookFiles.Withdraw(pid, HookContract.Requests.ChatSend);
            if (Current == Page.Room || Entering)
            {
                bool withdrawn = Entering && ((HookFiles.Withdraw(pid, HookContract.Requests.ChatJoin) == Withdrawal.Withdrawn) |
                                              (HookFiles.Withdraw(pid, HookContract.Requests.ChatCreate) == Withdrawal.Withdrawn));
                if (Entering) _dropPassword();
                if (!withdrawn && !_quiet) ChatRoomFiles.Leave(pid, HookFiles.NewToken(), Entering ? _joinToken ?? "" : "");
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
        _master.Forget();
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

    // the longest the hook allows is the room's name and mine away from full, so before there's a room it's the worst case
    public ComposeRules ComposeRules => _rules ??= new()
    {
        SingleLine = true,
        MaxChars = ChatRoomFiles.MaxTextLength,
        MaxBytes = () => ChatRoomFiles.MaxTextBytes(_roomIrc.Length > 0 ? _roomIrc : new string('#', MaxRoomIrc),
                                                    _lastNick ?? new string('W', ChatRoomFiles.MaxNameBytes)),
    };
    private ComposeRules? _rules;
    private const int MaxRoomIrc = 63;

    private bool CanSend => Current == Page.Room && Compose.Trim().Length > 0 && _gate.CanSend
                            && !_social.GameRunning && !_social.HandleSwitching;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Compose.Trim().Replace("\r", "").Replace('\n', ' ');
        if (text.Length == 0 || Pid is not { } pid) return;
        var token = HookFiles.NewToken();
        if (!ChatRoomFiles.Send(pid, token, text, Colour, Emoticon)) { SendNote = HookFailures.ChatCommError; return; }
        _gate.Sent(token, Compose, Now);
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
        if (e.PropertyName is nameof(ISocialSession.HandleSwitching) or nameof(ISocialSession.GameRunning))
            OnPropertyChanged(nameof(CanMaster));
        if (e.PropertyName == nameof(ISocialSession.HandleSwitching))
        {
            EnterCommand.NotifyCanExecuteChanged();
            CreateRoomCommand.NotifyCanExecuteChanged();
            SendCommand.NotifyCanExecuteChanged();
            SendNote = _social.HandleSwitching ? SocialViewModel.SwitchingNote : SendNote == SocialViewModel.SwitchingNote ? "" : SendNote;
            return;
        }
        if (e.PropertyName != nameof(ISocialSession.GameRunning)) return;
        if (_social.GameRunning)
        {
            Leave();
            Unavailable = GameRunningText;
        }
        else Unavailable = HookFailures.ChatRooms.NotNow;
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
        if (_zonesToken is null || chat.ZonesJob.Token != _zonesToken) return;
        if (chat.ZonesJob.State == "searching") return;
        _zonesToken = null;
        ZonesNote = "";
        if (chat.ZonesJob.State != "done")
        {
            Unavailable = HookFailures.ChatRooms.NotNow;
            return;
        }
        var keep = SelectedZone?.Zone.Code;
        _restoringZone = true;
        try
        {
            Zones.Clear();
            foreach (var z in chat.Zones) Zones.Add(new ChatZoneItem(z with { Name = _social.Filtered(z.Name) }));
            if (Zones.Count == 0) { Unavailable = HookFailures.ChatRooms.NotNow; return; }
            ZonesNote = "Select zone.";
            CreateRoomCommand.NotifyCanExecuteChanged();
            SelectedZone = keep is { } k ? Zones.FirstOrDefault(x => x.Zone.Code == k) : null;
        }
        finally { _restoringZone = false; }
    }

    private void ApplyRooms(ChatRoomsSnapshot chat)
    {
        if (_roomsToken is null || chat.RoomsJob.Token != _roomsToken || chat.RoomsJob.State == "searching") return;
        _roomsToken = null;
        Searching = false;
        Rooms.Clear();
        if (chat.RoomsJob.State == "failed:no chat") { Unavailable = HookFailures.ChatRooms.NotNow; return; }
        if (chat.RoomsJob.State != "done")
        {
            RoomsNote = HookFailures.ChatRooms.RoomListFailed(SocialRequests.FailReason(chat.RoomsJob.State));
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
        if (_joinToken is null || chat.RoomJob.Token != _joinToken) return;
        var title = _roomTitle;
        switch (chat.RoomJob.State)
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
                ShowError($"You have been kicked\nfrom {title}.");
                return;
            case "gone":
                BackToLobby();
                ShowError($"You're no longer in {title}.");
                return;
            case "lost":
                BackToLobby();
                Unavailable = HookFailures.ChatRooms.NotNow;
                ShowError("Communication error occurred during chat. Quitting PlayOnline Chat.");
                return;
            case "left":
                BackToLobby();
                return;
        }
        if (!chat.RoomJob.State.StartsWith("failed", StringComparison.Ordinal)) return;
        // the hook deletes the password entry once it knows there's one, this is in case it never got that far
        if (_joinLocked) _dropPassword();
        var why = chat.RoomJob.State switch
        {
            "failed:refused" => HookFailures.ChatRooms.Refused,
            "failed:password" => HookFailures.ChatRooms.WrongPassword,
            "failed:full" => HookFailures.ChatRooms.Full,
            "failed:closed" => HookFailures.ChatRooms.Closed,
            "failed:no chat" => HookFailures.ChatRooms.NotNow,
            "failed:handle changing" => HookFailures.ChatRooms.JoinHandleChanging,
            "failed:busy" => HookFailures.Busy,
            "failed:bad name" => HookFailures.ChatRooms.BadName,
            _ => HookFailures.ChatCommError,
        };
        BackToLobby();
        if (chat.RoomJob.State == "failed:no chat") Unavailable = HookFailures.ChatRooms.NotNow;
        ShowError(why);
    }

    private void ApplyLines(ChatRoomsSnapshot chat)
    {
        if (Current != Page.Room || chat.LineSeq <= _seenSeq || Pid is not { } pid) return;
        if (ChatRoomFiles.TryReadLines(pid) is not { } file || !string.Equals(file.Room, _roomIrc, StringComparison.OrdinalIgnoreCase)) return;
        var grounds = ChatColours.Grounds();
        foreach (var l in file.Lines.Where(x => x.Seq > _seenSeq).OrderBy(x => x.Seq))
        {
            var (text, style) = LineText(l);
            var line = new ChatLineItem(l.Seq, text, style, l.When);
            if (l.Kind == "say")
                line = line with
                {
                    Argb = ChatLook.Argb(l.Colour),
                    Colour = ChatLook.Argb(l.Colour) is { } c ? ChatColours.LineBrush(c, grounds) : null,
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
        var grounds = ChatColours.Grounds();
        for (int i = 0; i < ColourChoices.Count; i++)
            ColourChoices[i] = ColourChoices[i] with { Swatch = ChatColours.LineBrush(ChatLook.PickerArgb(ColourChoices[i].Pick), grounds) };
        for (int i = 0; i < Lines.Count; i++)
            if (Lines[i].Argb is { } argb) Lines[i] = Lines[i] with { Colour = ChatColours.LineBrush(argb, grounds) };
        OnPropertyChanged(nameof(PickedColour));
    }

    private void AddLine(long seq, string text, string style, DateTimeOffset? when = null) =>
        Lines.Add(new ChatLineItem(seq, text, style, when ?? DateTimeOffset.Now));

    private void ApplySend(ChatRoomsSnapshot chat)
    {
        if (_gate.Settle(chat.SendJob.Token, chat.SendJob.State, Now) is { } done) FinishSend(done.State, done.Text);
    }

    private void FinishSend(string done, string text)
    {
        if (done != "sent")
            SendNote = HookFailures.ForChatLine(done) ?? HookFailures.ChatCommError;
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
