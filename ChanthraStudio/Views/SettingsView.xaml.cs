using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // ViewModel created lazily so design-time DataContext doesn't trigger DB bootstrap.
        Loaded += (_, _) =>
        {
            if (DataContext is null)
            {
                var s = App.Current.Studio;
                DataContext = new SettingsViewModel(s.Settings, s.Providers);
            }
        };
    }

    private void ProviderKey_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Prime the box once with the decrypted key — PasswordBox doesn't accept Bindings
        // on Password (by design), so we sync it manually on first appearance.
        if (sender is PasswordBox pb && pb.Tag is ProviderRow row && pb.Password != row.ApiKeyDraft)
            pb.Password = row.ApiKeyDraft;
    }

    private void ProviderKey_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.Tag is ProviderRow row)
        {
            if (pb.Password != row.ApiKeyDraft)
                row.ApiKeyDraft = pb.Password;
        }
    }
}
