using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>Preserves actionable module errors across the host command boundary.</summary>
internal static class DianaCommandGuard
{
    public static CommandResult Run(Func<CommandResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    public static async Task<CommandResult> RunAsync(Func<Task<CommandResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException
            or FormatException
            or OverflowException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or JsonException;
}
