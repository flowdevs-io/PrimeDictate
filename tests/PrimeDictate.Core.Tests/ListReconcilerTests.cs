using PrimeDictate.Core.Collections;

namespace PrimeDictate.Core.Tests;

public class ListReconcilerTests
{
    private sealed class Row(string name)
    {
        public string Name { get; } = name;
    }

    private static string Names(IEnumerable<Row> rows) => string.Join(",", rows.Select(r => r.Name));

    [Fact]
    public void Rows_that_change_order_move_instead_of_duplicating()
    {
        Row a = new("a"), b = new("b"), c = new("c"), d = new("d");
        var target = new System.Collections.ObjectModel.ObservableCollection<Row> { a, b, c };

        // A line from the other meeting stream lands before b, and c now sorts before b.
        ListReconciler.Reconcile(target, [a, d, c, b]);

        Assert.Equal("a,d,c,b", Names(target));
        Assert.Equal(4, target.Distinct().Count());
    }

    [Fact]
    public void Removes_missing_rows_and_repairs_existing_duplicates()
    {
        Row a = new("a"), b = new("b"), c = new("c");
        var target = new List<Row> { c, a, b, a, c };

        ListReconciler.Reconcile(target, [a, b]);
        Assert.Equal("a,b", Names(target));

        ListReconciler.Reconcile(target, []);
        Assert.Empty(target);
    }

    [Fact]
    public void Keeps_untouched_rows_untouched()
    {
        Row a = new("a"), b = new("b"), c = new("c");
        var target = new System.Collections.ObjectModel.ObservableCollection<Row> { a, b };
        var changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        ListReconciler.Reconcile(target, [a, b]);
        Assert.Equal(0, changes);

        ListReconciler.Reconcile(target, [a, b, c]);
        Assert.Equal(1, changes);
    }
}
