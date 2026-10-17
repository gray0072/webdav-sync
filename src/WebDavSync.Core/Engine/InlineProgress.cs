namespace WebDavSync.Core.Engine;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/>: reports run in order on the reporting thread. <see cref="Progress{T}"/> posts
/// them to the thread pool, so a late report could overwrite a status set after the work finished.
/// </summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
