using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Evaluation;

/// <summary>A question for the agent and the tool calls it should make; <c>evals/README.md</c> has the format.</summary>
/// <param name="ExpectedCalls">In the order the agent should make them; may be empty.</param>
/// <param name="AllowedExtraCalls">Tools the agent may also call without being marked down.</param>
public sealed record AgentCase(
    string Id,
    string Question,
    IReadOnlyList<ExpectedCall> ExpectedCalls,
    IReadOnlyList<string> AllowedExtraCalls);

/// <param name="Arguments">
/// The key arguments only. Each is a value, a list of values of which one must be given, or
/// <c>{"words": [...]}</c> for text that must contain every word.
/// </param>
public sealed record ExpectedCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments);

/// <summary>
/// The cases with their invented notices and the date the agent is told is today, so that "next week" means the
/// same in every run.
/// </summary>
public sealed record AgentCaseSet(IReadOnlyList<AgentCase> Cases, IReadOnlyList<Notice> Notices, DateOnly ReferenceDate)
{
    /// <summary>Relative to the repository root.</summary>
    public const string Folder = "evals/agent";

    public const string CasesFile = "cases.jsonl";

    public const string NoticesFile = "notices.jsonl";

    public const string ReferenceDateFile = "reference-date.json";

    // A missing field or a null is an error, not an empty list: a case without expected calls says so with [].
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>The tools of the agent and the parameters of each, read from the tool definitions.</summary>
    public static IReadOnlyDictionary<string, string[]> Tools { get; } = typeof(TenderTools).GetMethods()
        .Where(method => method.IsDefined(typeof(McpServerToolAttribute)))
        .ToDictionary(
            method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
            method => method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).Select(p => p.Name!).ToArray());

    /// <exception cref="InvalidDataException">A file is broken; the message names the line or the case.</exception>
    public static AgentCaseSet Load(string folder)
    {
        var cases = ReadLines<AgentCase>(Path.Combine(folder, CasesFile));
        var problem = cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => $"Case {g.Key}: the id is used more than once.")
            .Concat(cases.SelectMany(Problems))
            .FirstOrDefault();
        if (problem is not null)
        {
            throw new InvalidDataException(problem);
        }

        var date = JsonSerializer.Deserialize<DateFile>(File.ReadAllText(Path.Combine(folder, ReferenceDateFile)), JsonOptions)!;
        return new AgentCaseSet(cases, ReadLines<Notice>(Path.Combine(folder, NoticesFile)), date.ReferenceDate);
    }

    private static IEnumerable<string> Problems(AgentCase @case)
    {
        foreach (var tool in @case.ExpectedCalls.Select(call => call.Tool).Concat(@case.AllowedExtraCalls).Where(tool => !Tools.ContainsKey(tool)))
        {
            yield return $"Case {@case.Id}: there is no tool {tool}.";
        }

        foreach (var call in @case.ExpectedCalls.Where(call => Tools.ContainsKey(call.Tool)))
        {
            foreach (var (name, value) in call.Arguments)
            {
                if (!Tools[call.Tool].Contains(name))
                {
                    yield return $"Case {@case.Id}: {call.Tool} has no argument {name}.";
                }
                else if (!IsValue(value) && !(value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(IsValue)) && !IsWords(value))
                {
                    yield return $"Case {@case.Id}: {name} must be a value, a list of values or {{\"words\": [...]}}.";
                }
            }
        }
    }

    private static bool IsValue(JsonElement value) => value.ValueKind is JsonValueKind.String or JsonValueKind.Number;

    private static bool IsWords(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(p => p.Name).SequenceEqual(["words"])
        && value.GetProperty("words").ValueKind == JsonValueKind.Array
        && value.GetProperty("words").EnumerateArray().All(word => word.ValueKind == JsonValueKind.String);

    private static List<T> ReadLines<T>(string path)
    {
        var items = new List<T>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                items.Add(JsonSerializer.Deserialize<T>(line, JsonOptions)!);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}, line {number}: {exception.Message}", exception);
            }
        }

        return items;
    }

    private sealed record DateFile(DateOnly ReferenceDate);
}
