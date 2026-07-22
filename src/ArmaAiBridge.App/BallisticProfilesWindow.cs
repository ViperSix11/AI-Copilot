using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Microsoft.Win32;

namespace ArmaAiBridge.App;

public sealed class BallisticProfilesWindow : Window
{
    private readonly BallisticProfileManager _manager;
    private readonly Func<StateBallisticProfile?> _gameProfile;
    private readonly Func<StateEnvironment?> _environment;
    private readonly ListBox _profiles = new() { MinWidth = 245 };
    private readonly TextBlock _current = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, TextBox> _text = new(StringComparer.Ordinal);
    private readonly CheckBox _enabled = new() { Content = "Enabled" };
    private readonly CheckBox _wind = new() { Content = "Wind correction", IsChecked = true };
    private readonly CheckBox _spin = new() { Content = "Spin drift" };
    private readonly ComboBox _drag = new() { ItemsSource = new[] { "G1", "G2", "G5", "G6", "G7", "G8" }, Width = 120 };
    private readonly ComboBox _solver = new() { ItemsSource = Enum.GetValues<BallisticSolverPreference>(), Width = 220 };

    public BallisticProfilesWindow(BallisticProfileManager manager, Func<StateBallisticProfile?> gameProfile, Func<StateEnvironment?> environment)
    {
        _manager = manager; _gameProfile = gameProfile; _environment = environment;
        Title = "Ballistic Profiles"; Width = 1050; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Build(); Refresh(); UseCurrentTestState();
    }

