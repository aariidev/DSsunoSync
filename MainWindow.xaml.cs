using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Reflection;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ArideevSunoPresence;

public partial class MainWindow : Window
{
    private const string StartupValueName = "Arideev Suno Presence";
    private const string StartInBackgroundArgument = "--background";
    private readonly bool _startInBackground = Environment.GetCommandLineArgs()
        .Skip(1).Any(argument => string.Equals(argument, StartInBackgroundArgument, StringComparison.OrdinalIgnoreCase));
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LocalBridge? _bridge;
    private readonly PresenceCoordinator _presence = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private string? _pendingUpdateInstaller;
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(7) };
    private string? _lastCoverUrl;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayAppIcon;
    private Forms.ToolStripMenuItem? _trayPresenceItem;
    private sealed class BrowserTrackState
    {
        public required TrackInfo Track { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset? StopRequestedAt { get; set; }
    }
    private readonly Dictionary<string, BrowserTrackState> _tracksBySource = new();
    private TrackInfo? _track;
    private string? _selectedSourceId;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastExtensionReport;
    private bool _reallyExit;
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        InitializeTray();
        if (_startInBackground)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }
        DiscordStatus.Text = "Discord: pendiente de prueba";
        BrowserStatus.Text = "Extensión: abre Suno y recarga la pestaña";
        BrowserDiagnostics.Text = "Diagnóstico: esperando datos de Media Session y del reproductor.";
        _presence.StatusChanged += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            DiscordStatus.Text = $"Discord: {message}";
            LogText.Text = message;
        }));
        _uiTimer.Tick += (_, _) => RefreshPlaybackUi();
        _retryTimer.Tick += async (_, _) => await RetryDiscordAsync();
        _uiTimer.Start();
        _retryTimer.Start();
        Loaded += async (_, _) =>
        {
            if (_startInBackground) Hide();
            await StartBridgeAsync();
            _ = CheckForUpdatesAsync(_updateCheckCancellation.Token);
        };
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arideev", "SunoPresence", "settings.json");
    // The extension is embedded in the EXE and extracted to a writable per-user directory on first launch.
    private string ExtensionPath => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "BrowserExtension");

    private void EnsureExtensionFiles()
    {
        Directory.CreateDirectory(ExtensionPath);
        var assembly = Assembly.GetExecutingAssembly();
        var extensionFiles = new[] { "manifest.json", "page-bridge.js", "content.js", "background.js", "popup.html", "popup.js" };
        foreach (var file in extensionFiles)
        {
            var resourceName = $"ArideevSunoPresence.BrowserExtension.{file}";
            using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"No se encontró el recurso integrado {resourceName}.");
            var target = Path.Combine(ExtensionPath, file);
            using var output = File.Create(target);
            resource.CopyTo(output);
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath)) _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { _settings = new(); }
        ClientIdBox.Text = _settings.DiscordApplicationId;
        PresenceEnabled.IsChecked = _settings.Enabled;
        StartWithWindows.IsChecked = _settings.StartWithWindows;
    }

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir aarideev | SunoSync", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayPresenceItem = new Forms.ToolStripMenuItem("Rich Presence activado", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            PresenceEnabled.IsChecked = !(PresenceEnabled.IsChecked == true);
            _settings.Enabled = PresenceEnabled.IsChecked == true;
            SaveSettings();
            _ = _presence.ConfigureAsync(_settings.DiscordApplicationId, _settings.Enabled);
            UpdateTrayLabel();
        }))) { Checked = true, CheckOnClick = false };
        menu.Items.Add(_trayPresenceItem);
        menu.Items.Add("Salir", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
        var executablePath = Environment.ProcessPath;
        _trayAppIcon = !string.IsNullOrWhiteSpace(executablePath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
            : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayAppIcon,
            Text = "aarideev | SunoSync",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        UpdateTrayLabel();
    }

    private void UpdateTrayLabel()
    {
        if (_trayPresenceItem is null) return;
        var enabled = _settings.Enabled;
        _trayPresenceItem.Checked = enabled;
        _trayPresenceItem.Text = enabled ? "Rich Presence activado" : "Rich Presence desactivado";
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray?.ShowBalloonTip(1800, "aarideev | SunoSync", "La aplicación sigue activa en la bandeja del sistema.", Forms.ToolTipIcon.Info);
            return;
        }
        base.OnClosing(e);
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            EnsureExtensionFiles();
            _bridge = new LocalBridge(47831, OnTrackReceived, message => Dispatcher.BeginInvoke(new Action(() =>
            {
                LogText.Text = message;
                if (!_lastExtensionReport.HasValue || DateTimeOffset.UtcNow - _lastExtensionReport.Value > TimeSpan.FromSeconds(4))
                    BrowserStatus.Text = message;
            })));
            await _bridge.StartAsync();
            ConnectionText.Text = "Conector local activo";
            BrowserStatus.Text = "Navegador: conector escuchando (Chrome + Edge)";
            StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
            LogText.Text = "Esperando el primer mensaje de la extensión";
            BrowserStatus.Text = "Extensión: abre su popup y pulsa «Conectar con Arideev» para autorizar el puente local.";
            await _presence.ConfigureAsync(_settings.DiscordApplicationId, _settings.Enabled);
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "Error del conector local";
            BrowserStatus.Text = "Navegador: no disponible";
            LogText.Text = ex.Message;
        }
    }

    private void OnTrackReceived(TrackInfo track)
    {
        if (_reallyExit) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!_reallyExit) await HandleTrackAsync(track);
        }));
    }

    private async Task HandleTrackAsync(TrackInfo incoming)
    {
        var next = incoming.Sanitized();
        var sourceId = string.IsNullOrWhiteSpace(next.SourceId) ? "unknown" : next.SourceId;
        var now = DateTimeOffset.UtcNow;
        _lastExtensionReport = now;

        BrowserDiagnostics.Text = string.IsNullOrWhiteSpace(next.DiagnosticMessage)
            ? $"MediaSession={next.MediaSessionState}; audio={next.AudioElementCount}; vídeo={next.VideoElementCount}; reproduciendo={next.PlayingMediaCount}"
            : next.DiagnosticMessage;

        if (next.HasTrack)
        {
            if (!_tracksBySource.TryGetValue(sourceId, out var state))
            {
                state = new BrowserTrackState { Track = next, LastSeen = now };
                _tracksBySource[sourceId] = state;
            }
            else
            {
                state.Track = next;
                state.LastSeen = now;
                state.StopRequestedAt = null;
            }

            BrowserStatus.Text = $"Extensión conectada · Suno detectado (pestaña {sourceId})";
            LogText.Text = next.IsPlaying ? "Pista recibida desde Suno" : "Pista recibida desde Suno (pausa)";
        }
        else
        {
            if (_tracksBySource.TryGetValue(sourceId, out var existing))
            {
                // Brief grace interval smooths Suno's audio-element replacement.
                existing.StopRequestedAt ??= now;
                existing.LastSeen = now;
            }

            BrowserStatus.Text = "Extensión conectada · esperando una pista reproducible";
            if (_track is null)
            {
                SourceText.Text = "EXTENSIÓN";
                PlaybackText.Text = "Extensión comunicando · sin pista detectada";
                PlaybackDot.Fill = System.Windows.Media.Brushes.Goldenrod;
                LogText.Text = "Datos de Suno recibidos; todavía no hay una pista activa";
            }
        }

        await RefreshSelectedTrackAsync();
    }

    private async Task RefreshSelectedTrackAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _tracksBySource
            .Where(entry => (entry.Value.StopRequestedAt.HasValue && now - entry.Value.StopRequestedAt.Value > TimeSpan.FromSeconds(4))
                || now - entry.Value.LastSeen > TimeSpan.FromSeconds(8))
            .Select(entry => entry.Key).ToArray())
            _tracksBySource.Remove(key);

        var selected = _tracksBySource
            .OrderByDescending(entry => entry.Value.Track.IsPlaying)
            .ThenByDescending(entry => entry.Value.LastSeen)
            .FirstOrDefault();

        if (selected.Value is null)
        {
            if (_track is null) return;
            _track = null;
            _selectedSourceId = null;
            _startedAt = null;
            TrackTitle.Text = "Esperando una canción...";
            TrackArtist.Text = "Abre Suno en Chrome o Edge";
            SourceText.Text = "SIN FUENTE";
            PlaybackText.Text = "Sin reproducción detectada";
            PlaybackDot.Fill = System.Windows.Media.Brushes.Gray;
            TrackProgress.Value = 0;
            ElapsedText.Text = "00:00";
            DurationText.Text = "--:--";
            _lastCoverUrl = null;
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            await _presence.UpdateTrackAsync(new TrackInfo { HasTrack = false }, _settings.DiscordApplicationId, _settings.Enabled);
            return;
        }

        var sourceId = selected.Key;
        var track = selected.Value.Track;
        var changed = _track is null || _selectedSourceId != sourceId
            || !string.Equals(_track.TrackId ?? _track.Title, track.TrackId ?? track.Title, StringComparison.Ordinal)
            || !string.Equals(_track.Title, track.Title, StringComparison.Ordinal)
            || !string.Equals(_track.Artist, track.Artist, StringComparison.Ordinal)
            || !string.Equals(_track.CoverUrl, track.CoverUrl, StringComparison.Ordinal)
            || !string.Equals(_track.PageUrl, track.PageUrl, StringComparison.Ordinal);
        if (changed) _startedAt = null;
        _selectedSourceId = sourceId;
        _track = track;
        if (track.IsPlaying) _startedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Math.Max(0, track.PositionSeconds));
        else _startedAt = null;

        TrackTitle.Text = track.Title;
        TrackArtist.Text = track.Artist;
        SourceText.Text = track.Source.ToUpperInvariant();
        DurationText.Text = track.DurationSeconds > 0 ? TimeSpan.FromSeconds(track.DurationSeconds).ToString(@"mm\:ss") : "--:--";
        PlaybackText.Text = track.IsPlaying ? "Reproduciendo ahora" : "En pausa";
        PlaybackDot.Fill = track.IsPlaying ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Goldenrod;
        BrowserStatus.Text = "Navegador: Suno detectado";

        // Publish metadata immediately; loading an artwork URL must never delay a song switch.
        var coverTask = UpdateCoverAsync(track.CoverUrl);
        await _presence.UpdateTrackAsync(track, _settings.DiscordApplicationId, _settings.Enabled);
        await coverTask;
    }

    private async Task UpdateCoverAsync(string? coverUrl)
    {
        if (string.Equals(_lastCoverUrl, coverUrl, StringComparison.Ordinal)) return;
        _lastCoverUrl = coverUrl;
        if (string.IsNullOrWhiteSpace(coverUrl) || !Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var bytes = await CoverHttp.GetByteArrayAsync(uri);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 520;
            bitmap.EndInit();
            bitmap.Freeze();
            // A slower request for the previous song must not overwrite the new song's cover.
            if (!string.Equals(_lastCoverUrl, coverUrl, StringComparison.Ordinal)) return;
            CoverImage.Source = bitmap;
            CoverImage.Visibility = Visibility.Visible;
        }
        catch
        {
            if (!string.Equals(_lastCoverUrl, coverUrl, StringComparison.Ordinal)) return;
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshPlaybackUi()
    {
        var now = DateTimeOffset.UtcNow;
        if (!_lastExtensionReport.HasValue || now - _lastExtensionReport.Value > TimeSpan.FromSeconds(8))
        {
            BrowserStatus.Text = "Extensión: sin mensajes recientes. Recarga la pestaña de Suno.";
        }
        var hasExpiredSources = _tracksBySource.Any(entry =>
            (entry.Value.StopRequestedAt.HasValue && now - entry.Value.StopRequestedAt.Value > TimeSpan.FromSeconds(4))
            || now - entry.Value.LastSeen > TimeSpan.FromSeconds(8));
        if (hasExpiredSources) _ = RefreshSelectedTrackAsync();
        if (_track is null) return;
        var elapsed = _track.IsPlaying && _startedAt.HasValue
            ? DateTimeOffset.UtcNow - _startedAt.Value
            : TimeSpan.FromSeconds(Math.Max(0, _track.PositionSeconds));
        ElapsedText.Text = elapsed.ToString(@"mm\:ss");
        if (_track.DurationSeconds > 0)
            TrackProgress.Value = Math.Clamp(elapsed.TotalSeconds / _track.DurationSeconds * 100, 0, 100);
    }

    private async Task RetryDiscordAsync()
    {
        await _presence.RetryAsync(_settings.Enabled);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalledBuild())
        {
            UpdateStatus.Text = "Actualizaciones: instala la app para habilitarlas.";
            return;
        }

        UpdateStatus.Text = "Actualizaciones: buscando una nueva versión...";
        try
        {
            var assemblyVersion = GetType().Assembly.GetName().Version
                ?? throw new InvalidOperationException("No se pudo determinar la versión instalada.");
            var currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build));
            var result = await _updateService.CheckAndDownloadAsync(currentVersion, cancellationToken);
            switch (result.Status)
            {
                case UpdateCheckStatus.NoRelease:
                    UpdateStatus.Text = "Actualizaciones: todavía no hay versiones publicadas.";
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatus.Text = $"Actualizaciones: tienes la versión {result.Version}.";
                    break;
                case UpdateCheckStatus.Downloaded:
                    _pendingUpdateInstaller = result.InstallerPath
                        ?? throw new InvalidDataException("La descarga no devolvió la ruta del instalador.");
                    UpdateStatus.Text = $"Actualización {result.Version} descargada; se instalará al salir.";
                    _tray?.ShowBalloonTip(3500, "Actualización lista", "Se instalará cuando elijas Salir desde la bandeja.", Forms.ToolTipIcon.Info);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Actualizaciones: no se pudo comprobar.";
            LogText.Text = $"Actualización: {ex.Message}";
        }
    }

    private static bool IsInstalledBuild()
    {
        var executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executablePath)
            && File.Exists(Path.Combine(Path.GetDirectoryName(executablePath)!, "unins000.exe"));
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        UpdateTrayLabel();
    }

    private void SaveSettingsFromUi()
    {
        _settings.DiscordApplicationId = ClientIdBox.Text.Trim();
        _settings.Enabled = PresenceEnabled.IsChecked == true;
        _settings.StartWithWindows = StartWithWindows.IsChecked == true;
        SaveSettings();
        UpdateStartupRegistration(_settings.StartWithWindows);
    }

    private static void UpdateStartupRegistration(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir la configuración de inicio de Windows.");
        if (!enabled)
        {
            runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("No se pudo determinar la ruta de la aplicación para iniciar con Windows.");
        runKey.SetValue(StartupValueName, $"\"{executablePath}\" {StartInBackgroundArgument}", RegistryValueKind.String);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try { SaveSettingsFromUi(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"No se pudo guardar la configuración: {ex.Message}", "aarideev | SunoSync", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await _presence.ConfigureAsync(_settings.DiscordApplicationId, _settings.Enabled);
        LogText.Text = "Configuración guardada";
    }

    private async void TestDiscord_Click(object sender, RoutedEventArgs e)
    {
        try { SaveSettingsFromUi(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"No se pudo guardar la configuración: {ex.Message}", "aarideev | SunoSync", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            await _presence.TestDiscordAsync(_settings.DiscordApplicationId, _settings.Enabled);
        }
        catch (Exception ex)
        {
            DiscordStatus.Text = $"Discord: error de prueba · {ex.Message}";
            LogText.Text = "Falló la prueba RPC";
        }
    }

    private void OpenDiscordDeveloper_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true });

    private void OpenSuno_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://suno.com") { UseShellExecute = true });

    private void OpenExtensionPage_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ExtensionPath))
        {
            System.Windows.MessageBox.Show("No se encuentra la extensión extraída en AppData. Cierra y vuelve a abrir la aplicación para regenerarla.", "aarideev | SunoSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", ExtensionPath) { UseShellExecute = true });
        System.Windows.MessageBox.Show($"Para cargar la extensión local en cada navegador:\n\n1. Abre brave://extensions (Brave), chrome://extensions (Chrome) o edge://extensions (Edge).\n2. Activa Modo de desarrollador.\n3. Pulsa Cargar descomprimida.\n4. Selecciona la carpeta que se acaba de abrir en el Explorador.\n5. Si la extensión ya estaba instalada, pulsa Recargar.\n6. Pulsa el icono de Arideev en la barra del navegador y luego «Conectar con Arideev».\n7. Si el navegador pregunta por acceso local / loopback, pulsa «Permitir».\n8. Recarga Suno (Ctrl+R) y vuelve a reproducir la canción.\n\nLa extensión se instala una sola vez en cada navegador.", "Instalar conector de navegador", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ExitApplication()
    {
        if (_reallyExit) return;
        _reallyExit = true;
        _updateCheckCancellation.Cancel();
        _uiTimer.Stop();
        _retryTimer.Stop();
        _bridge?.Dispose();
        try
        {
            await _presence.ConfigureAsync(_settings.DiscordApplicationId, enabled: false);
            await _presence.DisposeAsync();
        }
        catch { }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _trayAppIcon?.Dispose();
        _trayAppIcon = null;
        if (_pendingUpdateInstaller is not null)
        {
            try
            {
                var updater = Process.Start(new ProcessStartInfo(
                    _pendingUpdateInstaller,
                    "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = true
                });
                if (updater is null) throw new InvalidOperationException("Windows no pudo iniciar el instalador.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"No se pudo iniciar la actualización: {ex.Message}", "aarideev | SunoSync", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Close();
    }
}

public sealed class AppSettings
{
    public string DiscordApplicationId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
}
