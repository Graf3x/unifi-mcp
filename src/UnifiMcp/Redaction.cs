using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UnifiMcp;

/// <summary>
/// What of the console's answers may reach the client. Credentials the console sends (Wi-Fi passphrases, per-network
/// keys, RADIUS secrets, the internal API's hidden <c>x_</c> fields) are replaced with <see cref="Mark"/> before any
/// answer or error leaves the server, writes on or off; ids, names and every other setting stay. An answer that is not
/// JSON cannot be checked, so it is not shown at all.
/// </summary>
/// <remarks>
/// A field is secret by its name anywhere in an answer (<see cref="IsSecretName"/>), or, in one operation's answers, when
/// the OpenAPI document marks it secret in that operation's response (<see cref="DocumentedSecrets"/>): that is how a
/// voucher's <c>code</c> is hidden while an error's <c>code</c> is kept. Voucher codes are the point of the hotspot
/// voucher operations, so a deployment that hands them out can set UNIFI_REVEAL_SECRETS=voucher-codes. Nothing reveals a
/// Wi-Fi passphrase or key; read those in the UniFi app.
/// </remarks>
public static partial class Redaction
{
    /// <summary>What a secret value is replaced with.</summary>
    public const string Mark = "[redacted]";

    /// <summary>The document's schema whose secret fields UNIFI_REVEAL_SECRETS=voucher-codes shows.</summary>
    private const string VoucherSchema = "Hotspot voucher details";

    private const string NotJson = "[an answer that is not JSON, so it is not shown]";