    private UIElement Build()
    {
        Grid root = new() { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel left = new(); left.Children.Add(new TextBlock { Text = "Profiles", FontSize = 18, FontWeight = FontWeights.Bold });
        left.Children.Add(_profiles); _profiles.SelectionChanged += (_, _) => LoadSelected();
        WrapPanel listActions = new() { Margin = new Thickness(0, 8, 0, 0) };
        listActions.Children.Add(Button("New", (_, _) => Add(false))); listActions.Children.Add(Button("From current", (_, _) => Add(true)));
        listActions.Children.Add(Button("Duplicate", (_, _) => Duplicate())); listActions.Children.Add(Button("Delete", (_, _) => Delete()));
        left.Children.Add(listActions);
        WrapPanel fileActions = new() { Margin = new Thickness(0, 8, 0, 0) };
        fileActions.Children.Add(Button("Import JSON", (_, _) => Import())); fileActions.Children.Add(Button("Export JSON", (_, _) => Export()));
        left.Children.Add(fileActions); left.Children.Add(new TextBlock { Text = "Storage", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0) });
        left.Children.Add(new TextBlock { Text = _manager.StoragePath, TextWrapping = TextWrapping.Wrap }); Grid.SetColumn(left, 0); root.Children.Add(left);

        StackPanel form = new(); form.Children.Add(new TextBlock { Text = "Current game state and match", FontSize = 18, FontWeight = FontWeights.Bold });
        form.Children.Add(_current); form.Children.Add(Field("Display name", "DisplayName")); form.Children.Add(_enabled);
        form.Children.Add(Field("Notes", "Notes", 70)); form.Children.Add(new TextBlock { Text = "Exact case-sensitive matches; blank means wildcard.", FontStyle = FontStyles.Italic });
        foreach ((string label, string key) in new[] { ("Weapon class", "WeaponClass"), ("Muzzle class", "MuzzleClass"), ("Magazine class", "MagazineClass"), ("Ammunition class", "AmmunitionClass"), ("Weapon display name match", "WeaponDisplayNameMatch"), ("Match priority", "Priority") }) form.Children.Add(Field(label, key));
        form.Children.Add(new Separator());
        foreach ((string label, string key) in new[] { ("Weapon display name", "WeaponDisplayName"), ("Bullet diameter (mm)", "Diameter"), ("Bullet length (mm)", "Length"), ("Bullet mass (g)", "Mass"), ("Nominal muzzle velocity (m/s)", "Velocity"), ("Barrel length (mm)", "BarrelLength"), ("Barrel twist (mm/turn)", "Twist"), ("Sight height (mm)", "SightHeight"), ("Default zero range (m)", "DefaultZero"), ("Manual pressure (hPa, optional)", "Pressure"), ("Maximum supported range (m)", "MaximumRange") }) form.Children.Add(Field(label, key));
        form.Children.Add(Labeled("Drag model", _drag)); form.Children.Add(Field("Ballistic coefficients (comma separated)", "Coefficients")); form.Children.Add(Field("Velocity boundaries (m/s, comma separated)", "Boundaries")); form.Children.Add(Field("Standard atmosphere (ICAO or ASM)", "Atmosphere")); form.Children.Add(Field("Temperature velocity shifts", "TemperatureShifts")); form.Children.Add(Field("Barrel-length table", "BarrelLengths")); form.Children.Add(Field("Barrel velocity table", "BarrelVelocities"));
        form.Children.Add(Labeled("Preferred solver", _solver)); form.Children.Add(_wind); form.Children.Add(_spin);
        WrapPanel actions = new() { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Button("Save", (_, _) => SaveSelected())); actions.Children.Add(Button("Validate", (_, _) => ValidateSelected())); actions.Children.Add(Button("Set temporary active", (_, _) => Force())); actions.Children.Add(Button("Reset to automatic", (_, _) => { _manager.ForceTemporary(null); UpdateCurrent(); }));
        form.Children.Add(actions);
        form.Children.Add(new TextBlock { Text = "Local test calculation", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 4) });
        foreach ((string label, string key) in new[] { ("Range (m)", "TestRange"), ("Bearing (degrees)", "TestBearing"), ("Shooter elevation ASL (m)", "TestShooter"), ("Target elevation ASL (m)", "TestTarget"), ("Temperature (C)", "TestTemperature"), ("Humidity (0-1)", "TestHumidity"), ("Pressure (hPa, optional)", "TestPressure"), ("Wind speed (m/s)", "TestWindSpeed"), ("Wind from direction (degrees)", "TestWindDirection"), ("Current zero (m)", "TestZero") }) form.Children.Add(Field(label, key));
        WrapPanel testActions = new(); testActions.Children.Add(Button("Use Current Game State", (_, _) => UseCurrentTestState())); testActions.Children.Add(Button("Calculate Test Solution", async (_, _) => await TestAsync())); form.Children.Add(testActions);
        form.Children.Add(_validation);
        ScrollViewer scroll = new() { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18, 0, 0, 0) }; Grid.SetColumn(scroll, 1); root.Children.Add(scroll); return root;
    }

    private FrameworkElement Field(string label, string key, double height = double.NaN)
    {
        TextBox box = new() { MinWidth = 300 }; if (!double.IsNaN(height)) { box.Height = height; box.AcceptsReturn = true; }
        _text[key] = box; return Labeled(label, box);
    }
    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        Grid row = new() { Margin = new Thickness(0, 4, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
    private static Button Button(string text, RoutedEventHandler click) { Button button = new() { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 4, 8, 4) }; button.Click += click; return button; }
    private void Refresh() { _profiles.ItemsSource = null; _profiles.ItemsSource = _manager.Profiles; _profiles.DisplayMemberPath = nameof(UserBallisticProfile.DisplayName); if (_manager.Profiles.Count > 0) _profiles.SelectedIndex = 0; UpdateCurrent(); }
    private UserBallisticProfile? Selected => _profiles.SelectedItem as UserBallisticProfile;

    private void Add(bool fromCurrent)
    {
        StateBallisticProfile? game = _gameProfile(); UserBallisticProfile profile = new();
        if (fromCurrent && game is not null) { profile.DisplayName = string.IsNullOrWhiteSpace(game.WeaponDisplayName) ? "Current weapon profile" : game.WeaponDisplayName; profile.WeaponClassMatch = game.WeaponClass; profile.MuzzleClassMatch = game.MuzzleClass; profile.MagazineClassMatch = game.MagazineClass; profile.AmmunitionClassMatch = game.AmmunitionClass; profile.WeaponDisplayName = game.WeaponDisplayName; profile.AmmunitionDisplayName = game.AmmunitionDisplayName; profile.NominalMuzzleVelocityMetersPerSecond = game.InitialSpeedMetersPerSecond > 0 ? game.InitialSpeedMetersPerSecond : null; profile.DefaultZeroRangeMeters = game.CurrentZeroingMeters; }
        _manager.Add(profile); Refresh(); _profiles.SelectedItem = profile;
    }
    private void Duplicate() { if (Selected is not { } selected) return; UserBallisticProfile copy = selected.CloneAsNew(); _manager.Add(copy); Refresh(); _profiles.SelectedItem = copy; }
    private void Delete() { if (Selected is not { } selected || MessageBox.Show(this, $"Delete '{selected.DisplayName}'?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; if (_manager.ForcedProfileId == selected.ProfileId) _manager.ForceTemporary(null); _manager.Remove(selected); Refresh(); }
    private void LoadSelected()
    {
        if (Selected is not { } p) return;
        Set("DisplayName", p.DisplayName); Set("Notes", p.Notes); _enabled.IsChecked = p.Enabled; Set("WeaponClass", p.WeaponClassMatch); Set("MuzzleClass", p.MuzzleClassMatch); Set("MagazineClass", p.MagazineClassMatch); Set("AmmunitionClass", p.AmmunitionClassMatch); Set("WeaponDisplayNameMatch", p.WeaponDisplayNameMatch); Set("Priority", p.MatchPriority); Set("WeaponDisplayName", p.WeaponDisplayName); Set("Diameter", p.BulletDiameterMillimeters); Set("Length", p.BulletLengthMillimeters); Set("Mass", p.BulletMassGrams); Set("Velocity", p.NominalMuzzleVelocityMetersPerSecond); Set("BarrelLength", p.BarrelLengthMillimeters); Set("Twist", p.BarrelTwistMillimetersPerTurn); Set("SightHeight", p.SightHeightMillimeters); Set("DefaultZero", p.DefaultZeroRangeMeters); Set("Pressure", p.ManualPressureHectopascals); Set("MaximumRange", p.MaximumSupportedRangeMeters); _drag.SelectedItem = p.DragModel; Set("Coefficients", p.BallisticCoefficients); Set("Boundaries", p.VelocityBoundariesMetersPerSecond); Set("Atmosphere", p.StandardAtmosphere); Set("TemperatureShifts", p.TemperatureMuzzleVelocityShifts); Set("BarrelLengths", p.BarrelLengthsMillimeters); Set("BarrelVelocities", p.BarrelMuzzleVelocitiesMetersPerSecond); _solver.SelectedItem = p.PreferredSolver; _wind.IsChecked = p.WindCorrectionEnabled; _spin.IsChecked = p.SpinDriftEnabled; ValidateSelected();
    }
    private void SaveSelected()
    {
        if (Selected is not { } p) return;
        try { p.DisplayName = Get("DisplayName"); p.Notes = Get("Notes"); p.Enabled = _enabled.IsChecked == true; p.WeaponClassMatch = Get("WeaponClass"); p.MuzzleClassMatch = Get("MuzzleClass"); p.MagazineClassMatch = Get("MagazineClass"); p.AmmunitionClassMatch = Get("AmmunitionClass"); p.WeaponDisplayNameMatch = Get("WeaponDisplayNameMatch"); p.MatchPriority = Int("Priority"); p.WeaponDisplayName = Get("WeaponDisplayName"); p.BulletDiameterMillimeters = Nullable("Diameter"); p.BulletLengthMillimeters = Nullable("Length"); p.BulletMassGrams = Nullable("Mass"); p.NominalMuzzleVelocityMetersPerSecond = Nullable("Velocity"); p.BarrelLengthMillimeters = Nullable("BarrelLength"); p.BarrelTwistMillimetersPerTurn = Nullable("Twist"); p.SightHeightMillimeters = Nullable("SightHeight"); p.DefaultZeroRangeMeters = Nullable("DefaultZero"); p.ManualPressureHectopascals = Nullable("Pressure"); p.MaximumSupportedRangeMeters = Required("MaximumRange"); p.DragModel = _drag.SelectedItem as string ?? "G7"; p.BallisticCoefficients = Array("Coefficients"); p.VelocityBoundariesMetersPerSecond = Array("Boundaries"); p.StandardAtmosphere = Get("Atmosphere").ToUpperInvariant(); p.TemperatureMuzzleVelocityShifts = Array("TemperatureShifts"); p.BarrelLengthsMillimeters = Array("BarrelLengths"); p.BarrelMuzzleVelocitiesMetersPerSecond = Array("BarrelVelocities"); p.PreferredSolver = _solver.SelectedItem is BallisticSolverPreference solver ? solver : BallisticSolverPreference.Automatic; p.WindCorrectionEnabled = _wind.IsChecked == true; p.SpinDriftEnabled = _spin.IsChecked == true; _manager.Save(); Refresh(); _profiles.SelectedItem = p; _validation.Text = "Saved locally."; } catch (Exception ex) { _validation.Text = ex.Message; }
    }
    private void ValidateSelected() { if (Selected is not { } p) return; BallisticProfileValidation result = BallisticProfileValidator.Validate(p); _validation.Text = result.IsValid ? (result.Warnings.Count == 0 ? "Valid" : "Valid with warnings: " + string.Join("; ", result.Warnings)) : "Draft / invalid: " + string.Join("; ", result.Errors); }
    private void Force() { if (Selected is not { } p) return; BallisticProfileValidation result = BallisticProfileValidator.Validate(p); if (!result.IsValid) { _validation.Text = "Invalid profiles cannot be activated."; return; } _manager.ForceTemporary(p.ProfileId); UpdateCurrent(); }
    private void UpdateCurrent() { StateBallisticProfile? game = _gameProfile(); BallisticProfileMatch match = _manager.Match(game); _current.Text = $"Weapon: {game?.WeaponClass ?? "unavailable"}\nMuzzle: {game?.MuzzleClass ?? "unavailable"}\nMagazine: {game?.MagazineClass ?? "unavailable"}\nAmmunition: {game?.AmmunitionClass ?? "unavailable"}\nMatched profile: {match.Profile?.DisplayName ?? "none"}\nReason: {match.Reason}\nMode: {_manager.Resolve(game, out _)?.Model ?? game?.Model ?? "unavailable"}"; }
    private void UseCurrentTestState()
    {
        StateBallisticProfile? game = _gameProfile(); StateEnvironment? environment = _environment();
        Set("TestRange", 800); Set("TestBearing", 45); Set("TestShooter", game?.ShooterPositionAsl.Z ?? 0);
        Set("TestTarget", game?.ShooterPositionAsl.Z ?? 0); Set("TestTemperature", environment?.TemperatureCelsius ?? 15);
        Set("TestHumidity", environment?.Humidity ?? 0.5); Set("TestPressure", null); Set("TestZero", game?.CurrentZeroingMeters ?? 100);
        double speed = environment is null ? 0 : Math.Sqrt(environment.WindX * environment.WindX + environment.WindY * environment.WindY);
        Set("TestWindSpeed", speed); Set("TestWindDirection", environment?.WindDirection ?? 0);
    }

    private async Task TestAsync()
    {
        SaveSelected(); StateBallisticProfile? current = _gameProfile();
        if (current is null) { _validation.Text = "Current game state is unavailable."; return; }
        try
        {
            double range = Required("TestRange"), bearing = Required("TestBearing"), shooter = Required("TestShooter"), target = Required("TestTarget");
            double temperature = Required("TestTemperature"), humidity = Required("TestHumidity"), windSpeed = Required("TestWindSpeed"), windFrom = Required("TestWindDirection"), zero = Required("TestZero");
            double windToward = (windFrom + 180) * Math.PI / 180;
            FrozenBallisticEnvironment frozen = new(windSpeed * Math.Sin(windToward), windSpeed * Math.Cos(windToward), temperature, humidity, shooter, Nullable("TestPressure"));
            StateBallisticProfile game = current with { ShooterPositionAsl = current.ShooterPositionAsl with { Z = shooter }, CurrentZeroingMeters = zero };
            string json = JsonSerializer.Serialize(new { rangeMeters = range, bearingDegrees = bearing, targetElevationAslMeters = target, targetHeightAboveTerrainMeters = (double?)null });
            using JsonDocument args = JsonDocument.Parse(json);
            string result = await new BallisticToolService((_, _, _) => Task.FromResult(target), profiles: _manager, environment: () => frozen).CalculateAsync(args.RootElement, game, CancellationToken.None);
            ResolvedBallisticProfile? resolved = _manager.Resolve(game, out _);
            string provenance = resolved is null ? "" : string.Join(Environment.NewLine, resolved.Provenance.Select(item => $"{item.Key}: {item.Value}"));
            _validation.Text = result + Environment.NewLine + "Resolved provenance:" + Environment.NewLine + provenance + Environment.NewLine + "temperature/humidity/wind: test inputs; zeroRange: current/test Arma state";
        }
        catch (Exception exception) { _validation.Text = exception.Message; }
    }
    private void Import() { OpenFileDialog dialog = new() { Filter = "JSON files|*.json" }; if (dialog.ShowDialog(this) != true) return; try { BallisticProfileDocument preview = _manager.PreviewImport(File.ReadAllText(dialog.FileName)); if (MessageBox.Show(this, $"Replace local profiles with {preview.Profiles.Count} imported profile(s)?", "Import preview", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { _manager.Replace(preview); Refresh(); } } catch (Exception ex) { _validation.Text = ex.Message; } }
    private void Export() { SaveFileDialog dialog = new() { Filter = "JSON files|*.json", FileName = "ballistic-profiles.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, _manager.Export()); }
    private string Get(string key) => _text[key].Text.Trim(); private void Set(string key, object? value) => _text[key].Text = value switch { null => "", IEnumerable<double> values => string.Join(", ", values.Select(v => v.ToString("G", CultureInfo.InvariantCulture))), IFormattable format => format.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
    private double? Nullable(string key) => Get(key).Length == 0 ? null : Required(key); private double Required(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : throw new InvalidOperationException($"{key} must be a finite number."); private int Int(string key) => Get(key).Length == 0 ? 0 : int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : throw new InvalidOperationException($"{key} must be an integer."); private List<double> Array(string key) => Get(key).Length == 0 ? [] : Get(key).Split(',', StringSplitOptions.TrimEntries).Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : throw new InvalidOperationException($"{key} contains an invalid number.")).ToList();
}
