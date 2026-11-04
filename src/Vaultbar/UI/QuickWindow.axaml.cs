using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Vaultbar.Platform;
using Vaultbar.Vault;

namespace Vaultbar.UI;

public partial class QuickWindow : Window
{
    private enum Mode
    {
        Message,
        Locked,
        Busy,
        Search,
        Details,
        Reprompt,
        Help,
    }

    private static readonly KeyModifiers Primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private readonly AppController _app = null!;
    private readonly ObservableCollection<object> _rows = new();
    private readonly ObservableCollection<DetailRow> _details = new();
    private readonly HashSet<string> _repromptPassed = new();
    private readonly DispatcherTimer _totpTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _footerTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private ForeignWindow? _target;
    private Mode _mode;
    private Mode _modeBeforeReprompt;
    private Mode _modeBeforeHelp;
    private Func<Task>? _afterReprompt;
    private VaultItem? _repromptItem;
    private VaultItem? _detailItem;
    private int _selected = -1;
    private int _detailSelected;
    private long _shownAt;
    private bool _typing;

    public QuickWindow()
    {
        InitializeComponent();
    }

    public QuickWindow(AppController app) : this()
    {
        _app = app;
        Results.ItemsSource = _rows;
        Details.ItemsSource = _details;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        SearchBox.TextChanged += (_, _) =>
        {
            if (_mode == Mode.Search) Refresh();
        };
        Results.AddHandler(PointerPressedEvent, OnResultPointerPressed, RoutingStrategies.Bubble);
        Details.AddHandler(PointerPressedEvent, OnDetailPointerPressed, RoutingStrategies.Bubble);
        Deactivated += (_, _) =>
        {
            if (_typing || _mode == Mode.Busy || Environment.TickCount64 - _shownAt < 400) return;
            Dismiss();
        };
        _totpTimer.Tick += (_, _) =>
        {
            foreach (var row in _details.Where(r => r.IsTotp)) row.Refresh();
        };
        _footerTimer.Tick += (_, _) =>
        {
            _footerTimer.Stop();
            UpdateFooter();
        };
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.None && Frame.Margin != default)
            {
                Frame.Margin = new Thickness(0);
                Frame.CornerRadius = new CornerRadius(0);
                Width -= 32;
            }
        };
    }

    public void Present(ForeignWindow? target)
    {
        _target = target;
        _repromptPassed.Clear();
        _afterReprompt = null;
        SearchBox.Text = "";
        PasswordBox.Text = "";
        CloseDetails();
        UpdateMode();
        PlaceOnScreen(target);
        Show();
        if (_app.Config.HideFromScreenCapture) _app.Platform.ExcludeFromCapture(this);
        Activate();
        _app.Platform.AfterShow();
        _shownAt = Environment.TickCount64;
        _app.Vault.Touch();
        Dispatcher.UIThread.Post(FocusInput, DispatcherPriority.Input);
        if (_mode == Mode.Locked && _app.Vault.CanQuickUnlock)
            Dispatcher.UIThread.Post(() => _ = QuickUnlockAsync(), DispatcherPriority.Background);
    }

    public void Dismiss()
    {
        Hide();
        SearchBox.Text = "";
        PasswordBox.Text = "";
        CloseDetails();
        _repromptPassed.Clear();
        _afterReprompt = null;
        _rows.Clear();
    }

    public void OnVaultChanged()
    {
        if (!IsVisible) return;
        if (_mode is Mode.Search or Mode.Details or Mode.Reprompt or Mode.Help && _app.Vault.State == VaultState.Unlocked)
        {
            if (_mode == Mode.Search) Refresh();
            UpdateFooter();
            return;
        }
        if (_mode != Mode.Busy) UpdateMode();
    }

    private void PlaceOnScreen(ForeignWindow? target)
    {
        var anchor = _app.Platform.AnchorPoint(target);
        var screen = (anchor is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var width = (int)(Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (int)(area.Height * 0.18));
    }

    private void UpdateMode()
    {
        var vault = _app.Vault;
        switch (vault.State)
        {
            case VaultState.Unlocked:
                SetMode(Mode.Search);
                Refresh();
                break;
            case VaultState.Locked:
                SetMode(Mode.Locked);
                ShowMessage($"Unlock {vault.Email}", vault.CanQuickUnlock
                    ? $"Use {vault.QuickUnlock!.Name}, or enter your Bitwarden master password."
                    : "Enter your Bitwarden master password. Vaultbar reads the vault that the Bitwarden desktop app keeps on this device.");
                break;
            case VaultState.Error:
                SetMode(Mode.Message);
                ShowMessage("Cannot read your vault", vault.Error ?? "Unknown error");
                break;
            default:
                SetMode(Mode.Message);
                ShowMessage("Bitwarden desktop not found",
                    "Vaultbar works on top of the Bitwarden desktop app. Install it, log in once, and keep it syncing. Looked in:\n"
                    + string.Join("\n", DesktopStore.CandidatePaths()));
                break;
        }
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        var passwordInput = mode is Mode.Locked or Mode.Reprompt || mode == Mode.Busy && PasswordBox.IsVisible;
        SearchBox.IsVisible = !passwordInput;
        PasswordBox.IsVisible = passwordInput;
        SearchBox.IsReadOnly = mode != Mode.Search;
        PasswordBox.IsReadOnly = mode == Mode.Busy;
        ResultsScroll.IsVisible = mode == Mode.Search && _rows.Count > 0;
        DetailsPanel.IsVisible = mode == Mode.Details;
        HelpPanel.IsVisible = mode == Mode.Help;
        MessagePanel.IsVisible = mode is Mode.Message or Mode.Locked or Mode.Reprompt or Mode.Busy
                                 || mode == Mode.Search && _rows.Count == 0;
        HeaderIcon.Data = ResultRow.Resource(passwordInput || mode == Mode.Message ? "IconLock" : "IconSearch");
        if (mode != Mode.Busy) MessageError.IsVisible = false;
        PasswordBox.Watermark = "Master password";
        if (mode == Mode.Details) _totpTimer.Start();
        else _totpTimer.Stop();
        UpdateHints();
        UpdateFooter();
    }

    private void FocusInput()
    {
        if (PasswordBox.IsVisible) PasswordBox.Focus();
        else SearchBox.Focus();
    }

    private void ShowMessage(string title, string body)
    {
        MessageTitle.Text = title;
        MessageBody.Text = body;
        MessageBody.IsVisible = body.Length > 0;
    }

    private void ShowError(string error)
    {
        MessageError.Text = error;
        MessageError.IsVisible = true;
    }

    private void Refresh()
    {
        var vault = _app.Vault;
        var query = SearchBox.Text ?? "";
        _rows.Clear();
        var items = vault.Items;

        if (string.IsNullOrWhiteSpace(query))
        {
            var seen = new HashSet<string>();
            void Section(string title, IEnumerable<VaultItem> source, int max)
            {
                var list = new List<VaultItem>();
                foreach (var item in source)
                {
                    if (list.Count >= max) break;
                    if (seen.Add(item.Id)) list.Add(item);
                }
                if (list.Count == 0) return;
                _rows.Add(new HeaderRow(title));
                foreach (var item in list) _rows.Add(MakeRow(item));
            }

            var context = string.IsNullOrEmpty(_target?.App) ? "" : $" for {_target!.App}";
            Section($"SUGGESTED{context.ToUpperInvariant()}", Matcher.Suggest(items, _target, _app.Usage), 4);
            Section("FAVORITES", items.Where(i => i.Favorite).OrderByDescending(i => _app.Usage.Boost(i.Id)), 6);
            Section("RECENTLY USED", _app.Usage.Recent(items), 6);
            if (_rows.Count == 0)
                ShowMessage($"Search {items.Count} items", "Type a name, username or website.");
        }
        else
        {
            foreach (var item in Matcher.Search(items, query, _app.Usage)) _rows.Add(MakeRow(item));
            if (_rows.Count == 0) ShowMessage($"No items match “{query.Trim()}”", "");
        }

        ResultsScroll.IsVisible = _rows.Count > 0;
        MessagePanel.IsVisible = _rows.Count == 0;
        MessageError.IsVisible = false;
        _selected = -1;
        Select(NextResult(-1, 1));
        ResultsScroll.Offset = default;
    }

    private ResultRow MakeRow(VaultItem item)
    {
        var row = new ResultRow(item);
        if (_app.Config.ShowFavicons && _app.Vault.ShowFavicons && item.Type == ItemType.Login && item.IconHost is { } host)
            _ = LoadIconAsync(row, host);
        return row;
    }

    private async Task LoadIconAsync(ResultRow row, string host)
    {
        var icon = await _app.Icons.GetAsync(host);
        if (icon != null) row.Icon = icon;
    }

    private VaultItem? SelectedItem => _selected >= 0 && _selected < _rows.Count && _rows[_selected] is ResultRow r ? r.Item : null;

    private int NextResult(int from, int direction)
    {
        for (var i = from + direction; i >= 0 && i < _rows.Count; i += direction)
            if (_rows[i] is ResultRow) return i;
        return -1;
    }

    private void Select(int index)
    {
        if (index < 0) return;
        if (_selected >= 0 && _selected < _rows.Count && _rows[_selected] is ResultRow old) old.IsSelected = false;
        _selected = index;
        if (_rows[index] is ResultRow row) row.IsSelected = true;
        var target = index == NextResult(-1, 1) ? 0 : index;
        Dispatcher.UIThread.Post(() => Results.ContainerFromIndex(target)?.BringIntoView(), DispatcherPriority.Loaded);
        UpdateHints();
    }

    private void Move(int steps)
    {
        var direction = Math.Sign(steps);
        var index = _selected;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            var next = NextResult(index, direction);
            if (next < 0) break;
            index = next;
        }
        Select(index);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        _app.Vault.Touch();
        var mods = e.KeyModifiers;
        var primary = mods.HasFlag(Primary);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);

        if (e.Key == Key.Escape && _mode is not (Mode.Details or Mode.Reprompt or Mode.Search or Mode.Help))
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F1 || e.Key == Key.OemQuestion && primary) && _mode is Mode.Search or Mode.Details or Mode.Help)
        {
            if (_mode == Mode.Help) RestoreMode(_modeBeforeHelp);
            else ShowHelp();
            e.Handled = true;
            return;
        }

        switch (_mode)
        {
            case Mode.Help:
                if (e.Key is Key.Escape or Key.Left or Key.Back or Key.Enter) RestoreMode(_modeBeforeHelp);
                e.Handled = true;
                break;
            case Mode.Busy:
                e.Handled = true;
                break;
            case Mode.Message:
                if (e.Key == Key.Enter) Dismiss();
                break;
            case Mode.Locked:
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _ = UnlockAsync();
                }
                else if (e.Key == Key.H && primary && _app.Vault.CanQuickUnlock)
                {
                    e.Handled = true;
                    _ = QuickUnlockAsync();
                }
                break;
            case Mode.Reprompt:
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _repromptItem = null;
                    _afterReprompt = null;
                    PasswordBox.Text = "";
                    RestoreMode(_modeBeforeReprompt);
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _ = RepromptAsync();
                }
                break;
            case Mode.Search:
                e.Handled = HandleSearchKey(e.Key, primary, shift, alt);
                break;
            case Mode.Details:
                e.Handled = HandleDetailsKey(e.Key, primary, shift, alt);
                break;
        }
    }

    private bool HandleSearchKey(Key key, bool primary, bool shift, bool alt)
    {
        var item = SelectedItem;
        switch (key)
        {
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
                else Dismiss();
                return true;
            case Key.Down:
                Move(1);
                return true;
            case Key.Up:
                Move(-1);
                return true;
            case Key.PageDown:
                Move(6);
                return true;
            case Key.PageUp:
                Move(-6);
                return true;
            case Key.Tab:
            case Key.Right when SearchBox.CaretIndex >= (SearchBox.Text?.Length ?? 0) && SearchBox.SelectionStart == SearchBox.SelectionEnd:
                if (item != null) _ = OpenDetailsAsync(item);
                return true;
            case Key.Enter when item != null:
                if (primary) OpenUrl(item);
                else if (shift) _ = FillAsync(item, passwordOnly: true);
                else _ = DefaultActionAsync(item);
                return true;
            case Key.C when primary && item != null && SearchBox.SelectionStart == SearchBox.SelectionEnd:
                _ = CopyAsync(item, alt ? item.TotpCopy : shift ? item.SecondaryCopy : item.PrimaryCopy);
                return true;
            case Key.U when primary && shift && item != null:
                _ = CopyAsync(item, item.WebsiteCopy);
                return true;
            case Key.U when primary && item != null:
                _ = CopyAsync(item, item.Type == ItemType.Login ? item.SecondaryCopy : null);
                return true;
            case Key.P when primary && item != null:
                _ = CopyAsync(item, item.Type == ItemType.Login ? item.PrimaryCopy : null);
                return true;
            case Key.T when primary && item != null:
                _ = CopyAsync(item, item.TotpCopy);
                return true;
            case Key.O when primary && shift && item != null:
                OpenInWebVault(item);
                return true;
            case Key.O when primary && item != null:
                OpenUrl(item);
                return true;
            case Key.L when primary:
                _app.Lock();
                return true;
            case Key.W when primary:
                Dismiss();
                return true;
            default:
                return false;
        }
    }

    private bool HandleDetailsKey(Key key, bool primary, bool shift, bool alt)
    {
        var row = _detailSelected >= 0 && _detailSelected < _details.Count ? _details[_detailSelected] : null;
        switch (key)
        {
            case Key.Escape or Key.Left or Key.Back:
                CloseDetails();
                RestoreMode(Mode.Search);
                return true;
            case Key.Down:
                SelectDetail(Math.Min(_detailSelected + 1, _details.Count - 1));
                return true;
            case Key.Up:
                SelectDetail(Math.Max(_detailSelected - 1, 0));
                return true;
            case Key.Space when row is { IsSecret: true }:
                row.Revealed = !row.Revealed;
                return true;
            case Key.Enter when row != null && primary && row.Field.Kind == FieldKind.Url:
                if (VaultItem.WebUrl(row.Field.Value()) is { } url)
                {
                    _app.Platform.OpenUrl(url);
                    Dismiss();
                }
                else
                {
                    Toast("Only web addresses can be opened");
                }
                return true;
            case Key.Enter when row != null:
            case Key.C when primary && !shift && !alt && row != null:
                _ = CopyFieldAsync(row);
                return true;
            case Key.C when primary && _detailItem != null:
                _ = CopyAsync(_detailItem, alt ? _detailItem.TotpCopy : _detailItem.SecondaryCopy);
                return true;
            case Key.U when primary && shift && _detailItem != null:
                _ = CopyAsync(_detailItem, _detailItem.WebsiteCopy);
                return true;
            case Key.O when primary && shift && _detailItem != null:
                OpenInWebVault(_detailItem);
                return true;
            case Key.O when primary && _detailItem != null:
                OpenUrl(_detailItem);
                return true;
            case Key.L when primary:
                _app.Lock();
                return true;
            default:
                return true;
        }
    }

    private async Task DefaultActionAsync(VaultItem item)
    {
        switch (item.Type)
        {
            case ItemType.Login:
                await FillAsync(item, passwordOnly: false);
                break;
            case ItemType.SecureNote:
                await CopyAsync(item, item.PrimaryCopy);
                break;
            default:
                await OpenDetailsAsync(item);
                break;
        }
    }

    private async Task RunGuardedAsync(VaultItem item, bool sensitive, Func<Task> action)
    {
        if (!sensitive || !item.Reprompt || _repromptPassed.Contains(item.Id))
        {
            await action();
            return;
        }

        if (_app.Vault.CanQuickUnlock)
        {
            var before = _mode;
            SetMode(Mode.Busy);
            ShowMessage($"“{item.Name}” is protected", $"Confirm with {_app.Vault.QuickUnlock!.Name}.");
            var status = await _app.Vault.VerifyWithQuickUnlockAsync();
            BringBack();
            RestoreMode(before);
            if (status == QuickUnlockStatus.Success)
            {
                _repromptPassed.Add(item.Id);
                await action();
                return;
            }
        }

        _repromptItem = item;
        _afterReprompt = action;
        _modeBeforeReprompt = _mode;
        PasswordBox.Text = "";
        SetMode(Mode.Reprompt);
        ShowMessage($"“{item.Name}” is protected", "Re-enter your master password to use this item.");
        FocusInput();
    }

    private Task CopyAsync(VaultItem item, CopyAction? action)
    {
        if (action is null)
        {
            Toast("Nothing to copy for this item");
            return Task.CompletedTask;
        }
        return RunGuardedAsync(item, action.Sensitive, async () =>
        {
            string? value;
            try
            {
                value = action.Value();
            }
            catch (Exception ex)
            {
                Log.Write($"copy: {ex.Message}");
                Toast($"Could not read {action.Label.ToLowerInvariant()}");
                return;
            }
            if (string.IsNullOrEmpty(value))
            {
                Toast($"{action.Label} is empty");
                return;
            }

            _app.Usage.Record(item.Id);
            var clearing = _app.CopyAsync(value, action.Sensitive);
            var seconds = _app.Config.ClipboardClearSeconds;
            Toast(action.Sensitive && seconds > 0 ? $"{action.Label} copied · clears in {seconds}s" : $"{action.Label} copied");
            if (_app.Config.HideAfterCopy) DismissSoon();
            await clearing;
        });
    }

    private Task CopyFieldAsync(DetailRow row)
    {
        var item = _detailItem;
        if (item is null) return Task.CompletedTask;
        var sensitive = row.Field.Kind is FieldKind.Secret or FieldKind.Totp or FieldKind.Multiline;
        return CopyAsync(item, new CopyAction(row.Label, row.Field.Value, sensitive));
    }

    private Task FillAsync(VaultItem item, bool passwordOnly)
    {
        var unavailable = _app.Platform.AutoTypeUnavailableReason(_target);
        if (unavailable != null)
        {
            return RunGuardedAsync(item, true, async () =>
            {
                if (item.Password is { Length: > 0 } pw)
                {
                    var clearing = _app.CopyAsync(pw, true);
                    _app.Usage.Record(item.Id);
                    Toast($"Can't auto-type ({unavailable}) · password copied");
                    await clearing;
                }
                else
                {
                    Toast($"Can't auto-type ({unavailable})");
                }
            });
        }

        return RunGuardedAsync(item, true, async () =>
        {
            var template = passwordOnly ? "{PASSWORD}" : _app.Config.AutotypeSequence;
            var steps = AutoTypeSequence.Build(template, item.Username, item.Password, item.CurrentTotp);
            _app.Usage.Record(item.Id);
            _typing = true;
            bool ok;
            try
            {
                ok = await _app.Platform.AutoTypeAsync(_target, steps, Hide);
            }
            catch (Exception ex)
            {
                Log.Write($"autotype: {ex}");
                ok = false;
            }
            finally
            {
                _typing = false;
            }

            if (!ok)
            {
                var target = _target;
                Present(target);
                if (item.Password is { Length: > 0 } pw) _ = _app.CopyAsync(pw, true);
                Toast("Couldn't switch back to the previous window · password copied");
                return;
            }

            Dismiss();
            if (_app.Config.CopyTotpAfterFill && item.HasTotp && !template.Contains("{TOTP}", StringComparison.OrdinalIgnoreCase)
                && item.CurrentTotp() is { } code)
                await _app.CopyAsync(code, true);
        });
    }

    private void OpenUrl(VaultItem item)
    {
        if (item.LaunchUrl is not { } url)
        {
            Toast("This item has no website");
            return;
        }
        _app.Usage.Record(item.Id);
        _app.Platform.OpenUrl(url);
        Dismiss();
    }

    private void OpenInWebVault(VaultItem item)
    {
        if (VaultItem.WebUrl(_app.Vault.WebVaultUrl) is not { } baseUrl)
        {
            Toast("No web vault address configured");
            return;
        }
        _app.Platform.OpenUrl($"{baseUrl.TrimEnd('/')}/#/vault?itemId={Uri.EscapeDataString(item.Id)}&action=view");
        Dismiss();
    }

    private void RestoreMode(Mode mode)
    {
        SetMode(mode);
        ResultsScroll.IsVisible = mode == Mode.Search && _rows.Count > 0;
        MessagePanel.IsVisible = mode == Mode.Search && _rows.Count == 0;
        FocusInput();
    }

    private void BringBack()
    {
        if (!IsVisible) return;
        Activate();
        _app.Platform.AfterShow();
    }

    private void ShowHelp()
    {
        _modeBeforeHelp = _mode;
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
        var opt = OperatingSystem.IsMacOS() ? "⌥" : "Alt";
        var shift = OperatingSystem.IsMacOS() ? "⇧" : "Shift";
        (string[] Keys, string Label)[] rows =
        [
            (["Enter"], "Fill login · copy note · open details"),
            ([shift, "Enter"], "Fill password only"),
            ([mod, "C"], "Copy password, card number or note"),
            ([mod, shift, "C"], "Copy username or security code"),
            ([mod, opt, "C"], "Copy one-time code"),
            ([mod, shift, "U"], "Copy website"),
            ([mod, "O"], "Open website"),
            ([mod, shift, "O"], "Open in Bitwarden web vault"),
            (["Tab"], "Item details"),
            (["Space"], "Reveal hidden field (details)"),
            ([mod, "L"], "Lock"),
            (["Esc"], "Back, clear search, close"),
            (["F1"], "Keyboard shortcuts"),
        ];

        HelpList.Children.Clear();
        foreach (var (keys, label) in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2) };
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Width = 150 };
            foreach (var k in keys)
                group.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = k } });
            line.Children.Add(group);
            line.Children.Add(new TextBlock { Text = label, Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center });
            HelpList.Children.Add(line);
        }
        SetMode(Mode.Help);
        MessagePanel.IsVisible = false;
    }

    private async Task QuickUnlockAsync()
    {
        if (_mode != Mode.Locked || _app.Vault.QuickUnlock is not { } provider) return;
        SetMode(Mode.Busy);
        PasswordBox.IsVisible = false;
        SearchBox.IsVisible = true;
        ShowMessage($"Unlock {_app.Vault.Email}", $"Waiting for {provider.Name}…");
        var (_, error) = await _app.Vault.QuickUnlockAsync();
        BringBack();
        UpdateMode();
        if (error != null) ShowError(error);
        FocusInput();
    }

    private async Task OfferQuickUnlockAsync()
    {
        if (_app.Vault.QuickUnlock is not { } provider || !await _app.Vault.ShouldOfferQuickUnlockAsync()) return;
        SetMode(Mode.Busy);
        ShowMessage($"Set up {provider.Name}", $"Confirm with {provider.Name} to unlock Vaultbar without your master password from now on.");
        var status = await _app.Vault.SetUpQuickUnlockAsync();
        BringBack();
        UpdateMode();
        FocusInput();
        Toast(status switch
        {
            QuickUnlockStatus.Success => $"{provider.Name} is set up",
            QuickUnlockStatus.Canceled => $"{provider.Name} skipped · turn it on from the tray menu",
            _ => $"Could not set up {provider.Name}",
        });
    }

    private Task OpenDetailsAsync(VaultItem item) => RunGuardedAsync(item, item.Reprompt, () =>
    {
        _detailItem = item;
        _details.Clear();
        foreach (var field in item.GetFields()) _details.Add(new DetailRow(field));
        DetailTitle.Text = item.Name;
        DetailSubtitle.Text = string.Join(" · ", new[] { item.Subtitle, item.Folder }.Where(s => !string.IsNullOrEmpty(s)));
        _detailSelected = -1;
        SetMode(Mode.Details);
        SelectDetail(0);
        DetailsScroll.Offset = default;
        return Task.CompletedTask;
    });

    private void CloseDetails()
    {
        _detailItem = null;
        _details.Clear();
        _totpTimer.Stop();
    }

    private void SelectDetail(int index)
    {
        if (index < 0 || index >= _details.Count) return;
        if (_detailSelected >= 0 && _detailSelected < _details.Count) _details[_detailSelected].IsSelected = false;
        _detailSelected = index;
        _details[index].IsSelected = true;
        Dispatcher.UIThread.Post(() => Details.ContainerFromIndex(index)?.BringIntoView(), DispatcherPriority.Loaded);
        UpdateHints();
    }

    private async Task UnlockAsync()
    {
        var password = PasswordBox.Text ?? "";
        if (password.Length == 0) return;
        SetMode(Mode.Busy);
        ShowMessage("Unlocking…", "");
        var error = await _app.Vault.UnlockAsync(password);
        PasswordBox.Text = "";
        if (error != null)
        {
            UpdateMode();
            ShowError(error);
            FocusInput();
            return;
        }
        UpdateMode();
        FocusInput();
        await OfferQuickUnlockAsync();
    }

    private async Task RepromptAsync()
    {
        var password = PasswordBox.Text ?? "";
        if (password.Length == 0 || _repromptItem is null) return;
        var item = _repromptItem;
        SetMode(Mode.Busy);
        var ok = await _app.Vault.VerifyMasterPasswordAsync(password);
        PasswordBox.Text = "";
        if (!ok)
        {
            SetMode(Mode.Reprompt);
            ShowError("Invalid master password.");
            FocusInput();
            return;
        }

        _repromptPassed.Add(item.Id);
        _repromptItem = null;
        var action = _afterReprompt;
        _afterReprompt = null;
        RestoreMode(_modeBeforeReprompt);
        if (action != null) await action();
    }

    private void DismissSoon()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsVisible) Dismiss();
        };
        timer.Start();
    }

    private void Toast(string text)
    {
        FooterText.Text = text;
        FooterText.IsVisible = true;
        Hints.IsVisible = false;
        _footerTimer.Stop();
        _footerTimer.Start();
    }

    private void UpdateFooter()
    {
        if (!_footerTimer.IsEnabled)
        {
            FooterText.IsVisible = false;
            Hints.IsVisible = true;
        }
        var vault = _app.Vault;
        HeaderStatus.Text = _mode is Mode.Search or Mode.Details && vault.State == VaultState.Unlocked
            ? $"{vault.Items.Count} items{(vault.LastSync is { } s ? " · synced " + Ago(s) : "")}"
            : _app.HotkeyError != null && _mode == Mode.Locked ? "Global hotkey unavailable" : "";
        ToolTip.SetTip(HeaderStatus, _app.HotkeyError);
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} d ago";
    }

    private void UpdateHints()
    {
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
        var opt = OperatingSystem.IsMacOS() ? "⌥" : "Alt";
        var shift = OperatingSystem.IsMacOS() ? "⇧" : "Shift";
        var hints = new List<(string[] Keys, string Label)>();
        var item = SelectedItem;

        switch (_mode)
        {
            case Mode.Locked:
                hints.Add((["Enter"], "Unlock"));
                if (_app.Vault.CanQuickUnlock) hints.Add(([mod, "H"], _app.Vault.QuickUnlock!.Name));
                hints.Add((["Esc"], "Close"));
                break;
            case Mode.Help:
                hints.Add((["Esc"], "Back"));
                break;
            case Mode.Reprompt:
                hints.Add((["Enter"], "Confirm"));
                hints.Add((["Esc"], "Cancel"));
                break;
            case Mode.Search when item != null:
                switch (item.Type)
                {
                    case ItemType.Login:
                        hints.Add((["Enter"], "Fill"));
                        if (item.HasPassword) hints.Add(([mod, "C"], "Password"));
                        if (item.Username.Length > 0) hints.Add(([mod, shift, "C"], "Username"));
                        if (item.HasTotp) hints.Add(([mod, opt, "C"], "Code"));
                        break;
                    case ItemType.SecureNote:
                        hints.Add((["Enter"], "Copy note"));
                        break;
                    default:
                        hints.Add((["Enter"], "Details"));
                        if (item.PrimaryCopy is { } p) hints.Add(([mod, "C"], p.Label));
                        break;
                }
                hints.Add((["Tab"], "Details"));
                break;
            case Mode.Search:
                hints.Add((["F1"], "Shortcuts"));
                hints.Add((["Esc"], "Close"));
                break;
            case Mode.Details:
                hints.Add((["Enter"], "Copy"));
                if (_detailSelected >= 0 && _detailSelected < _details.Count && _details[_detailSelected].IsSecret)
                    hints.Add((["Space"], "Reveal"));
                hints.Add((["Esc"], "Back"));
                break;
            case Mode.Message:
                hints.Add((["Esc"], "Close"));
                break;
        }

        Hints.Children.Clear();
        foreach (var (keys, label) in hints)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var k in keys)
                group.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = k } });
            group.Children.Add(new TextBlock { Text = label, Classes = { "hint" }, Margin = new Thickness(4, 0, 0, 0) });
            Hints.Children.Add(group);
        }
    }

    private void OnResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_mode != Mode.Search || e.Source is not StyledElement { DataContext: ResultRow row }) return;
        var index = _rows.IndexOf(row);
        if (index < 0) return;
        Select(index);
        if (e.ClickCount >= 2) _ = DefaultActionAsync(row.Item);
        e.Handled = true;
    }

    private void OnDetailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_mode != Mode.Details || e.Source is not StyledElement { DataContext: DetailRow row }) return;
        SelectDetail(_details.IndexOf(row));
        if (e.ClickCount >= 2) _ = CopyFieldAsync(row);
        e.Handled = true;
    }
}
