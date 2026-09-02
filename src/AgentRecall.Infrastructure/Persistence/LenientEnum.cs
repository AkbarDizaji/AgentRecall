using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// Stores an enum as its name, and reads a name it does not recognise back as a fallback
/// member instead of throwing.
///
/// The append-only log tables are written by whichever build ran last and read by whichever
/// build runs next, and those are not always the same build: a newer AgentRecall records a
/// value an older one has never heard of, and EF's plain string conversion then throws for
/// the whole query — so `agentrecall activity` and the turn summary die on one log row rather
/// than skipping it. That is the worst possible place to fail, because those commands are how
/// anyone finds out what AgentRecall did. Read tolerance keeps a forward-written log readable:
/// the unrecognised row still carries its own human-readable summary text.
///
/// Writes are unaffected — a build only ever writes names it knows.
/// </summary>
internal static class LenientEnum
{
    /// <summary>A name-based enum converter that falls back to <paramref name="fallback"/> on an unknown name.</summary>
    public static ValueConverter<TEnum, string> Converter<TEnum>(TEnum fallback)
        where TEnum : struct, Enum =>
        new(value => value.ToString(), name => Parse(name, fallback));

    private static TEnum Parse<TEnum>(string? name, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
