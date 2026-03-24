using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Collections;
using Modular.Gui.Services;
using Modular.Sdk.Collections;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod collections management view.
/// </summary>
public partial class CollectionViewModel : ViewModelBase
{
    private readonly ModCollectionService? _service;
    private readonly ModCollectionRepository? _repository;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<ModCollection> _collections = new();

    [ObservableProperty]
    private ModCollection? _selectedCollection;

    [ObservableProperty]
    private ObservableCollection<ModCollectionEntry> _collectionEntries = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a collection or create a new one";

    [ObservableProperty]
    private string _newCollectionName = string.Empty;

    [ObservableProperty]
    private string _newCollectionGame = string.Empty;

    // Designer constructor
    public CollectionViewModel()
    {
        var sample = new ModCollection
        {
            Name = "My Skyrim Build",
            GameId = "skyrimspecialedition"
        };
        sample.Entries.Add(new ModCollectionEntry
        {
            ModId = "1234",
            Name = "Sample Mod",
            Author = "TestAuthor",
            Version = "1.0.0"
        });
        Collections.Add(sample);
    }

    // DI constructor
    public CollectionViewModel(
        NexusModsBackend backend,
        IDialogService dialogService)
    {
        _dialogService = dialogService;
        _repository = new ModCollectionRepository();
        _service = new ModCollectionService(_repository, backend);
        _ = RefreshCollectionsAsync();
    }

    partial void OnSelectedCollectionChanged(ModCollection? value)
    {
        CollectionEntries.Clear();
        if (value != null)
        {
            foreach (var entry in value.Entries)
                CollectionEntries.Add(entry);
            StatusMessage = $"{value.Name} — {value.Entries.Count} mod(s)";
        }
    }

    [RelayCommand]
    private async Task RefreshCollectionsAsync()
    {
        if (_repository == null) return;

        IsLoading = true;
        try
        {
            var list = await _repository.ListAsync();
            Collections.Clear();
            foreach (var c in list)
                Collections.Add(c);
            StatusMessage = $"{list.Count} collection(s) loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load collections: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateCollectionAsync()
    {
        if (_service == null) return;

        if (string.IsNullOrWhiteSpace(NewCollectionName) || string.IsNullOrWhiteSpace(NewCollectionGame))
        {
            StatusMessage = "Enter a name and game domain";
            return;
        }

        try
        {
            await _service.CreateAsync(NewCollectionName, NewCollectionGame);
            NewCollectionName = string.Empty;
            NewCollectionGame = string.Empty;
            await RefreshCollectionsAsync();
            StatusMessage = "Collection created";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteCollectionAsync()
    {
        if (_repository == null || SelectedCollection == null) return;

        var (_, path) = await _repository.FindByNameAsync(SelectedCollection.Name);
        if (path != null && File.Exists(path))
        {
            File.Delete(path);
            await RefreshCollectionsAsync();
            SelectedCollection = null;
            StatusMessage = "Collection deleted";
        }
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(ModCollectionEntry? entry)
    {
        if (_service == null || SelectedCollection == null || entry == null) return;

        await _service.RemoveModAsync(SelectedCollection, entry.ModId);
        CollectionEntries.Remove(entry);
        StatusMessage = $"Removed {entry.Name}";
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_service == null || SelectedCollection == null) return;

        IsLoading = true;
        StatusMessage = "Checking for updates...";

        try
        {
            var updates = await _service.CheckUpdatesAsync(SelectedCollection);
            StatusMessage = updates.Count > 0
                ? $"{updates.Count} update(s) available"
                : "All mods are up to date";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
