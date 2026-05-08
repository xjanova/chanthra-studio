using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is null)
                DataContext = new QueueViewModel(App.Current.Studio);
            else if (DataContext is QueueViewModel qvm)
                qvm.Refresh();
        };
    }
}
