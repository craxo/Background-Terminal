using System.Collections.ObjectModel;

namespace Background_Terminal.Extensions;

public static class ObservableCollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        if (items == null) return;

        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}