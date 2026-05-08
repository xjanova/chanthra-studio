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

    public StubView() => InitializeComponent();
}
