using System.Windows.Forms;

namespace HoldPlugin;

public interface IWindowHandle
{
    void Focus();
    void Close();
}

public class WindowHandle(VatSysForm form) : IWindowHandle
{
    public void Focus()
    {
        form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    public void Close()
    {
        form.ForceClose();
    }
}
