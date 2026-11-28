namespace EpiInfo.Domain.Events;

/// <summary>
/// One message box requested by a check-code <c>DIALOG "&lt;text&gt;" [TITLETEXT = "&lt;title&gt;"]</c> statement.
/// It travels in the <c>dialogs</c> list of a field-event response, as <c>{message, title}</c> on the wire, and the
/// client opens one modal per entry in list order. Nothing is shown or decided by the server.
/// </summary>
/// <param name="Message">
/// The dialog text exactly as the program's string literal yields it, without its quotes. Never
/// <see langword="null"/>.
/// </param>
/// <param name="Title">
/// The <c>TITLETEXT</c> value, or <see langword="null"/> when the statement has none. No default caption is
/// substituted here; choosing one for a missing title is the client's display rule.
/// </param>
/// <exception cref="ArgumentNullException"><paramref name="Message"/> is <see langword="null"/>.</exception>
public sealed record DialogMessage(string Message, string? Title)
{
    private readonly string message = ValidateMessage(Message);

    /// <summary>
    /// The dialog text exactly as the program's string literal yields it, without its quotes. Never
    /// <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public string Message
    {
        get => message;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => message = ValidateMessage(value);
    }

    /// <summary>
    /// The <c>TITLETEXT</c> value, or <see langword="null"/> when the statement has none. No default caption is
    /// substituted here; choosing one for a missing title is the client's display rule.
    /// </summary>
    // Declared explicitly, after Message, because a synthesised positional property is emitted ahead of the
    // explicit Message and would put "title" before "message" in the serialised JSON and the OpenAPI schema.
    public string? Title { get; init; } = Title;

    private static string ValidateMessage(string value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Message));
        return value;
    }
}
