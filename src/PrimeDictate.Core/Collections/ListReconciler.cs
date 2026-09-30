namespace PrimeDictate.Core.Collections;

public static class ListReconciler
{
    /// <summary>
    /// Makes <paramref name="target"/> hold exactly <paramref name="desired"/>, in that order, by reference, touching
    /// as little as possible (so a UI list keeps focus and scroll). An item that has to move is moved, never
    /// inserted a second time, and duplicates already in the target are dropped.
    /// </summary>
    public static void Reconcile<T>(IList<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i]))
                {
                    target.RemoveAt(j);
                    break;
                }
            }

            if (i >= target.Count)
            {
                target.Add(desired[i]);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
