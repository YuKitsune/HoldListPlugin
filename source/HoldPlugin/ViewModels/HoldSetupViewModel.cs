using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using HoldPlugin.Contracts;

namespace HoldPlugin.ViewModels;

public partial class HoldSetupViewModel : ObservableObject
{
    readonly IErrorReporter _errorReporter;
    
    [ObservableProperty] HoldButtonViewModel _button1;
    [ObservableProperty] HoldButtonViewModel _button2;
    [ObservableProperty] HoldButtonViewModel _button3;
    [ObservableProperty] HoldButtonViewModel _button4;

    public HoldSetupViewModel(string[] holds, IErrorReporter errorReporter)
    {
        _errorReporter = errorReporter;

        Button1 = new HoldButtonViewModel(0, holds[0], this, _errorReporter);
        Button2 = new HoldButtonViewModel(1, holds[1], this, _errorReporter);
        Button3 = new HoldButtonViewModel(2, holds[2], this, _errorReporter);
        Button4 = new HoldButtonViewModel(3, holds[3], this, _errorReporter);
    }

    public bool IsHoldPointNameTaken(string pointName, int excludeIndex)
    {
        if (string.IsNullOrWhiteSpace(pointName))
            return false;

        var buttons = new[] { Button1, Button2, Button3, Button4 };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i != excludeIndex &&
                !string.IsNullOrWhiteSpace(buttons[i].HoldPointName) &&
                buttons[i].HoldPointName.Equals(pointName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public partial class HoldButtonViewModel : ObservableObject
    {
        readonly int _index;
        readonly HoldSetupViewModel _parent;
        readonly IErrorReporter _errorReporter;

        [ObservableProperty] string? _holdPointName;
        [ObservableProperty] bool _isEditing;
        [ObservableProperty] string _editValue = "";

        public HoldButtonViewModel(int index, string pointName, HoldSetupViewModel parent, IErrorReporter errorReporter)
        {
            _index = index;
            _parent = parent;
            _errorReporter = errorReporter;
            
            HoldPointName = pointName;
        }

        public string DisplayText => string.IsNullOrWhiteSpace(HoldPointName)
            ? "XXXXXXXXXXX"
            : HoldPointName;

        [RelayCommand]
        void Edit()
        {
            EditValue = HoldPointName ?? "";
            IsEditing = true;
        }

        [RelayCommand]
        void CommitEdit()
        {
            if (!IsEditing)
                return;

            var trimmedValue = EditValue.Trim().ToUpperInvariant();

            // Check for duplicates
            if (!string.IsNullOrWhiteSpace(trimmedValue) &&
                _parent.IsHoldPointNameTaken(trimmedValue, _index))
            {
                _errorReporter.ReportError($"Hold Window for {trimmedValue} already exists");
                
                IsEditing = false;
                EditValue = "";
                return;
            }

            // If there was a previous hold point, delete it
            if (!string.IsNullOrWhiteSpace(HoldPointName))
            {
                WeakReferenceMessenger.Default.Send(new HoldPointRemovedCommand(HoldPointName));
            }

            // Update the hold point name
            var previousName = HoldPointName;
            HoldPointName = string.IsNullOrWhiteSpace(trimmedValue) ? null : trimmedValue;
            OnPropertyChanged(nameof(DisplayText));

            // If the new name is not empty, create the hold list
            if (!string.IsNullOrWhiteSpace(HoldPointName))
            {
                WeakReferenceMessenger.Default.Send(new HoldPointAddedCommand(_index, HoldPointName));
            }

            IsEditing = false;
            EditValue = "";
        }

        [RelayCommand]
        void CancelEdit()
        {
            IsEditing = false;
            EditValue = "";
        }
    }
}