using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia3DViewer.Controls;
using Avalonia3DViewer.Rendering;

namespace Avalonia3DViewer;

public partial class MainWindow : Window
{
    private GLViewport? _viewport;
    private Slider? _exposureSlider;
    private Slider? _mainLightSlider;
    private Slider? _fillLightSlider;
    private Slider? _rimLightSlider;
    private Slider? _topLightSlider;
    private Slider? _ambientSlider;
    private CheckBox? _keyLightFollowsCameraCheckBox;
    private Slider? _keyLightSideBiasSlider;
    private Slider? _keyLightUpBiasSlider;
    private Slider? _shadowStrengthSlider;
    private Slider? _ssaoIntensitySlider;
    private Slider? _bloomIntensitySlider;
    private CheckBox? _useShadowsCheckBox;
    private CheckBox? _useBloomCheckBox;
    private CheckBox? _useSSAOCheckBox;
    private CheckBox? _showGroundCheckBox;
    private RadioButton? _backgroundLightRadio;
    private RadioButton? _backgroundDarkRadio;
    private CheckBox? _useIBLCheckBox;
    private CheckBox? _useTonemappingCheckBox;
    private CheckBox? _useFXAACheckBox;
    private CheckBox? _useMSAACheckBox;
    private ComboBox? _msaaSamplesComboBox;
    private ComboBox? _debugModeComboBox;
    private Slider? _specularScaleSlider;
    private Slider? _roughnessOffsetSlider;
    private Slider? _metallicOffsetSlider;
    private Slider? _iblIntensitySlider;
    private TextBlock? _infoText;
    private Button? _runGcButton;
    
    private ComboBox? _modelComboBox;
    private readonly ModelLibrary _modelLibrary = new();
    
    private Border? _loadingOverlay;
    private TextBlock? _loadingText;
    private ProgressBar? _loadingProgressBar;

    public MainWindow()
    {
        InitializeComponent();
        InitializeControls();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewport != null)
            _viewport.LoadingStatusChanged -= OnLoadingStatusChanged;
        
        base.OnClosed(e);
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private void InitializeControls()
    {
        FindAllControls();
        SubscribeToLoadingStatusChanges();
        InitializeControlValues();
        SetupEventHandlers();
    }

    private void FindAllControls()
    {
        _viewport = this.FindControl<GLViewport>("Viewport");
        _loadingOverlay = this.FindControl<Border>("LoadingOverlay");
        _loadingText = this.FindControl<TextBlock>("LoadingText");
        _loadingProgressBar = this.FindControl<ProgressBar>("LoadingProgressBar");
        
        _exposureSlider = this.FindControl<Slider>("ExposureSlider");
        _mainLightSlider = this.FindControl<Slider>("MainLightSlider");
        _fillLightSlider = this.FindControl<Slider>("FillLightSlider");
        _rimLightSlider = this.FindControl<Slider>("RimLightSlider");
        _topLightSlider = this.FindControl<Slider>("TopLightSlider");
        _ambientSlider = this.FindControl<Slider>("AmbientSlider");
        _keyLightFollowsCameraCheckBox = this.FindControl<CheckBox>("KeyLightFollowsCameraCheckBox");
        _keyLightSideBiasSlider = this.FindControl<Slider>("KeyLightSideBiasSlider");
        _keyLightUpBiasSlider = this.FindControl<Slider>("KeyLightUpBiasSlider");
        _shadowStrengthSlider = this.FindControl<Slider>("ShadowStrengthSlider");
        _ssaoIntensitySlider = this.FindControl<Slider>("SsaoIntensitySlider");
        _bloomIntensitySlider = this.FindControl<Slider>("BloomIntensitySlider");
        _useShadowsCheckBox = this.FindControl<CheckBox>("UseShadowsCheckBox");
        _useBloomCheckBox = this.FindControl<CheckBox>("UseBloomCheckBox");
        _useSSAOCheckBox = this.FindControl<CheckBox>("UseSSAOCheckBox");
        _showGroundCheckBox = this.FindControl<CheckBox>("ShowGroundCheckBox");
        _backgroundLightRadio = this.FindControl<RadioButton>("BackgroundLightRadio");
        _backgroundDarkRadio = this.FindControl<RadioButton>("BackgroundDarkRadio");
        _useIBLCheckBox = this.FindControl<CheckBox>("UseIBLCheckBox");
        _useTonemappingCheckBox = this.FindControl<CheckBox>("UseTonemappingCheckBox");
        _useFXAACheckBox = this.FindControl<CheckBox>("UseFXAACheckBox");
        _useMSAACheckBox = this.FindControl<CheckBox>("UseMSAACheckBox");
        _msaaSamplesComboBox = this.FindControl<ComboBox>("MsaaSamplesComboBox");
        _debugModeComboBox = this.FindControl<ComboBox>("DebugModeComboBox");
        _specularScaleSlider = this.FindControl<Slider>("SpecularScaleSlider");
        _roughnessOffsetSlider = this.FindControl<Slider>("RoughnessOffsetSlider");
        _metallicOffsetSlider = this.FindControl<Slider>("MetallicOffsetSlider");
        _iblIntensitySlider = this.FindControl<Slider>("IblIntensitySlider");
        _infoText = this.FindControl<TextBlock>("InfoText");
        _runGcButton = this.FindControl<Button>("RunGcButton");
        _modelComboBox = this.FindControl<ComboBox>("ModelComboBox");
    }

