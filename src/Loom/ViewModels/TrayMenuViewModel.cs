using System.Collections.ObjectModel;

namespace Loom.ViewModels;

/// <summary>
/// Callbacks the tray context menu forwards to the tray host. Passed in (instead of the view model
/// touching <c>TrayHost</c> directly) so the menu can be instantiated by previews/harnesses with
/// recording fakes.
/// </summary>
public sealed record TrayMenuActions(
    Action OpenStatusPanel,
    Action Snapshot,
    Action Check,
    Action CheckUpdate,
    Action OpenReport,
    Action OpenSettings,
    Action OpenBackupFolder,
    Action Exit)
{
    /// <summary>No-op action set used by <c>ui render</c> previews.</summary>
    public static TrayMenuActions Preview { get; } = new(
        OpenStatusPanel: static () => { },
        Snapshot: static () => { },
        Check: static () => { },
        CheckUpdate: static () => { },
        OpenReport: static () => { },
        OpenSettings: static () => { },
        OpenBackupFolder: static () => { },
        Exit: static () => { });
}

/// <summary>One row of the tray context menu (or a separator when <see cref="IsSeparator"/>).</summary>
public sealed class TrayMenuItem : ObservableObject
{
    private bool _isEnabled = true;

    public string Glyph { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsSeparator { get; init; }
    public bool IsDanger { get; init; }
    public Action? Action { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public bool CanInvoke => !IsSeparator && IsEnabled && Action is not null;
}

/// <summary>
/// View model for the WPF-drawn tray context menu. It follows the Loom app theme (unlike Windows'
/// native menus) and disables the two potentially long-running actions while Loom is busy.
/// </summary>
public sealed class TrayMenuViewModel : ObservableObject
{
    private readonly TrayMenuItem _snapshotItem;
    private readonly TrayMenuItem _checkItem;
    private readonly TrayMenuItem _exitItem;
    private bool _busy;

    public TrayMenuViewModel(TrayMenuActions actions)
    {
        _snapshotItem = new TrayMenuItem
        {
            Text = UiStrings.MenuSnapshot,
            Glyph = "\uE722",
            Action = actions.Snapshot,
        };
        _checkItem = new TrayMenuItem
        {
            Text = UiStrings.MenuCheck,
            Glyph = "\uE895",
            Action = actions.Check,
        };
        _exitItem = new TrayMenuItem
        {
            Text = UiStrings.MenuExit,
            Glyph = "\uE7E8",
            IsDanger = true,
            Action = actions.Exit,
        };

        Items = new ObservableCollection<TrayMenuItem>
        {
            new()
            {
                Text = UiStrings.MenuPopup,
                Glyph = "\uE946",
                Action = actions.OpenStatusPanel,
            },
            Separator(),
            _snapshotItem,
            _checkItem,
            new()
            {
                Text = UiStrings.MenuUpdateCheck,
                Glyph = "\uE896",
                Action = actions.CheckUpdate,
            },
            new()
            {
                Text = UiStrings.MenuReport,
                Glyph = "\uE7C3",
                Action = actions.OpenReport,
            },
            Separator(),
            new()
            {
                Text = UiStrings.MenuSettings,
                Glyph = "\uE713",
                Action = actions.OpenSettings,
            },
            new()
            {
                Text = UiStrings.MenuBackupFolder,
                Glyph = "\uE8DA",
                Action = actions.OpenBackupFolder,
            },
            Separator(),
            _exitItem,
        };
    }

    public ObservableCollection<TrayMenuItem> Items { get; }

    public bool IsBusy => _busy;

    /// <summary>Narrows the menu when a snapshot/check is already running.</summary>
    public void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        _snapshotItem.IsEnabled = !busy;
        _checkItem.IsEnabled = !busy;
        OnPropertyChanged(nameof(IsBusy));
    }

    /// <summary>Menu used by <c>ui render</c> (all actions enabled, no side effects).</summary>
    public static TrayMenuViewModel CreatePreview() => new(TrayMenuActions.Preview);

    private static TrayMenuItem Separator() => new() { IsSeparator = true };
}
