using System.Windows;
using System.Windows.Controls;
using HoldPlugin.ViewModels;

namespace HoldPlugin.Controls;

public partial class HoldList : UserControl
{
    public HoldList(HoldListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    void GlobalOpsTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: HoldItemViewModel viewModel })
            return;
        
        viewModel.CommitGlobalOpsEditCommand.Execute(null);
    }

    void GlobalOpsTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox textBox || e.NewValue is not bool isVisible || !isVisible)
            return;
        
        textBox.Focus();
        textBox.SelectAll();
    }
}