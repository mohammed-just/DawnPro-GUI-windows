namespace Moondrop.Tests;

internal static class AssertEx
{
    public static T ThrowsException<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }

        Assert.Fail($"Expected exception of type {typeof(T).Name}.");
        throw new InvalidOperationException("Unreachable.");
    }

    public static async Task<T> ThrowsExceptionAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T ex)
        {
            return ex;
        }

        Assert.Fail($"Expected exception of type {typeof(T).Name}.");
        throw new InvalidOperationException("Unreachable.");
    }
}
