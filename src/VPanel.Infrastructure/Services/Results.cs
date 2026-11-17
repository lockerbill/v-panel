namespace VPanel.Infrastructure.Services;

public sealed record Result<T>(T? Value, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
    public static Result<T> Ok(T value) => new(value, []);
    public static Result<T> Fail(params string[] errors) => new(default, errors);
    public static Result<T> Fail(IReadOnlyList<string> errors) => new(default, errors);
}
