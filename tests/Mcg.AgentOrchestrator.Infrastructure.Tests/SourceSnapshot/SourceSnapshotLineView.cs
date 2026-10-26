using System.Collections;

// Immutable line offsets share the snapshot text; line strings are sliced only on access.
internal sealed class SourceSnapshotLineView : IReadOnlyList<string>, IList<string>
{
    private readonly string text;
    private readonly int[] starts;

    public SourceSnapshotLineView(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
        var offsets = new List<int>();
        if (text.Length > 0) offsets.Add(0);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            if (index + 1 < text.Length) offsets.Add(index + 1);
        }
        offsets.Add(text.Length);
        starts = offsets.ToArray();
    }

    public int Count => starts.Length - 1;
    public bool IsReadOnly => true;

    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var start = starts[index];
            var end = starts[index + 1];
            if (end > start && text[end - 1] == '\n') end--;
            if (end > start && text[end - 1] == '\r') end--;
            return text.Substring(start, end - start);
        }
        set => throw new NotSupportedException();
    }

    public int IndexOf(string item)
    {
        for (var index = 0; index < Count; index++)
            if (string.Equals(this[index], item, StringComparison.Ordinal)) return index;
        return -1;
    }

    public bool Contains(string item) => IndexOf(item) >= 0;

    public void CopyTo(string[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (arrayIndex > array.Length || Count > array.Length - arrayIndex)
            throw new ArgumentException("The destination array has insufficient space.", nameof(array));
        for (var index = 0; index < Count; index++) array[arrayIndex + index] = this[index];
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(string item) => throw new NotSupportedException();
    public void Insert(int index, string item) => throw new NotSupportedException();
    public bool Remove(string item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
}