    private void SubscribeToLoadingStatusChanges()
    {
        if (_viewport != null)
            _viewport.LoadingStatusChanged += OnLoadingStatusChanged;
    }

    private void InitializeControlValues()
    {
        if (_viewport == null) return;

        SetSliderValue(_exposureSlider, _viewport.Exposure);
        SetSliderValue(_mainLightSlider, _viewport.MainLightIntensity);
        SetSliderValue(_fillLightSlider, _viewport.FillLightIntensity);
        SetSliderValue(_rimLightSlider, _viewport.RimLightIntensity);
        SetSliderValue(_topLightSlider, _viewport.TopLightIntensity);
        SetSliderValue(_ambientSlider, _viewport.AmbientIntensity);
        SetSliderValue(_keyLightSideBiasSlider, _viewport.KeyLightSideBias);
        SetSliderValue(_keyLightUpBiasSlider, _viewport.KeyLightUpBias);
        SetSliderValue(_shadowStrengthSlider, _viewport.ShadowStrength);
        SetSliderValue(_ssaoIntensitySlider, _viewport.SsaoIntensity);
        SetSliderValue(_bloomIntensitySlider, _viewport.BloomIntensity);
        SetSliderValue(_specularScaleSlider, _viewport.SpecularScale);
        SetSliderValue(_roughnessOffsetSlider, _viewport.RoughnessOffset);
        SetSliderValue(_metallicOffsetSlider, _viewport.MetallicOffset);
        SetSliderValue(_iblIntensitySlider, _viewport.IblIntensity);

        SetCheckBoxValue(_keyLightFollowsCameraCheckBox, _viewport.KeyLightFollowsCamera);
        SetCheckBoxValue(_useShadowsCheckBox, _viewport.UseShadows);
        SetCheckBoxValue(_useBloomCheckBox, _viewport.UseBloom);
        SetCheckBoxValue(_useSSAOCheckBox, _viewport.UseSSAO);
        SetCheckBoxValue(_showGroundCheckBox, _viewport.ShowGround);
        SetCheckBoxValue(_useIBLCheckBox, _viewport.UseIBL);
        SetCheckBoxValue(_useTonemappingCheckBox, _viewport.UseTonemapping);
        SetCheckBoxValue(_useFXAACheckBox, _viewport.UseFXAA);
        SetCheckBoxValue(_useMSAACheckBox, _viewport.UseMSAA);

        SetRadioButtonValue(_backgroundLightRadio, !_viewport.UseDarkBackground);
        SetRadioButtonValue(_backgroundDarkRadio, _viewport.UseDarkBackground);

        InitializeMsaaSamplesComboBox();
    }

