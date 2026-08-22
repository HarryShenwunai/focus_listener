namespace FocusListener.Tests;

internal sealed class InlineTestProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
