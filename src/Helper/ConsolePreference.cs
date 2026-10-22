namespace Nexus.Service.Helper;

/// <summary>Helper selection rule for keystroke injection: the console session's helper when there is one, else any helper.</summary>
public static class ConsolePreference
{
    public static T? Pick<T>(T? console, System.Func<T?> any) where T : class => console ?? any();
}
