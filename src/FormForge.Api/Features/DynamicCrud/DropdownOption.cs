namespace FormForge.Api.Features.DynamicCrud;

// One option for a Designer-backed Dropdown: the value the form stores and the
// human-readable label shown in the combobox. Value is always serialized as a
// string (the source column may be a uuid / number / text); label may be null
// when the chosen label column is empty for a row.
internal sealed record DropdownOption(string Value, string? Label);
