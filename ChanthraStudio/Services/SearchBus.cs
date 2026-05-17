using System;
using System.ComponentModel;

namespace ChanthraStudio.Services;

/// <summary>
/// Shared search-query state across the app. The title-bar TextBox sets
/// <see cref="Query"/>; any list view that wants to honour it subscribes to
/// <see cref="PropertyChanged"/> and re-filters.
///
/// Kept on the StudioContext rather than a static so unit tests can spin up
/// multiple isolated contexts if/when we add them.
/// </summary>
public sealed class SearchBus : INotifyPropertyChanged
{
    private string _query = "";

    /// <summary>Free-form filter text. Empty = no filter.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
        }
    }

    /// <summary>True iff a query is set. Cheaper than null/empty check at
    /// every list-item filter callback.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(_query);

    public event PropertyChangedEventHandler? PropertyChanged;
}
