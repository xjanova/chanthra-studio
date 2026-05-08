using System.Collections.Generic;
using System.Windows;
using ChanthraStudio.Services;

namespace ChanthraStudio.Views.Dialogs;

public sealed class PostTarget
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ConfigSummary { get; init; } = "";
}

public partial class PostDialog : Window
{
    public List<PostTarget> ProviderOptions { get; } = new();
    public PostTarget? SelectedProvider { get; set; }

    public string ClipFileName { get; }
    public string Caption => CaptionBox.Text;
    public string ProviderId => SelectedProvider?.Id ?? "";
    public bool Confirmed { get; private set; }

    public PostDialog(StudioContext ctx, string clipFileName, string defaultCaption)
    {
        ClipFileName = clipFileName;

        var fbConfigured = !string.IsNullOrWhiteSpace(ctx.Settings.PostFacebookPageId)
                          && ctx.Settings.HasApiKey("facebook");
        var webhookConfigured = !string.IsNullOrWhiteSpace(ctx.Settings.PostWebhookUrl);

        ProviderOptions.Add(new PostTarget
        {
            Id = "facebook",
            DisplayName = "Facebook Page · Graph API",
            ConfigSummary = fbConfigured
                ? $"page id {ctx.Settings.PostFacebookPageId} · token saved"
                : "⚠ page id + token missing in Settings",
        });
        ProviderOptions.Add(new PostTarget
        {
            Id = "webhook",
            DisplayName = "Generic webhook",
            ConfigSummary = webhookConfigured
                ? ctx.Settings.PostWebhookUrl
                : "⚠ webhook URL missing in Settings",
        });
        // Default to webhook if configured, else first option.
        SelectedProvider = webhookConfigured
            ? ProviderOptions[1]
            : ProviderOptions[0];

        InitializeComponent();
        DataContext = this;
        CaptionBox.Text = defaultCaption;
        Loaded += (_, _) => CaptionBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
