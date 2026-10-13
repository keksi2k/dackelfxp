using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DackelFxp.Core.Connections;
using DackelFxp.Core.Queue;
using DackelFxp.Core.Rules;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;

namespace DackelFxp.App.Services;

/// <summary>Small modal dialogs built in code, plus file pickers. Must be called on the UI thread.</summary>
public sealed class DialogService
{
    public Window? Owner { get; set; }

    public async Task ShowMessageAsync(string title, string message)
    {
        await ShowAsync<bool>(title, message, [("OK", true)]);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? yes = null, string? no = null) =>
        await ShowAsync(title, message, [(yes ?? Lang.T("Ja", "Yes"), true), (no ?? Lang.T("Nein", "No"), false)]);

    public async Task<string?> PromptAsync(string title, string message, string? defaultValue = null, bool password = false)
    {
        var box = new TextBox { Text = defaultValue ?? "", MinWidth = 360 };
        if (password) box.PasswordChar = '•';
        var ok = await ShowAsync(title, message, [("OK", true), (Lang.T("Abbrechen", "Cancel"), false)], box, focus: box);
        return ok ? box.Text : null;
    }

    public async Task<CertificateDecision> AskCertificateAsync(Site site, X509Certificate2 cert, SslPolicyErrors errors)
    {
        var text =
            Lang.T($"Das Zertifikat von {site.DisplayName} ({site.Host}) ist nicht vertrauenswürdig.", $"The certificate of {site.DisplayName} ({site.Host}) is not trusted.") + "\n\n" +
            Lang.T("Fehler", "Errors") + $": {errors}\n" +
            Lang.T("Ausgestellt für", "Issued to") + $": {cert.Subject}\n" +
            Lang.T("Aussteller", "Issuer") + $": {cert.Issuer}\n" +
            Lang.T("Gültig", "Valid") + $": {cert.NotBefore:d} – {cert.NotAfter:d}\n" +
            $"SHA-256: {FormatFingerprint(ConnectionManager.Fingerprint(cert))}";
        return await ShowAsync(Lang.T("Zertifikat prüfen", "Verify certificate"), text,
        [
            (Lang.T("Immer vertrauen", "Always trust"), CertificateDecision.AcceptAlways),
            (Lang.T("Nur diesmal", "Only this time"), CertificateDecision.AcceptOnce),
            (Lang.T("Ablehnen", "Reject"), CertificateDecision.Reject),
        ]);
    }

    private static string FormatFingerprint(string hex) =>
        string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    public async Task<(ExistsDecision? Decision, bool ApplyToAll)> AskFileExistsAsync(QueueItem item, long sourceSize, long targetSize)
    {
        var applyAll = new CheckBox { Content = Lang.T("Für alle weiteren Dateien übernehmen", "Apply to all further files") };
        var text = Lang.T("Die Zieldatei existiert bereits:", "The target file already exists:") + $"\n{item.Target.Path}\n\n" +
                   Lang.T("Quelle", "Source") + $": {Converters.Formats.Bytes(sourceSize)}\n" +
                   Lang.T("Ziel", "Target") + $":   {Converters.Formats.Bytes(targetSize)}";
        var decision = await ShowAsync<ExistsDecision?>(Lang.T("Datei existiert", "File exists"), text,
        [
            (Lang.T("Überschreiben", "Overwrite"), ExistsDecision.Overwrite),
            (Lang.T("Fortsetzen", "Resume"), ExistsDecision.Resume),
            (Lang.T("Umbenennen", "Rename"), ExistsDecision.Rename),
            (Lang.T("Überspringen", "Skip"), ExistsDecision.Skip),
        ], applyAll);
        return (decision, applyAll.IsChecked == true);
    }

    /// <summary>Non-modal window with selectable text (command output, SFV results).</summary>
    public void ShowText(string title, string text)
    {
        var window = new Window
        {
            Title = title,
            Width = 700,
            Height = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"),
                Margin = new Thickness(8),
            },
        };
        if (Owner is not null) window.Show(Owner);
        else window.Show();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is null) return null;
        var result = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenFileAsync(string title, string pattern)
    {
        if (Owner is null) return null;
        var result = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string pattern)
    {
        if (Owner is null) return null;
        var result = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        });
        return result?.TryGetLocalPath();
    }

    public Task ShowDialogAsync(Window window) =>
        Owner is null ? Task.CompletedTask : window.ShowDialog(Owner);

    public Task<T> ShowDialogAsync<T>(Window window) =>
        Owner is null ? Task.FromResult(default(T)!) : window.ShowDialog<T>(Owner);

    private async Task<T> ShowAsync<T>(string title, string message, (string Label, T Value)[] buttons,
        Control? extra = null, Control? focus = null)
    {
        T result = default!;
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 380,
            MaxWidth = 720,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (label, value) = buttons[i];
            var button = new Button { Content = label, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) button.IsDefault = true;
            if (i == buttons.Length - 1 && buttons.Length > 1) button.IsCancel = true;
            button.Click += (_, _) =>
            {
                result = value;
                window.Close();
            };
            buttonPanel.Children.Add(button);
        }

        var content = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        content.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (extra is not null) content.Children.Add(extra);
        content.Children.Add(buttonPanel);
        window.Content = content;
        if (focus is not null) window.Opened += (_, _) => Dispatcher.UIThread.Post(() => focus.Focus());

        if (Owner is { IsVisible: true }) await window.ShowDialog(Owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return result;
    }
}

public sealed class UiCertificatePrompt(DialogService dialogs) : ICertificatePrompt
{
    public Task<CertificateDecision> AskAsync(Site site, X509Certificate2 certificate, SslPolicyErrors errors) =>
        Dispatcher.UIThread.InvokeAsync(() => dialogs.AskCertificateAsync(site, certificate, errors));
}

/// <summary>Asks for existing files; "apply to all" is remembered until <see cref="Reset"/>.</summary>
public sealed class UiExistsPrompt(DialogService dialogs) : IFileExistsPrompt
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ExistsDecision? _remembered;
    private bool _hasRemembered;

    public void Reset() => _hasRemembered = false;

    public async Task<ExistsDecision?> AskAsync(QueueItem item, long sourceSize, long targetSize)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hasRemembered) return _remembered;
            var (decision, all) = await Dispatcher.UIThread.InvokeAsync(() => dialogs.AskFileExistsAsync(item, sourceSize, targetSize));
            if (all)
            {
                _remembered = decision;
                _hasRemembered = true;
            }
            return decision;
        }
        finally
        {
            _lock.Release();
        }
    }
}
