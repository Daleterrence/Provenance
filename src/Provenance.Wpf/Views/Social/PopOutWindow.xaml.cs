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
        Bar.UpperCase = false;
        Bar.Title = conversation.Title;
        conversation.PropertyChanged += OnConversationChanged;
        var social = conversation.Messages.Social;
        social.MessageReceived += OnMessageLanded;
        IsVisibleChanged += (_, _) => ReportVisible();
        StateChanged += (_, _) => ReportVisible();
        Activated += (_, _) => social.PopOutUsed(conversation);
        Loaded += (_, _) =>
        {
            ReportVisible();
            if (ShowActivated) Pane.FocusCompose();
        };
#pragma warning disable RS0030
        if (Application.Current is { } app) app.Activated += OnAppActivated;
#pragma warning restore RS0030
        Closed += (_, _) =>
        {
            conversation.PropertyChanged -= OnConversationChanged;
            social.MessageReceived -= OnMessageLanded;
#pragma warning disable RS0030
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
#pragma warning restore RS0030
            social.PopOutClosed(conversation);
        };
    }

    public PoppedConversation Conversation { get; }

    private void OnConversationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PoppedConversation.Title)) Bar.Title = Conversation.Title;
    }

    private void ReportVisible() =>
        Conversation.Messages.ConversationVisible = IsVisible && WindowState != WindowState.Minimized;

    private void OnMessageLanded(string who, string _)
    {
        if (!string.Equals(Conversation.Friend.Name, who, StringComparison.OrdinalIgnoreCase)) return;
        if (!Conversation.Messages.Social.Flashes(NoticeKind.Message)) return;
        if (!IsVisible || WindowState == WindowState.Minimized || WindowOwner.Active() is not null) return;
        TaskbarFlash.Start(this);
    }

    private void OnAppActivated(object? sender, EventArgs e) => TaskbarFlash.StopIfFlashing(this);

    internal void PutBack() => Conversation.Messages.Social.PopIn(Conversation);
}
