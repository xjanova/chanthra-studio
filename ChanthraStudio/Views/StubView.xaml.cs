using System.Windows;
using System.Windows.Controls;

namespace ChanthraStudio.Views;

public partial class StubView : UserControl
{
    public static readonly DependencyProperty TagPropertyAlt = DependencyProperty.Register(
        nameof(Tag), typeof(string), typeof(StubView), new PropertyMetadata("View"));

    public new string Tag
    {
        get => (string)GetValue(TagPropertyAlt);
        set => SetValue(TagPropertyAlt, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(StubView),
        new PropertyMetadata("This panel is on the roadmap but not yet shipped."));

    /// <summary>Body copy shown under the "COMING SOON" stamp. Set per
    /// usage so the user understands what this panel will do and what
    /// they should reach for in the meantime.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public StubView() => InitializeComponent();
}
