using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Forms.VisualStyles;
using vatsys;

namespace HoldPlugin;

public class VatSysForm : BaseForm
{
    public event FormClosingEventHandler? CustomFormClosing;

    bool ForceClosing { get; set; }

    public IWindowHandle WindowHandle { get; }

    public void ForceClose()
    {
        ForceClosing = true;
        HideOnClose = false;
        Close();
    }

    public VatSysForm(string title, Func<IWindowHandle, UIElement> childFactory, bool shrinkToContent, bool canClose, bool canMinimise)
    {
        MiddleClickClose = false; // This is on by default. Why...

        Text = title;

        WindowHandle = new WindowHandle(this);
        var child = childFactory(WindowHandle);

        var elementHost = new ElementHost();
        if (shrinkToContent)
        {
            // For text wrapping to work correctly, we need to measure with a constraint
            var maxWidth = 1000; // Maximum dialog width
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            child.Arrange(new Rect(child.DesiredSize));
            child.UpdateLayout();

            var desired = child.DesiredSize;
            elementHost.Child = child;
            elementHost.Size = new System.Drawing.Size((int)Math.Ceiling(desired.Width), (int)Math.Ceiling(desired.Height));
            elementHost.Location = new System.Drawing.Point(0, 0);

            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = elementHost.Size;
        }
        else
        {
            elementHost.Child = child;
            elementHost.Dock = DockStyle.Fill;
        }

        if (!shrinkToContent)
        {
            elementHost.Dock = DockStyle.Fill;
        }

        if (!canClose)
        {
            HasCloseButton = false;
        }

        if (!canMinimise)
        {
            MinimizeBox = false;
            HasMinimizeButton = false;
        }

        Controls.Add(elementHost);

        Padding = new Padding(0);
        Margin = new Padding(0);
        PerformLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ForceClosing && CustomFormClosing != null)
        {
            CustomFormClosing.Invoke(this, e);
            if (e.Cancel)
                return;
        }

        base.OnFormClosing(e);

        if (ForceClosing)
        {
            e.Cancel = false;
        }
    }
}
