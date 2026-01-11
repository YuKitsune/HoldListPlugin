using System.Windows;
using System.Windows.Controls;
using HoldPlugin.ViewModels;

namespace HoldPlugin.Controls;

public partial class HoldSetup : UserControl
{
    public HoldSetup(HoldSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    void HoldPointTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: HoldSetupViewModel.HoldButtonViewModel viewModel })
            return;

        viewModel.CommitEditCommand.Execute(null);
    }

    void HoldPointTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox textBox || e.NewValue is not bool isVisible || !isVisible)
            return;

        textBox.Focus();
        textBox.SelectAll();
    }
}