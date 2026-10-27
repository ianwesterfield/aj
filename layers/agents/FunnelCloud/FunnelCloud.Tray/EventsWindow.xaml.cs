using System.Windows;
using System.Windows.Input;
using FunnelCloud.Tray.ViewModels;
using Wpf.Ui.Controls;

namespace FunnelCloud.Tray;

public partial class EventsWindow : FluentWindow
{
  private bool _isClosing;

  public EventsWindow()
  {
  }

  protected override void OnDeactivated(EventArgs e)
  {
    base.OnDeactivated(e);
    // Close when losing focus (guard against recursive close)
    if (!_isClosing)
    {
      _isClosing = true;
      Close();
    }
  }

  protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
  {
    _isClosing = true;
    base.OnClosing(e);
  }

  private void EventItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
  {
    if (sender is FrameworkElement element && element.DataContext is EventItemViewModel vm)
    {
      if (vm.HasResult)
      {
        vm.IsExpanded = !vm.IsExpanded;
      }
    }
  }
}