    private void InitializeMsaaSamplesComboBox()
    {
        if (_msaaSamplesComboBox == null || _viewport == null) return;

        int idx = _viewport.MsaaSamples switch { 2 => 0, 4 => 1, 8 => 2, _ => 1 };
        _msaaSamplesComboBox.SelectedIndex = idx;
        _msaaSamplesComboBox.IsEnabled = _viewport.UseMSAA;
    }

    private static void SetSliderValue(Slider? slider, float value)
    {
        if (slider != null) slider.Value = value;
    }

    private static void SetCheckBoxValue(CheckBox? checkBox, bool value)
    {
        if (checkBox != null) checkBox.IsChecked = value;
    }

    private static void SetRadioButtonValue(RadioButton? radioButton, bool value)
    {
        if (radioButton != null) radioButton.IsChecked = value;
    }

    private void SetupEventHandlers()
    {
        SetupButtonHandlers();
        SetupSliderHandlers();
        SetupCheckBoxHandlers();
        SetupComboBoxHandlers();
        SetupModelLibraryHandlers();
    }

    private void SetupButtonHandlers()
    {
        var loadModelButton = this.FindControl<Button>("LoadModelButton");
        var loadEnvironmentButton = this.FindControl<Button>("LoadEnvironmentButton");
        var addModelButton = this.FindControl<Button>("AddModelButton");
        var removeModelButton = this.FindControl<Button>("RemoveModelButton");

        if (loadModelButton != null) loadModelButton.Click += LoadModelButton_Click;
        if (loadEnvironmentButton != null) loadEnvironmentButton.Click += LoadEnvironmentButton_Click;
        if (addModelButton != null) addModelButton.Click += AddModelButton_Click;
        if (removeModelButton != null) removeModelButton.Click += RemoveModelButton_Click;
        if (_runGcButton != null) _runGcButton.Click += RunGcButton_Click;
    }

    private void SetupSliderHandlers()
    {
        BindSliderToViewport(_exposureSlider, v => _viewport!.Exposure = v);
        BindSliderToViewport(_mainLightSlider, v => _viewport!.MainLightIntensity = v);
        BindSliderToViewport(_fillLightSlider, v => _viewport!.FillLightIntensity = v);
        BindSliderToViewport(_rimLightSlider, v => _viewport!.RimLightIntensity = v);
        BindSliderToViewport(_topLightSlider, v => _viewport!.TopLightIntensity = v);
        BindSliderToViewport(_ambientSlider, v => _viewport!.AmbientIntensity = v);
        BindSliderToViewport(_keyLightSideBiasSlider, v => _viewport!.KeyLightSideBias = v);
        BindSliderToViewport(_keyLightUpBiasSlider, v => _viewport!.KeyLightUpBias = v);
        BindSliderToViewport(_shadowStrengthSlider, v => _viewport!.ShadowStrength = v);
        BindSliderToViewport(_ssaoIntensitySlider, v => _viewport!.SsaoIntensity = v);
        BindSliderToViewport(_bloomIntensitySlider, v => _viewport!.BloomIntensity = v);
        BindSliderToViewport(_specularScaleSlider, v => _viewport!.SpecularScale = v);
        BindSliderToViewport(_roughnessOffsetSlider, v => _viewport!.RoughnessOffset = v);
        BindSliderToViewport(_metallicOffsetSlider, v => _viewport!.MetallicOffset = v);
        BindSliderToViewport(_iblIntensitySlider, v => _viewport!.IblIntensity = v);
    }

