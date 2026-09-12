using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SRdeckPlugin.Meshtastic.ViewModels;

/// <summary>Preserves unchanged rows; large filter changes produce one reset notification.</summary>
internal sealed class MeshtasticDisplayCollection<T> : ObservableCollection<T>
{
    internal void ReplaceWith(IReadOnlyList<T> values, Func<T, T, bool>? equals = null)
    {
        equals ??= EqualityComparer<T>.Default.Equals;
        int changes = 0;
        for (int index = 0; index < values.Count; index++)
        {
            if (index < Count && equals(this[index], values[index])) continue;
            if (++changes > 64) { ResetTo(values); return; }
            if (index == Count) Add(values[index]);
            else if (index + 1 < values.Count && equals(this[index], values[index + 1]))
                Insert(index, values[index]);
            else if (index + 1 < Count && equals(this[index + 1], values[index]))
            { RemoveAt(index); index--; }
            else this[index] = values[index];
        }
        if (Count - values.Count + changes > 64) { ResetTo(values); return; }
        while (Count > values.Count) RemoveAt(Count - 1);
    }

    private void ResetTo(IReadOnlyList<T> values)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
