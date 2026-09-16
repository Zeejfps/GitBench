using GitBench.Features.Assistant.Backend;
using GitBench.Features.Repos;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// One role's line of the settings card while it is being edited: the provider picked for it and
/// the model typed for it, applied on save.
/// </summary>
internal sealed class AssistantRoleDraft : IDisposable
{
    private readonly IAssistantSessionStore _store;
    private readonly ILocalizationService _loc;
    private readonly State<string> _providerId;
    private readonly State<string> _model = new(string.Empty);
    private readonly Derived<string> _providerName;
    private readonly Derived<string> _modelHint;
    private readonly Derived<bool> _hasModelPresets;

    public AssistantRoleDraft(AssistantRole role, IAssistantSessionStore store, ILocalizationService loc)
    {
        Role = role;
        _store = store;
        _loc = loc;
        _providerId = new State<string>(AssistantProviders.Default.Id);
        _providerName = new Derived<string>(() => Provider.DisplayName);
        _modelHint = new Derived<string>(() => Provider.DefaultModelFor(role));
        _hasModelPresets = new Derived<bool>(() => Provider.ModelPresets.Count > 0);
    }

    public AssistantRole Role { get; }

    public IReadable<string> ProviderId => _providerId;

    /// <summary>Writable because that is what a two-way bound field binds to.</summary>
    public State<string> Model => _model;

    public IReadable<string> ProviderName => _providerName;

    /// <summary>The provider's own model, shown as the placeholder for "leave it alone".</summary>
    public IReadable<string> ModelHint => _modelHint;

    /// <summary>Whether the provider publishes models worth offering. False for the endpoints that
    /// serve whatever the user loaded, where the field is free text and nothing else.</summary>
    public IReadable<bool> HasModelPresets => _hasModelPresets;

    public AssistantProvider Provider => AssistantProviders.Resolve(_providerId.Value);

    /// <summary>What the line would save: the provider picked, and the model typed or none.</summary>
    public AssistantModelChoice Choice => AssistantModelChoice.For(_providerId.Value, _model.Value);

    public void Seed(AssistantModelChoice choice)
    {
        _providerId.Value = choice.Provider.Id;
        _model.Value = choice.Model ?? string.Empty;
    }

    /// <summary>Points the line at a provider. The model field is cleared with it: a model name
    /// means nothing to another provider.</summary>
    public void SetProvider(string providerId)
    {
        var provider = AssistantProviders.Resolve(providerId);
        if (provider.Id == _providerId.Value) return;
        _providerId.Value = provider.Id;
        _model.Value = string.Empty;
    }

    /// <summary>Every provider, marked with the one picked and saying what each has for a key — a
    /// role can be pointed at any of them, but only the ones with a key will answer.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildProviderMenu()
    {
        var current = _providerId.Value;
        var keys = _store.Keys.Value;
        return AssistantProviders.All
            .Select(provider => new RepoBarContextMenu.Item(
                provider.DisplayName,
                () => SetProvider(provider.Id),
                Checked: string.Equals(provider.Id, current, StringComparison.Ordinal),
                Shortcut: AssistantKeyLabels.For(keys.For(provider), _loc.Strings.Value)))
            .ToArray();
    }

    /// <summary>The provider's models, marked with the one in the field. A default rather than a
    /// whitelist: picking fills the field in, and a model typed instead is kept as typed.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildModelMenu()
    {
        var current = _model.Value.Trim();
        return Provider.ModelPresets
            .Select(model => new RepoBarContextMenu.Item(
                model,
                () => _model.Value = model,
                Checked: string.Equals(model, current, StringComparison.Ordinal)))
            .ToArray();
    }

    public void Dispose()
    {
        _hasModelPresets.Dispose();
        _modelHint.Dispose();
        _providerName.Dispose();
        _model.Dispose();
        _providerId.Dispose();
    }
}

/// <summary>The words a menu uses for what a provider has for a key: the trailing slot the menu
/// draws muted, since a provider row carries no gesture.</summary>
internal static class AssistantKeyLabels
{
    public static string? For(AssistantKeyState state, Strings s) => state.Source switch
    {
        AssistantKeySource.Saved => s.AssistantProviderKeySaved,
        AssistantKeySource.Environment => s.AssistantProviderKeyEnvironment,
        AssistantKeySource.NotRequired => null,
        AssistantKeySource.None => s.AssistantProviderKeyMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state.Source, null),
    };

    /// <summary>The role's name as the card labels it.</summary>
    public static string RoleName(AssistantRole role, Strings s) => role switch
    {
        AssistantRole.General => s.AssistantRoleGeneral,
        AssistantRole.CommitMessage => s.AssistantRoleCommitMessage,
        AssistantRole.Review => s.AssistantRoleReview,
        AssistantRole.Walkthrough => s.AssistantRoleWalkthrough,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