    private void BindSliderToViewport(Slider? slider, Action<float> setter)
    {
        if (slider == null) return;

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Value" || _viewport == null) return;
            setter((float)slider.Value);
            _viewport.RequestNextFrameRendering();
        };
    }

    private void SetupCheckBoxHandlers()
    {
        BindCheckBoxToViewport(_keyLightFollowsCameraCheckBox, v => _viewport!.KeyLightFollowsCamera = v);
        BindCheckBoxToViewport(_useShadowsCheckBox, v => _viewport!.UseShadows = v);
        BindCheckBoxToViewport(_useBloomCheckBox, v => _viewport!.UseBloom = v);
        BindCheckBoxToViewport(_useSSAOCheckBox, v => _viewport!.UseSSAO = v);
        BindCheckBoxToViewport(_showGroundCheckBox, v => _viewport!.ShowGround = v);
        BindCheckBoxToViewport(_useIBLCheckBox, v => _viewport!.UseIBL = v);
        BindCheckBoxToViewport(_useTonemappingCheckBox, v => _viewport!.UseTonemapping = v);
        BindCheckBoxToViewport(_useFXAACheckBox, v => _viewport!.UseFXAA = v);

        SetupMsaaCheckBoxHandler();
        SetupBackgroundRadioHandlers();
    }

    private void BindCheckBoxToViewport(CheckBox? checkBox, Action<bool> setter)
    {
        if (checkBox == null) return;

        checkBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "IsChecked" || _viewport == null || !checkBox.IsChecked.HasValue) return;
            setter(checkBox.IsChecked.Value);
            _viewport.RequestNextFrameRendering();
        };
    }

    private void SetupMsaaCheckBoxHandler()
    {
        if (_useMSAACheckBox == null) return;

        _useMSAACheckBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "IsChecked" || _viewport == null || !_useMSAACheckBox.IsChecked.HasValue) return;

            _viewport.UseMSAA = _useMSAACheckBox.IsChecked.Value;
            if (_msaaSamplesComboBox != null)
                _msaaSamplesComboBox.IsEnabled = _viewport.UseMSAA;
            _viewport.RequestFramebufferRecreate();
        };
    }

    private void SetupBackgroundRadioHandlers()
    {
        if (_backgroundLightRadio != null)
        {
            _backgroundLightRadio.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name != "IsChecked" || _viewport == null || _backgroundLightRadio.IsChecked != true) return;
                _viewport.UseDarkBackground = false;
                _viewport.RequestNextFrameRendering();
            };
        }

        if (_backgroundDarkRadio != null)
        {
            _backgroundDarkRadio.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name != "IsChecked" || _viewport == null || _backgroundDarkRadio.IsChecked != true) return;
                _viewport.UseDarkBackground = true;
                _viewport.RequestNextFrameRendering();
            };
        }
    }

    private void SetupComboBoxHandlers()
    {
        SetupMsaaSamplesHandler();
        SetupDebugModeHandler();
    }

    private void SetupMsaaSamplesHandler()
    {
        if (_msaaSamplesComboBox == null) return;

        _msaaSamplesComboBox.SelectionChanged += (_, _) =>
        {
            if (_viewport == null) return;

            int samples = _msaaSamplesComboBox.SelectedIndex switch { 0 => 2, 1 => 4, 2 => 8, _ => 4 };
            _viewport.MsaaSamples = samples;

            if (_viewport.UseMSAA)
                _viewport.RequestFramebufferRecreate();
            else
                _viewport.RequestNextFrameRendering();
        };
    }

    private void SetupDebugModeHandler()
    {
        if (_debugModeComboBox == null) return;

        _debugModeComboBox.SelectionChanged += (_, _) =>
        {
            if (_viewport == null) return;
            _viewport.DebugMode = _debugModeComboBox.SelectedIndex;
            _viewport.RequestNextFrameRendering();
        };
    }

    private void SetupModelLibraryHandlers()
    {
        if (_modelComboBox == null) return;

        _modelComboBox.ItemsSource = _modelLibrary.Models;
        _modelComboBox.SelectionChanged += ModelComboBox_SelectionChanged;
    }

    private void RunGcButton_Click(object? sender, RoutedEventArgs e)
    {
        long before = GC.GetTotalMemory(forceFullCollection: false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);

        if (_infoText != null)
        {
            _infoText.Text = $"GC done. Managed memory: {ToMiB(before):F1} MiB → {ToMiB(after):F1} MiB";
        }
    }

    private static double ToMiB(long bytes) => bytes / (1024.0 * 1024.0);

    private void OnLoadingStatusChanged(bool isLoading, string status, int progressPercent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_loadingOverlay != null)
                _loadingOverlay.IsVisible = isLoading;

            if (_loadingText != null)
                _loadingText.Text = status;

            if (_loadingProgressBar == null) return;

            if (progressPercent < 0)
            {
                _loadingProgressBar.IsIndeterminate = true;
            }
            else
            {
                _loadingProgressBar.IsIndeterminate = false;
                _loadingProgressBar.Maximum = 100;
                _loadingProgressBar.Value = progressPercent;
            }
        });
    }

    private async void LoadModelButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = await BrowseForModel();
        if (path != null)
            await LoadModelFromPathAsync(path);
    }

    private async Task<string?> BrowseForModel()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open 3D Model",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("3D Models")
                {
                    Patterns = new[] { "*.obj", "*.fbx", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.stl" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task LoadModelFromPathAsync(string path)
    {
        if (_viewport == null) return;

        try
        {
            await _viewport.LoadModelAsync(path);
            if (_infoText != null)
                _infoText.Text = $"Loaded: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            if (_infoText != null)
                _infoText.Text = $"Error: {ex.Message}";
            Console.WriteLine($"[MainWindow] Error loading model: {ex.Message}");
        }
    }

    private async void AddModelButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = await BrowseForModel();
        if (path == null) return;

        _modelLibrary.Add(path);
        await SelectModelInComboBox(path);
    }

    private async Task SelectModelInComboBox(string path)
    {
        if (_modelComboBox == null)
        {
            await LoadModelFromPathAsync(path);
            return;
        }

        var target = FindModelEntryByPath(path);
        if (target == null)
        {
            await LoadModelFromPathAsync(path);
            return;
        }

        bool selectionWillChange = !ReferenceEquals(_modelComboBox.SelectedItem, target);
        _modelComboBox.SelectedItem = target;

        if (!selectionWillChange)
            await LoadModelFromPathAsync(target.Path);
    }

    private ModelEntry? FindModelEntryByPath(string path)
    {
        foreach (var item in _modelLibrary.Models)
        {
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private void RemoveModelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_modelComboBox?.SelectedItem is not ModelEntry selectedModel) return;

        _modelLibrary.Remove(selectedModel);
        _modelComboBox.SelectedItem = null;
    }

    private async void ModelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_modelComboBox?.SelectedItem is not ModelEntry selectedModel) return;

        if (System.IO.File.Exists(selectedModel.Path))
        {
            await LoadModelFromPathAsync(selectedModel.Path);
        }
        else if (_infoText != null)
        {
            _infoText.Text = $"File not found: {selectedModel.Name}";
        }
    }

    private async void LoadEnvironmentButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open HDRI Environment",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("HDRI Images")
                {
                    Patterns = new[] { "*.hdr", "*.exr", "*.jpg", "*.jpeg" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path == null || _viewport == null) return;

        try
        {
            _viewport.LoadEnvironment(path);
            if (_infoText != null)
                _infoText.Text = $"Environment: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            if (_infoText != null)
                _infoText.Text = $"Error: {ex.Message}";
            Console.WriteLine($"[MainWindow] Error loading environment: {ex.Message}");
        }
    }
}
