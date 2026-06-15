using Avalonia.Threading;

namespace Gomp.App.Services;

/// <summary>
/// Marshals an action onto the UI thread. The gomp client raises observer
/// callbacks from the daemon's background event stream, so every mutation of a
/// bound collection has to hop here first. Abstracted so view-models can be
/// tested with a synchronous fake instead of a running Avalonia dispatcher.
/// </summary>
public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

/// <summary>The live dispatcher: posts onto Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
