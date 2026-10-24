namespace DispatchFlow.Application.Orders;

public static class IdempotencyKeyRules {
    public const int MAX_LENGTH = 128;

    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MAX_LENGTH && key.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':');
}
