using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// The agent presets as the settings page edits them: a list to pick one from and the fields of
/// the picked one. Edits write through to the preference as they are made, like every other
/// setting; a field that doesn't read as a preset shows why and saves nothing. The last preset
/// can't be deleted, so there is always an agent to open. UI thread only.
/// </summary>
internal sealed class AgentPresetsSettingsViewModel : IDisposable
{
    private readonly PreferencesService _preferences;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly Derived<FieldStatus?> _nameStatus;
    private readonly Derived<FieldStatus?> _argumentsStatus;
    private readonly Derived<bool> _nameInvalid;
    private readonly Derived<bool> _argumentsInvalid;
    private readonly Derived<bool> _canDelete;
    private bool _loading;

    public AgentPresetsSettingsViewModel(PreferencesService preferences, IMessageBus bus, ILocalizationService loc)
    {
        _preferences = preferences;
        _bus = bus;
        _loc = loc;
        foreach (var preset in preferences.Current.AgentPresets) Presets.Add(preset);

        _nameStatus = new Derived<FieldStatus?>(() => Name.Value.Trim().Length == 0
            ? new FieldStatus(FieldSeverity.Error, loc.Strings.Value.AgentPresetsNameRequired)
            : null);
        _argumentsStatus = new Derived<FieldStatus?>(() =>
            AgentArguments.Parse(Arguments.Value, Kind.Value) is AgentArgumentsParse.Invalid invalid
                ? new FieldStatus(FieldSeverity.Error, Describe(loc.Strings.Value, invalid.Problem))
                : null);
        _nameInvalid = new Derived<bool>(() => _nameStatus.Value is not null);
        _argumentsInvalid = new Derived<bool>(() => _argumentsStatus.Value is not null);
        _canDelete = new Derived<bool>(() => Presets.Count > 1);
        Add = new Command(DoAdd);

        Name.Changed += _ => Commit();
        Kind.Changed += _ => Commit();
        Permission.Changed += _ => Commit();
        Arguments.Changed += _ => Commit();
        Load(Presets.Count > 0 ? Presets[0] : null);
    }

    public ObservableList<AgentPreset> Presets { get; } = new();

    /// <summary>The preset being edited; null only while there is none to edit.</summary>
    public State<AgentPresetId?> SelectedId { get; } = new(null);

    public State<string> Name { get; } = new(string.Empty);
    public State<AgentKind> Kind { get; } = new(AgentKind.ClaudeCode);
    public State<AgentPermission> Permission { get; } = new(AgentPermission.Ask);

    /// <summary>The extra arguments as typed, which may not parse yet.</summary>
    public State<string> Arguments { get; } = new(string.Empty);

    public IReadable<FieldStatus?> NameStatus => _nameStatus;
    public IReadable<FieldStatus?> ArgumentsStatus => _argumentsStatus;
    public IReadable<bool> NameInvalid => _nameInvalid;
    public IReadable<bool> ArgumentsInvalid => _argumentsInvalid;

    public Command Add { get; }

    public IReadable<bool> CanDelete => _canDelete;

    public void Select(AgentPresetId id)
    {
        if (SelectedId.Value == id) return;
        Load(Find(id));
    }

    /// <summary>Deletes the preset, with an Undo that puts it back where it was.</summary>
    public void Delete(AgentPresetId id)
    {
        if (Presets.Count <= 1 || IndexOf(id) is not { } index) return;
        var deleted = Presets[index];
        Presets.RemoveAt(index);
        Save();
        if (SelectedId.Value == id) Load(Presets[Math.Min(index, Presets.Count - 1)]);

        var s = _loc.Strings.Value;
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Info(
            s.AgentPresetsDeleted(deleted.Name),
            new ToastAction(s.AgentPresetsUndo, () =>
            {
                if (IndexOf(deleted.Id) is not null) return;
                Presets.Insert(Math.Min(index, Presets.Count), deleted);
                Save();
                Load(deleted);
            }))));
    }

    private void DoAdd()
    {
        var created = new AgentPreset(
            AgentPresetId.New(), _loc.Strings.Value.AgentPresetsNewName, AgentKind.ClaudeCode, AgentPermission.Ask, []);
        Presets.Add(created);
        Save();
        Load(created);
    }

    private void Load(AgentPreset? preset)
    {
        _loading = true;
        try
        {
            SelectedId.Value = preset?.Id;
            Name.Value = preset?.Name ?? string.Empty;
            Kind.Value = preset?.Kind ?? AgentKind.ClaudeCode;
            Permission.Value = preset?.Permission ?? AgentPermission.Ask;
            Arguments.Value = preset is null ? string.Empty : AgentArguments.Format(preset.Arguments);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Commit()
    {
        if (_loading || SelectedId.Value is not { } id || IndexOf(id) is not { } index) return;
        var name = Name.Value.Trim();
        if (name.Length == 0) return;
        if (AgentArguments.Parse(Arguments.Value, Kind.Value) is not AgentArgumentsParse.Ok parsed) return;

        var edited = new AgentPreset(id, name, Kind.Value, Permission.Value, parsed.Arguments);
        if (edited.Equals(Presets[index])) return;
        Presets.Replace(index, edited);
        Save();
    }

    private void Save()
    {
        var presets = Presets.ToArray();
        _preferences.Update(p => p with { AgentPresets = presets });
    }

    private AgentPreset? Find(AgentPresetId id) => IndexOf(id) is { } index ? Presets[index] : null;

    private int? IndexOf(AgentPresetId id)
    {
        for (var i = 0; i < Presets.Count; i++)
            if (Presets[i].Id == id)
                return i;
        return null;
    }

    public static string Describe(Strings s, AgentArgumentsProblem problem) => problem switch
    {
        AgentArgumentsProblem.UnclosedQuote => s.AgentPresetsArgumentsUnclosedQuote,
        AgentArgumentsProblem.NotAFlag notAFlag => s.AgentPresetsArgumentsNotAFlag(notAFlag.Token),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unknown problem."),
    };

    public void Dispose()
    {
        _canDelete.Dispose();
        _argumentsInvalid.Dispose();
        _nameInvalid.Dispose();
        _argumentsStatus.Dispose();
        _nameStatus.Dispose();
        SelectedId.Dispose();
        Name.Dispose();
        Kind.Dispose();
        Permission.Dispose();
        Arguments.Dispose();
    }
}
