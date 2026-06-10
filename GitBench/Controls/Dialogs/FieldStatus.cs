namespace GitBench.Controls.Dialogs;

/// <summary>
/// Severity of a <see cref="FieldStatus"/> shown beneath a <see cref="LabeledInputField"/>.
/// Drives the field's border + message color: <see cref="Error"/> reuses the dialog error
/// token, <see cref="Warning"/> the dialog warning (amber) token.
/// </summary>
public enum FieldSeverity
{
    Error,
    Warning,
}

/// <summary>
/// A validation result attached to a single input field. <c>null</c> (the absence of a
/// status) means the field is neutral — normal border, no message line. A non-null value
/// recolors the field's border and reveals a wrapped message beneath it, keyed off
/// <see cref="Severity"/>.
/// </summary>
public sealed record FieldStatus(FieldSeverity Severity, string Message);
