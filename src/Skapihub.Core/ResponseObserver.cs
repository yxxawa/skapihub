using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Skapihub.Core;

public sealed class ResponseObserver(bool eventStream, Stopwatch watch)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private readonly MemoryStream _json = new();
    private bool _skipLine;
    private bool _skipEvent;
    private bool _jsonTooLarge;
    public double? FirstTokenMs { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public long? TotalTokens { get; private set; }
    public bool TerminalSeen { get; private set; }
    public string? Error { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (!eventStream)
        {
            if (_json.Length + bytes.Length <= 16 * 1024 * 1024) _json.Write(bytes);
            else _jsonTooLarge = true;
            return;
        }
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, false);
        for (var i = 0; i < count; i++)
        {
            var c = chars[i];
            if (c == '\n')
            {
                if (!_skipLine) ProcessLine(_line.ToString().TrimEnd('\r'));
                _line.Clear(); _skipLine = false;
            }
            else if (_line.Length < 1024 * 1024 && !_skipLine) _line.Append(c);
            else { _line.Clear(); _skipLine = true; _skipEvent = true; }
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0)
        {
            if (!_skipEvent && _data.Length > 0) ProcessEvent(_data.ToString());
            _data.Clear(); _skipEvent = false;
        }
        else if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            if (_data.Length > 1024 * 1024) { _skipEvent = true; return; }
            if (_data.Length > 0) _data.Append('\n');
            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ') value = value[1..];
            _data.Append(value);
        }
    }

    private void ProcessEvent(string data)
    {
        if (data.Trim() == "[DONE]") { TerminalSeen = true; return; }
        try
        {
            using var document = JsonDocument.Parse(data);
            var rootElement = document.RootElement;
            Observe(rootElement);
            if (FirstTokenMs is null && ContainsToken(rootElement)) FirstTokenMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        }
        catch (JsonException) { }
    }

    public void Finish()
    {
        if (eventStream)
        {
            if (_line.Length > 0 && !_skipLine) ProcessLine(_line.ToString().TrimEnd('\r'));
            ProcessLine("");
        }
        else if (!_jsonTooLarge && _json.Length > 0)
        {
            try { using var document = JsonDocument.Parse(_json.ToArray()); Observe(document.RootElement); }
            catch (JsonException) { }
        }
        _json.Dispose();
    }

    private void Observe(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object) return;
        if (rootElement.TryGetProperty("usage", out var usage)) ReadUsage(usage);
        if (rootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("usage", out usage)) ReadUsage(usage);
        if (rootElement.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object && response.TryGetProperty("usage", out usage)) ReadUsage(usage);
        var type = Text(rootElement, "type");
        if (type is "message_stop" or "response.completed") TerminalSeen = true;
        if (type is "error" or "response.failed" || rootElement.TryGetProperty("error", out _)) Error = "上游返回错误事件。";
        if (rootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            foreach (var choice in choices.EnumerateArray())
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String) TerminalSeen = true;
    }

    private void ReadUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;
        var input = Number(usage, "prompt_tokens") ?? Number(usage, "input_tokens");
        if (input is not null && !usage.TryGetProperty("prompt_tokens", out _)) input += (Number(usage, "cache_read_input_tokens") ?? 0) + (Number(usage, "cache_creation_input_tokens") ?? 0);
        InputTokens = Max(InputTokens, input);
        OutputTokens = Max(OutputTokens, Number(usage, "completion_tokens") ?? Number(usage, "output_tokens"));
        TotalTokens = Max(TotalTokens, Number(usage, "total_tokens"));
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    private static long? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Nonempty(JsonElement e, string key) => e.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.String && v.GetString()?.Length > 0 || v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0);

    private static bool HasToolContent(JsonElement delta)
    {
        if (delta.TryGetProperty("function_call", out var function) && function.ValueKind == JsonValueKind.Object
            && (Nonempty(function, "name") || Nonempty(function, "arguments"))) return true;
        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray())
                if (call.ValueKind == JsonValueKind.Object && call.TryGetProperty("function", out function) && function.ValueKind == JsonValueKind.Object
                    && (Nonempty(function, "name") || Nonempty(function, "arguments"))) return true;
        return false;
    }

    private static bool ContainsToken(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object) return false;
        var type = Text(rootElement, "type");
        if (type is "response.output_text.delta" or "response.reasoning_text.delta" or "response.reasoning_summary_text.delta" or "response.function_call_arguments.delta") return Nonempty(rootElement, "delta");
        if (rootElement.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
            && (Nonempty(delta, "text") || Nonempty(delta, "thinking") || Nonempty(delta, "partial_json"))) return true;
        if (rootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            foreach (var choice in choices.EnumerateArray())
            {
                if (Nonempty(choice, "text")) return true;
                if (choice.TryGetProperty("delta", out delta) && delta.ValueKind == JsonValueKind.Object
                    && (Nonempty(delta, "content") || Nonempty(delta, "reasoning_content") || Nonempty(delta, "reasoning") || HasToolContent(delta))) return true;
            }
        return false;
    }
}
