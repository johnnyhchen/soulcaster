namespace JcAttractor.UnifiedLlm;

public record ResponseFormat(
    string Type,
    Dictionary<string, object>? JsonSchema = null,
    bool Strict = false)
{
    public static readonly ResponseFormat TextFormat = new("text");
    public static readonly ResponseFormat JsonFormat = new("json_object");

    public static ResponseFormat JsonSchemaFormat(Dictionary<string, object> schema, bool strict = true) =>
        new("json_schema", schema, strict);
}
