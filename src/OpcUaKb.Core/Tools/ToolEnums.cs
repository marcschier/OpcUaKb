using System.Text.Json.Serialization;

// Closed-set tool parameters modelled as real JSON enums so the schema constrains the
// values instead of describing them in prose (schema constraints beat prose for model
// tool-use accuracy). Each enum is string-serialized with the exact wire values the
// handlers and the underlying data expect, preserving backward compatibility with callers
// that already pass those strings. Enums live in the global namespace to match the tool
// classes in this folder.

[JsonConverter(typeof(JsonStringEnumConverter<ReleaseStatus>))]
public enum ReleaseStatus
{
    [JsonStringEnumMemberName("released")] Released,
    [JsonStringEnumMemberName("rc")] Rc,
    [JsonStringEnumMemberName("draft")] Draft,
    [JsonStringEnumMemberName("all")] All,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConformanceMode>))]
public enum ConformanceMode
{
    [JsonStringEnumMemberName("expand")] Expand,
    [JsonStringEnumMemberName("satisfy")] Satisfy,
    [JsonStringEnumMemberName("diff")] Diff,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileRelationship>))]
public enum ProfileRelationship
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("includes")] Includes,
    [JsonStringEnumMemberName("included_by")] IncludedBy,
}

[JsonConverter(typeof(JsonStringEnumConverter<SpecSource>))]
public enum SpecSource
{
    [JsonStringEnumMemberName("opcfoundation")] OpcFoundation,
    [JsonStringEnumMemberName("cloudlib")] CloudLib,
}

[JsonConverter(typeof(JsonStringEnumConverter<SpecListOrder>))]
public enum SpecListOrder
{
    [JsonStringEnumMemberName("popularity")] Popularity,
    [JsonStringEnumMemberName("name")] Name,
    [JsonStringEnumMemberName("date")] Date,
}

[JsonConverter(typeof(JsonStringEnumConverter<NodeCountFacet>))]
public enum NodeCountFacet
{
    [JsonStringEnumMemberName("node_class")] NodeClass,
    [JsonStringEnumMemberName("spec_part")] SpecPart,
    [JsonStringEnumMemberName("modelling_rule")] ModellingRule,
    [JsonStringEnumMemberName("data_type")] DataType,
    [JsonStringEnumMemberName("source")] Source,
}

[JsonConverter(typeof(JsonStringEnumConverter<NodeClass>))]
public enum NodeClass
{
    ObjectType,
    Variable,
    Method,
    DataType,
    Object,
    VariableType,
    ReferenceType,
}

[JsonConverter(typeof(JsonStringEnumConverter<ModellingRule>))]
public enum ModellingRule
{
    Mandatory,
    Optional,
    MandatoryPlaceholder,
    OptionalPlaceholder,
}

// Maps enum values to the exact wire strings used in OData filters, downstream helpers,
// and output. Kept explicit (not ToString) so a rename of an enum member can never
// silently change a filter string.
static class ToolEnums
{
    public static string Wire(this ReleaseStatus value) => value switch
    {
        ReleaseStatus.Rc => "rc",
        ReleaseStatus.Draft => "draft",
        ReleaseStatus.All => "all",
        _ => "released",
    };

    public static string Wire(this ConformanceMode value) => value switch
    {
        ConformanceMode.Satisfy => "satisfy",
        ConformanceMode.Diff => "diff",
        _ => "expand",
    };

    public static string Wire(this ProfileRelationship value) => value switch
    {
        ProfileRelationship.Includes => "includes",
        ProfileRelationship.IncludedBy => "included_by",
        _ => "none",
    };

    public static string Wire(this SpecSource value) =>
        value == SpecSource.CloudLib ? "cloudlib" : "opcfoundation";

    public static string Wire(this SpecListOrder value) => value switch
    {
        SpecListOrder.Name => "name",
        SpecListOrder.Date => "date",
        _ => "popularity",
    };

    public static string Wire(this NodeCountFacet value) => value switch
    {
        NodeCountFacet.SpecPart => "spec_part",
        NodeCountFacet.ModellingRule => "modelling_rule",
        NodeCountFacet.DataType => "data_type",
        NodeCountFacet.Source => "source",
        _ => "node_class",
    };
}
