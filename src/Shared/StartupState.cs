namespace DeskCards;

internal static class StartupState
{
    public static bool Enabled(object? runValue, object? approvalValue) => runValue != null
        && (!(approvalValue is byte[] bytes) || bytes.Length == 0 || (bytes[0] & 1) == 0);

    public static byte[] EnabledBytes() => new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
}
