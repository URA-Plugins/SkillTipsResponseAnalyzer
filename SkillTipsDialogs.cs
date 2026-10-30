using Terminal.Gui.App;

namespace SkillTipsResponseAnalyzer;

internal static class SkillTipsDialogs
{
    internal static async Task<T> RunAsync<T>(
        IApplication application, Func<T> runDialog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnIteration(object? sender, Terminal.Gui.App.EventArgs<IApplication?> e)
        {
            application.Iteration -= OnIteration;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(runDialog());
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        // Terminal.Gui 2.4.17 holds its timer lock during Invoke callbacks; open modals outside it.
        application.Iteration += OnIteration;
        try
        {
            application.Invoke(static () => { });
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { application.Iteration -= OnIteration; }
    }
}