    private static readonly JsonReaderOptions Reading = new() { MaxDepth = 256 };
    private static readonly JsonWriterOptions Writing = new() { MaxDepth = 256, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Lazy<Document> Doc = new(Document.Load);

    /// <summary>
    /// One operation's successful answer for the client, or null when it is not JSON (the caller says so instead).
    /// </summary>
    public static string? Answer(ApiOperation op, string body, Settings settings)
    {
        var extra = Doc.Value.SecretsOf(op.OperationId)
            .Where(f => !(settings.RevealVoucherCodes && f.Schema == VoucherSchema))
            .Select(f => f.Field)
            .ToHashSet(StringComparer.Ordinal);
        return Rewrite(body, name => IsSecretName(name) || extra.Contains(name));
    }

    /// <summary>An error answer, for an error message: redacted by name, or a fixed line when it is not JSON.</summary>
    public static string Error(string body) => Rewrite(body, IsSecretName) ?? NotJson;

    /// <summary>
    /// Whether a field holds a credential wherever it appears: a passphrase, password, secret, private or API key,
    /// token or PSK by name (ignoring case, <c>_</c> and <c>-</c>), or any of the internal API's hidden <c>x_</c> fields.
    /// A field that holds pre-shared keys is a secret too when the keys come as bare text.
    /// </summary>
    public static bool IsSecretName(string name) =>
        name.StartsWith("x_", StringComparison.OrdinalIgnoreCase) || SecretName().IsMatch(Normal(name));

    /// <summary>A field whose bare text values are keys, though its objects (a key with its network) are not secret as such.</summary>
    private static bool HoldsKeys(string name) => Normal(name) is var n && (n.EndsWith("presharedkey", StringComparison.Ordinal) || n.EndsWith("presharedkeys", StringComparison.Ordinal));

    private static string Normal(string name) => name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    [GeneratedRegex("passphrase|password|passwd|secret|privatekey|apikey|authkey|token|psk$")]
    private static partial Regex SecretName();

    /// <summary>
    /// Every field the document itself marks secret, as "schema.field", that the name rule does not already cover: for
    /// review when the document changes.
    /// </summary>
    public static IReadOnlyList<string> DocumentedSecrets() =>
        Doc.Value.All.Where(f => !IsSecretName(f.Field)).Select(f => $"{f.Schema}.{f.Field}").Distinct().ToList();

    /// <summary>The JSON again with every secret value replaced; null when it is not one JSON value.</summary>
    private static string? Rewrite(string body, Func<string, bool> secret)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            var reader = new Utf8JsonReader(bytes, Reading);
            using var output = new MemoryStream(bytes.Length);
            using (var writer = new Utf8JsonWriter(output, Writing))
            {
                // For each open array: whether its bare values are keys.
                var keys = new Stack<bool>();
                var next = false; // the array about to open holds keys
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            var name = reader.GetString()!;
                            writer.WritePropertyName(name);
                            next = HoldsKeys(name);
                            if (secret(name) || (next && Peek(reader) is not (JsonTokenType.StartArray or JsonTokenType.StartObject)))
                            {
                                reader.Read();
                                if (reader.TokenType == JsonTokenType.Null) writer.WriteNullValue();
                                else
                                {
                                    reader.Skip();
                                    writer.WriteStringValue(Mark);
                                }
                                next = false;
                            }
                            continue;
                        case JsonTokenType.StartObject:
                            writer.WriteStartObject();
                            keys.Push(false);
                            break;
                        case JsonTokenType.EndObject:
                            writer.WriteEndObject();
                            keys.Pop();
                            break;
                        case JsonTokenType.StartArray:
                            writer.WriteStartArray();
                            keys.Push(next);
                            break;
                        case JsonTokenType.EndArray:
                            writer.WriteEndArray();
                            keys.Pop();
                            break;
                        case JsonTokenType.Null:
                            writer.WriteNullValue();
                            break;
                        case var _ when keys.Count > 0 && keys.Peek():
                            writer.WriteStringValue(Mark);
                            break;
                        case JsonTokenType.String:
                            writer.WriteStringValue(reader.GetString());
                            break;
                        case JsonTokenType.Number:
                            writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                            break;
                        case JsonTokenType.True or JsonTokenType.False:
                            writer.WriteBooleanValue(reader.GetBoolean());
                            break;
                    }
                    next = false;
                }
            }
            return output.Length == 0 ? null : Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The kind of the next token, without moving the reader (it is a struct, so this reads a copy).</summary>
    private static JsonTokenType Peek(Utf8JsonReader copy) => copy.Read() ? copy.TokenType : JsonTokenType.None;

    /// <summary>A field the document marks secret, and the schema it is declared in.</summary>
    private sealed record Documented(string Schema, string Field);

    /// <summary>The embedded OpenAPI document's secret fields: all of them, and those in each operation's answers.</summary>
    private sealed partial class Document
    {
        private readonly JsonObject _schemas;
        private readonly Dictionary<string, IReadOnlyList<Documented>> _byOperation = new(StringComparer.Ordinal);

        public List<Documented> All { get; } = [];

        private Document(JsonObject doc)
        {
            _schemas = doc["components"]?["schemas"]?.AsObject() ?? [];
            foreach (var (name, schema) in _schemas) All.AddRange(Own(name, schema));
            foreach (var (_, item) in doc["paths"]?.AsObject() ?? [])
                foreach (var (_, node) in item?.AsObject() ?? [])
                {
                    if (node is not JsonObject op || op["operationId"]?.GetValue<string>() is not { } id) continue;
                    var found = new List<Documented>();
                    foreach (var (code, response) in op["responses"]?.AsObject() ?? [])
                        if (code.StartsWith('2') && response?["content"]?["application/json"]?["schema"] is { } schema)
                            Walk(schema, "", found, []);
                    _byOperation[id] = found;
                }
        }

        public static Document Load()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("openapi.json")
                ?? throw new InvalidOperationException("The embedded OpenAPI document is missing.");
            return new Document(JsonNode.Parse(stream)!.AsObject());
        }

        public IReadOnlyList<Documented> SecretsOf(string operationId) => _byOperation.GetValueOrDefault(operationId) ?? [];

        /// <summary>The fields one schema declares itself (in its properties or an inline allOf part) that it marks secret.</summary>
        private static IEnumerable<Documented> Own(string schemaName, JsonNode? schema)
        {
            if (schema is not JsonObject o) yield break;
            foreach (var (field, property) in o["properties"]?.AsObject() ?? [])
                if (Marked(property)) yield return new(schemaName, field);
            foreach (var part in o["allOf"]?.AsArray() ?? [])
                if (part?["$ref"] is null)
                    foreach (var f in Own(schemaName, part)) yield return f;
        }

        /// <summary>Secret by the document: a password format, write-only, or a description that calls it a secret or password.</summary>
        private static bool Marked(JsonNode? property) =>
            property is JsonObject p
            && (p["format"]?.GetValue<string>() == "password" || p["writeOnly"]?.GetValue<bool>() == true
                || (p["description"]?.GetValue<string>() is { } d && SecretWords().IsMatch(d)));

        [GeneratedRegex(@"\b(secret|password|passphrase)\b", RegexOptions.IgnoreCase)]
        private static partial Regex SecretWords();

        /// <summary>Every schema an answer can contain, collecting the secret fields each declares.</summary>
        private void Walk(JsonNode? node, string schemaName, List<Documented> found, HashSet<string> seen)
        {
            switch (node)
            {
                case JsonObject o when o["$ref"]?.GetValue<string>() is { } reference:
                    var name = reference[(reference.LastIndexOf('/') + 1)..];
                    if (!seen.Add(name)) return;
                    found.AddRange(Own(name, _schemas[name]));
                    Walk(_schemas[name], name, found, seen);
                    return;
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "discriminator")
                        {
                            foreach (var (_, target) in value?["mapping"]?.AsObject() ?? [])
                                Walk(new JsonObject { ["$ref"] = target?.GetValue<string>() }, schemaName, found, seen);
                        }
                        else if (key is "properties" && value is JsonObject properties)
                        {
                            foreach (var (_, property) in properties) Walk(property, schemaName, found, seen);
                        }
                        else if (key is "items" or "additionalProperties" or "allOf" or "oneOf" or "anyOf")
                        {
                            Walk(value, schemaName, found, seen);
                        }
                    }
                    return;
                case JsonArray a:
                    foreach (var item in a) Walk(item, schemaName, found, seen);
                    return;
            }
        }
    }
}
