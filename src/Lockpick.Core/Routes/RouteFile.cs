using System.Text.Json;
using Lockpick.Core.State;

namespace Lockpick.Core.Routes;

/// <summary>
/// Reads routes from JSON: <c>{"routes": [{"id", "name", "category", "character", "verified", "source", "steps": [...]}]}</c>,
/// each step one of <c>{"get": item, "in"?: room, "hint"?}</c>, <c>{"use": item, "in"?, "hint"?}</c>,
/// <c>{"go": room, "hint"?}</c> or <c>{"end": true}</c>; any step can carry <c>"done": "field:0xMASK"</c>, the game flag set
/// once it has happened, and <c>"spots": [[x, z, width, depth, "label", "field:0xMASK"?], ...]</c>, a puzzle's spots in the
/// order to press (with the flag the game sets once each is pressed), and <c>"at": [x, z]</c>, where in its room a step
/// the room file doesn't place happens.
/// </summary>
public static class RouteFile
{
    public static IReadOnlyList<Route> Parse(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("routes").EnumerateArray().Select(Route)];
    }

    private static Route Route(JsonElement r)
    {
        var character = r.GetProperty("character").GetString() switch
        {
            "jill" => PlayableCharacter.Jill,
            "chris" => PlayableCharacter.Chris,
            _ => PlayableCharacter.Unknown,
        };
        return new Route(S(r, "id")!, S(r, "name")!, S(r, "category") ?? S(r, "name")!, character,
            [.. r.GetProperty("steps").EnumerateArray().Select(Step)],
            r.TryGetProperty("verified", out var v) && v.GetBoolean(), S(r, "source") ?? string.Empty)
        {
            Timers = r.TryGetProperty("timers", out var timers)
                ? [.. timers.EnumerateArray().Select(t => new RouteTimer(S(t, "label")!, Flag(S(t, "start")!), Flag(S(t, "stop")!), t.GetProperty("minutes").GetDouble()))]
                : [],
        };
    }

    private static RouteStep Step(JsonElement s) => Plain(s) with
    {
        Spots = s.TryGetProperty("spots", out var spots)
            ? [.. spots.EnumerateArray().Select(p => new RouteSpot(p[0].GetInt32(), p[1].GetInt32(), p[2].GetInt32(), p[3].GetInt32(), p[4].GetString()!,
                p.GetArrayLength() > 5 ? Flag(p[5].GetString()!) : null))]
            : [],
        At = s.TryGetProperty("at", out var at) ? new RoutePoint(at[0].GetDouble(), at[1].GetDouble()) : null,
        Optional = s.TryGetProperty("optional", out var optional) && optional.GetBoolean(),
    };

    private static RouteStep Plain(JsonElement s)
    {
        var room = S(s, "in");
        var hint = S(s, "hint");
        var done = S(s, "done") is { } flag ? Flag(flag) : null;
        if (s.TryGetProperty("get", out var get))
        {
            return new RouteStep(RouteStepKind.Get, get.GetInt32(), room, hint, done);
        }

        if (s.TryGetProperty("use", out var use))
        {
            return new RouteStep(RouteStepKind.Use, use.GetInt32(), room, hint, done);
        }

        if (S(s, "go") is { } go)
        {
            return new RouteStep(RouteStepKind.Go, null, go, hint, done);
        }

        if (s.TryGetProperty("end", out _))
        {
            return new RouteStep(RouteStepKind.End, null, null, hint);
        }

        throw new InvalidDataException($"Unknown route step: {s.GetRawText()}");
    }

    private static RouteFlag Flag(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && text[(colon + 1)..] is ['0', 'x' or 'X', .. var hex]
            && long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var mask)
            ? new RouteFlag(text[..colon], mask)
            : throw new InvalidDataException($"A route flag is \"field:0xMASK\": {text}");
    }

    private static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() : null;
}
