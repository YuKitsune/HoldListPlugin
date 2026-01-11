using vatsys;

namespace HoldPlugin;

public interface IErrorReporter
{
    void ReportError(string message);
    void ReportError(Exception exception);
}

public class ErrorReporter : IErrorReporter
{
    public void ReportError(string message)
    {
        Errors.Add(new Exception(message), Plugin.Name);
    }

    public void ReportError(Exception exception)
    {
        Errors.Add(exception, Plugin.Name);
    }
}