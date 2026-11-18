using System.Windows;
using Provenance.Common.Settings;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class PopOutWindow : AppWindow
{
    public PopOutWindow(PoppedConversation conversation)
    {
        Conversation = conversation;
        InitializeComponent();
        Title = conversation.Title;
        conversation.PropertyChanged += OnConversationChanged;
        var social = conversation.Messages.Social;
        social.MessageReceived += OnMessageLanded;
        IsVisibleChanged += (_, _) => ReportVisible();
        StateChanged += (_, _) => ReportVisible();
        Loaded += (_, _) =>
        {
            ReportVisible();
            if (ShowActivated) Pane.FocusCompose();
        };
#pragma warning disable RS0030 // a view, not a view model
        if (Application.Current is { } app) app.Activated += OnAppActivated;
#pragma warning restore RS0030
        // closing it is all that happens, the conversation just isn't in a window any more
        Closed += (_, _) =>
        {
            conversation.PropertyChanged -= OnConversationChanged;
            social.MessageReceived -= OnMessageLanded;
#pragma warning disable RS0030 // a view, not a view model
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
#pragma warning restore RS0030
            social.PopOutClosed(conversation);
        };
    }

    public PoppedConversation Conversation { get; }

    private void OnConversationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PoppedConversation.Title)) Title = Conversation.Title;
    }

    private void ReportVisible() =>
        Conversation.Messages.ConversationVisible = IsVisible && WindowState != WindowState.Minimized;

    // with Provenance in the background it flashes instead, till it's brought up
    private void OnMessageLanded(string who, string _)
    {
        if (!string.Equals(Conversation.Friend.Name, who, StringComparison.OrdinalIgnoreCase)) return;
        if (!Conversation.Messages.Social.Flashes(NoticeKind.Message)) return;
        if (!IsVisible || WindowState == WindowState.Minimized || WindowOwner.Active() is not null) return;
        TaskbarFlash.Start(this);
    }

    private void OnAppActivated(object? sender, EventArgs e) => TaskbarFlash.StopIfFlashing(this);

    private void OnPopIn(object sender, RoutedEventArgs e) => Conversation.Messages.Social.PopIn(Conversation);
}
